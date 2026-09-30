using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Web.Connectors;

namespace WorkContinuity.Integration.Tests.Connectors;

/// <summary>
/// Gate 3C: the real public monday webhook endpoint over HTTP, against the real local PostgreSQL through the runtime
/// role. The challenge is answered only for an active monday connection and never stored; an event is stored through
/// the Gate 2 ingress only with a valid token for its connection, and success is answered only after the durable
/// commit; every refusal stores nothing and answers with a fixed, content-free error.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MondayWebhookEndpointTests(DatabaseFixture fixture)
{
    private const string Challenge = "3eZbrw1aBm2rZgRNFdxV2595E9CY3gmdALWMmHkvFXO7tYXAYM8P";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_challenge_for_an_active_monday_connection_is_echoed_needs_no_token_and_stores_nothing()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();

        var response = await host.PostAsync(MondayWebhookHost.PathOf(connection.Connection), "{\"challenge\":\"" + Challenge + "\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"challenge\":\"" + Challenge + "\"}", await response.Content.ReadAsStringAsync(Token));
        AssertHygienicHeaders(response);

        // The challenge proves the address only: no token, no Signing Secret, no receipt, no outbox marker.
        Assert.Equal(0, host.Secrets!.Reads);
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Empty(await IngressData.MarkersAsync(fixture, connection.Tenant, Token));
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, Challenge, Token));
    }

    [Fact]
    public async Task A_challenge_ignores_a_tenant_in_the_query_and_any_authorization_header()
    {
        var connection = await NewConnectionAsync();
        var other = await NewConnectionAsync();
        await using var host = await StartAsync();

        var response = await host.PostAsync(
            MondayWebhookHost.PathOf(connection.Connection) + "?tenantId=" + other.Tenant + "&tenant_id=" + other.Tenant,
            "{\"challenge\":\"" + Challenge + "\"}",
            "not-a-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"challenge\":\"" + Challenge + "\"}", await response.Content.ReadAsStringAsync(Token));
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, other.Tenant, Token));
    }

    [Fact]
    public async Task A_challenge_for_an_unknown_disabled_or_foreign_connection_is_not_found()
    {
        var disconnected = await NewConnectionAsync();
        await WebhookData.DisconnectAsync(fixture, disconnected, Token);
        var (_, synthetic) = await IngressData.NewConnectionAsync(fixture, Token);
        var active = await NewConnectionAsync();
        await using var host = await StartAsync();
        var challenge = "{\"challenge\":\"" + Challenge + "\"}";

        foreach (var path in new[]
                 {
                     MondayWebhookHost.PathOf(Guid.CreateVersion7()),
                     MondayWebhookHost.PathOf(disconnected.Connection),
                     MondayWebhookHost.PathOf(synthetic),
                     MondayWebhookHost.PathOf(active.Connection).ToUpperInvariant().Replace("/API/CONNECTORS/MONDAY/WEBHOOKS/", "/api/connectors/monday/webhooks/", StringComparison.Ordinal),
                     "/api/connectors/monday/webhooks/" + active.Connection.ToString("N"),
                     "/api/connectors/monday/webhooks/not-a-connection",
                     "/api/connectors/monday/webhooks/" + Guid.Empty.ToString("D"),
                 })
        {
            var response = await host.PostAsync(path, challenge);

            await AssertRefusedAsync(response, HttpStatusCode.NotFound, MondayWebhookErrors.NotFound);
        }
    }

    [Theory]
    [InlineData("""{"challenge":"abc","tenantId":"0191f3a0-7a44-7c1b-9d2e-3f5a6b7c8d9e"}""")]
    [InlineData("""{"challenge":"abc","event":{"type":"create_item"}}""")]
    [InlineData("""{"challenge":null}""")]
    [InlineData("""{"challenge":"has space"}""")]
    public async Task A_malformed_challenge_is_refused_and_stores_nothing(string body)
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();

        await AssertRefusedAsync(await host.PostAsync(MondayWebhookHost.PathOf(connection.Connection), body), HttpStatusCode.BadRequest, MondayWebhookErrors.Malformed);
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
    }

    [Fact]
    public async Task An_oversized_challenge_is_refused()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();

        var response = await host.PostAsync(MondayWebhookHost.PathOf(connection.Connection), "{\"challenge\":\"" + new string('a', 257) + "\"}");

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, MondayWebhookErrors.Malformed);
    }

    [Fact]
    public async Task A_challenge_that_cannot_be_checked_is_answered_so_that_monday_retries()
    {
        await using var host = await MondayWebhookHost.StartAsync(UnreachableDatabase);

        var response = await host.PostAsync(MondayWebhookHost.PathOf(Guid.CreateVersion7()), "{\"challenge\":\"" + Challenge + "\"}");

        await AssertRefusedAsync(response, HttpStatusCode.ServiceUnavailable, MondayWebhookErrors.Unavailable);
    }

    [Fact]
    public async Task An_authenticated_event_is_stored_exactly_as_received_before_success_is_answered()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();
        var marker = IngressData.NewMarker();
        var trigger = WebhookData.NewTrigger();
        var body = WebhookData.EventBody(marker, trigger);

        var response = await host.PostEventAsync(connection, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Token));
        AssertHygienicHeaders(response);

        // By the time the answer arrives, the receipt and its outbox marker are committed, in the connection's tenant.
        var receipt = Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Equal(connection.Tenant.Value, receipt.TenantId);
        Assert.Equal("monday", receipt.SourceSystem);
        Assert.Equal(trigger, receipt.SourceEventId);
        Assert.Equal(MondayWebhooks.SchemaVersion, receipt.SchemaVersion);
        Assert.Equal(Sha256Hex(body), receipt.PayloadFingerprint);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse(receipt.Payload!)));

        // monday's own times and causation stay in the stored body for normalization; nothing was derived from them.
        var stored = JsonNode.Parse(receipt.Payload!)!["event"]!;
        Assert.Equal(1727697599.4971m, stored["changedAt"]!.GetValue<decimal>());
        Assert.Equal("2026-09-30T11:59:59.512Z", (string?)stored["triggerTime"]);
        Assert.Null(stored["originalTriggerUuid"]);

        var outbox = Assert.Single(await IngressData.MarkersAsync(fixture, connection.Tenant, Token));
        Assert.Equal(IngressMessageTypes.ReceiptReceived, outbox.MessageType);
        Assert.Equal(response.Headers.GetValues("X-Correlation-Id").Single(), outbox.CorrelationId);
    }

    [Fact]
    public async Task Success_is_answered_only_after_the_ingress_outcome_is_settled()
    {
        var connection = await NewConnectionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartAsync(configure: services => GatedIngress.Install(services, entered, release));
        var marker = IngressData.NewMarker();

        var pending = host.PostEventAsync(connection, WebhookData.EventBody(marker, WebhookData.NewTrigger()));
        await entered.Task.WaitAsync(Token);

        // The endpoint is waiting on ingress: no answer and no receipt yet.
        Assert.False(pending.IsCompleted);
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));

        release.SetResult();
        var response = await pending;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
    }

    [Fact]
    public async Task A_missing_invalid_or_foreign_token_is_refused_before_the_database_is_touched()
    {
        var connection = await NewConnectionAsync();
        var other = await NewConnectionAsync();
        await using var host = await StartAsync();
        var marker = IngressData.NewMarker();
        var body = WebhookData.EventBody(marker, WebhookData.NewTrigger());
        var now = host.Clock.UtcNow;
        var valid = WebhookTestTokens.Mint(connection.Connection, connection.Account, now);
        var signature = valid[(valid.LastIndexOf('.') + 1)..];
        var otherSignature = (signature[0] == 'A' ? "B" : "A") + signature[1..];

        foreach (var authorization in new string?[]
                 {
                     null,
                     string.Empty,
                     "Bearer",
                     "Bearer ",
                     WebhookTestTokens.Mint(connection.Connection, connection.Account, now, secret: WebhookTestTokens.OtherSecret),
                     valid[..(valid.LastIndexOf('.') + 1)] + otherSignature,
                     WebhookTestTokens.Mint(connection.Connection, connection.Account, now.AddMinutes(-10)),
                     WebhookTestTokens.Mint(other.Connection, connection.Account, now),
                     WebhookTestTokens.WithClaims(valid, "{\"accountId\":9002,\"aud\":\"" + WebhookTestTokens.Audience(connection.Connection) + "\",\"exp\":4102444800}"),
                     "abc.def",
                     "Basic " + valid,
                     "bearer " + valid,
                     "Bearer  " + valid,
                     valid + ", " + valid,
                 })
        {
            var response = await host.PostAsync(MondayWebhookHost.PathOf(connection.Connection), body, authorization);

            await AssertRefusedAsync(response, HttpStatusCode.Unauthorized, MondayWebhookErrors.Unauthenticated);
        }

        // Two Authorization headers are ambiguous, even when one of them is valid.
        var doubled = await host.PostAsync(
            MondayWebhookHost.PathOf(connection.Connection),
            body,
            valid,
            request => request.Headers.TryAddWithoutValidation("Authorization", valid));
        await AssertRefusedAsync(doubled, HttpStatusCode.Unauthorized, MondayWebhookErrors.Unauthenticated);

        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));

        // None of these requests reached the database: no database context was even created.
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_readable_signing_secret_or_a_base_address_events_are_refused_retryably_and_challenges_still_answered()
    {
        var connection = await NewConnectionAsync();
        var marker = IngressData.NewMarker();
        var body = WebhookData.EventBody(marker, WebhookData.NewTrigger());

        await using (var unreadable = await StartAsync())
        {
            unreadable.Secrets!.Unavailable = true;
            await AssertRefusedAsync(await unreadable.PostEventAsync(connection, body), HttpStatusCode.ServiceUnavailable, MondayWebhookErrors.Unavailable);
        }

        await using (var unconfigured = await StartAsync(configured: false))
        {
            await AssertRefusedAsync(await unconfigured.PostEventAsync(connection, body), HttpStatusCode.ServiceUnavailable, MondayWebhookErrors.Unavailable);
        }

        // As composed today, with no approved secret store: challenges are answered and every event is refused.
        await using (var production = await StartAsync(withSecretStore: false))
        {
            await AssertRefusedAsync(await production.PostEventAsync(connection, body), HttpStatusCode.ServiceUnavailable, MondayWebhookErrors.Unavailable);
            Assert.Equal(HttpStatusCode.OK, (await production.PostAsync(MondayWebhookHost.PathOf(connection.Connection), "{\"challenge\":\"" + Challenge + "\"}")).StatusCode);
        }

        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));
    }

    [Fact]
    public async Task A_repeated_delivery_with_the_same_trigger_uuid_is_acknowledged_and_stored_once()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();
        var body = WebhookData.EventBody(IngressData.NewMarker(), WebhookData.NewTrigger());

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.PostEventAsync(connection, body)).StatusCode);
        }

        Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Single(await IngressData.MarkersAsync(fixture, connection.Tenant, Token));
    }

    [Fact]
    public async Task The_same_trigger_uuid_with_altered_content_keeps_the_first_receipt_and_is_not_silent()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();
        var trigger = WebhookData.NewTrigger();
        var (first, second) = (IngressData.NewMarker(), IngressData.NewMarker());

        Assert.Equal(HttpStatusCode.OK, (await host.PostEventAsync(connection, WebhookData.EventBody(first, trigger))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostEventAsync(connection, WebhookData.EventBody(second, trigger))).StatusCode);

        var receipt = Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Equal(Sha256Hex(WebhookData.EventBody(first, trigger)), receipt.PayloadFingerprint);
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, second, Token));
        Assert.Contains(host.Logs.Entries, entry => entry.Message.Contains("different payload fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delivery_without_a_trigger_uuid_is_kept_each_time_as_a_duplicate_candidate_and_never_dropped()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();
        var body = WebhookData.EventBody(IngressData.NewMarker(), triggerUuid: null);

        Assert.Equal(HttpStatusCode.OK, (await host.PostEventAsync(connection, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostEventAsync(connection, body)).StatusCode);

        var receipts = await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token);
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, receipt =>
        {
            Assert.Null(receipt.SourceEventId);
            Assert.Equal(Sha256Hex(body), receipt.PayloadFingerprint);
        });
    }

    [Fact]
    public async Task A_disabled_connection_a_foreign_source_or_a_suspended_tenant_is_refused_without_storing()
    {
        var disconnected = await NewConnectionAsync();
        await WebhookData.DisconnectAsync(fixture, disconnected, Token);
        var (syntheticTenant, synthetic) = await IngressData.NewConnectionAsync(fixture, Token);
        var suspended = await NewConnectionAsync();
        await WebhookData.ExecuteAsAdministratorAsync(fixture, "UPDATE tenants SET status = 'Suspended' WHERE tenant_id = @id;", suspended.Tenant.Value, Token);
        await using var host = await StartAsync();
        var marker = IngressData.NewMarker();
        var body = WebhookData.EventBody(marker, WebhookData.NewTrigger());

        await AssertRefusedAsync(await host.PostEventAsync(disconnected, body), HttpStatusCode.NotFound, MondayWebhookErrors.NotFound);
        await AssertRefusedAsync(
            await host.PostAsync(MondayWebhookHost.PathOf(synthetic), body, WebhookTestTokens.Mint(synthetic, "9001", host.Clock.UtcNow)),
            HttpStatusCode.NotFound,
            MondayWebhookErrors.NotFound);
        await AssertRefusedAsync(await host.PostEventAsync(suspended, body), HttpStatusCode.NotFound, MondayWebhookErrors.NotFound);

        Assert.Empty(await IngressData.ReceiptsAsync(fixture, disconnected.Tenant, Token));
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, syntheticTenant, Token));
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, suspended.Tenant, Token));
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));
    }

    [Fact]
    public async Task An_authorization_requiring_reauthorization_or_whose_user_left_is_refused_without_storing()
    {
        var reauthorization = await NewConnectionAsync();
        await WebhookData.RequireReauthorizationAsync(fixture, reauthorization, Token);
        var departed = await NewConnectionAsync();
        await WebhookData.ExecuteAsAdministratorAsync(fixture, "UPDATE users SET is_active = false WHERE user_id = @id;", departed.User.Value, Token);
        await using var host = await StartAsync();
        var marker = IngressData.NewMarker();
        var body = WebhookData.EventBody(marker, WebhookData.NewTrigger());

        await AssertRefusedAsync(await host.PostEventAsync(reauthorization, body), HttpStatusCode.Unauthorized, MondayWebhookErrors.Unauthenticated);
        await AssertRefusedAsync(await host.PostEventAsync(departed, body), HttpStatusCode.Unauthorized, MondayWebhookErrors.Unauthenticated);

        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));
    }

    [Fact]
    public async Task A_body_that_ingress_cannot_store_is_refused_as_malformed()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();
        var marker = IngressData.NewMarker();

        var response = await host.PostEventAsync(connection, WebhookData.EventBody(marker + "\\u0000", WebhookData.NewTrigger()));

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, MondayWebhookErrors.Malformed);
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));
    }

    [Fact]
    public async Task Requests_that_are_not_uncompressed_utf8_json_or_exceed_the_size_limit_are_refused_before_being_read()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();
        var path = MondayWebhookHost.PathOf(connection.Connection);
        var marker = IngressData.NewMarker();
        var body = WebhookData.EventBody(marker, WebhookData.NewTrigger());
        var token = WebhookTestTokens.Mint(connection.Connection, connection.Account, host.Clock.UtcNow);

        foreach (var mediaType in new[] { "text/plain", "application/json; charset=iso-8859-1", "application/x-www-form-urlencoded", "application/jsonx" })
        {
            var response = await host.PostAsync(path, body, token, request => request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType));
            await AssertRefusedAsync(response, HttpStatusCode.UnsupportedMediaType, MondayWebhookErrors.UnsupportedMediaType);
        }

        await AssertRefusedAsync(
            await host.PostAsync(path, body, token, request => request.Content!.Headers.ContentType = null),
            HttpStatusCode.UnsupportedMediaType,
            MondayWebhookErrors.UnsupportedMediaType);
        await AssertRefusedAsync(
            await host.PostAsync(path, body, token, request => request.Content!.Headers.ContentEncoding.Add("gzip")),
            HttpStatusCode.UnsupportedMediaType,
            MondayWebhookErrors.UnsupportedMediaType);

        var tooLarge = SizedEvent(marker, MondayWebhooks.MaxBodyBytes + 1);
        await AssertRefusedAsync(await host.PostAsync(path, tooLarge, token), HttpStatusCode.RequestEntityTooLarge, MondayWebhookErrors.TooLarge);

        // Without a declared length the body is read to the limit and refused whole, never truncated.
        var chunked = await host.PostAsync(path, string.Empty, token, request =>
        {
            request.Content = new StreamContent(new WebhookData.UnknownLengthStream(Encoding.UTF8.GetBytes(tooLarge)));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        });
        await AssertRefusedAsync(chunked, HttpStatusCode.RequestEntityTooLarge, MondayWebhookErrors.TooLarge);

        await AssertRefusedAsync(await host.PostAsync(path, "{\"event\":", token), HttpStatusCode.BadRequest, MondayWebhookErrors.Malformed);
        await AssertRefusedAsync(await host.PostAsync(path, "[]", token), HttpStatusCode.BadRequest, MondayWebhookErrors.Malformed);

        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));

        // Exactly at the limit, the delivery is stored whole.
        var atLimit = SizedEvent(marker, MondayWebhooks.MaxBodyBytes);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(path, atLimit, token)).StatusCode);
        Assert.Equal(Sha256Hex(atLimit), Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token)).PayloadFingerprint);
    }

    [Fact]
    public async Task Without_a_server_body_limit_the_endpoint_still_reads_no_more_than_its_own_bound()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync(hideServerBodyLimit: true);
        var path = MondayWebhookHost.PathOf(connection.Connection);
        var marker = IngressData.NewMarker();
        var token = WebhookTestTokens.Mint(connection.Connection, connection.Account, host.Clock.UtcNow);
        var tooLarge = SizedEvent(marker, MondayWebhooks.MaxBodyBytes + 1);

        // Chunked, so no declared length refuses it early, and no server limit applies: only the endpoint's own
        // bound stops the read, and the body is refused whole rather than read on or truncated.
        var chunked = await host.PostAsync(path, string.Empty, token, request =>
        {
            request.Content = new StreamContent(new WebhookData.UnknownLengthStream(Encoding.UTF8.GetBytes(tooLarge)));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        });

        await AssertRefusedAsync(chunked, HttpStatusCode.RequestEntityTooLarge, MondayWebhookErrors.TooLarge);
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));

        // Exactly at the bound, the delivery is still read and stored whole.
        var atLimit = SizedEvent(marker, MondayWebhooks.MaxBodyBytes);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(path, atLimit, token)).StatusCode);
        Assert.Equal(Sha256Hex(atLimit), Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token)).PayloadFingerprint);
    }

    [Fact]
    public async Task Only_post_is_served_on_the_webhook_route()
    {
        var connection = await NewConnectionAsync();
        await using var host = await StartAsync();

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, MondayWebhookHost.PathOf(connection.Connection));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Client.SendAsync(request, Token)).StatusCode);
        }
    }

    [Fact]
    public async Task Every_ingress_outcome_maps_to_its_answer_and_only_an_acknowledged_one_is_success()
    {
        var connection = Guid.CreateVersion7();
        var outcome = IngressOutcome.Failed(CorrelationId.New());
        await using var host = await StartAsync(configure: services => StubIngress.Install(services, () => outcome));
        var token = WebhookTestTokens.Mint(connection, "9001", host.Clock.UtcNow);
        var body = WebhookData.EventBody(IngressData.NewMarker(), WebhookData.NewTrigger());

        async Task<HttpStatusCode> AnswerFor(IngressOutcome settled)
        {
            outcome = settled;
            return (await host.PostAsync(MondayWebhookHost.PathOf(connection), body, token)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await AnswerFor(IngressOutcome.Accepted(Guid.CreateVersion7(), CorrelationId.New())));
        Assert.Equal(HttpStatusCode.OK, await AnswerFor(IngressOutcome.Duplicate(Guid.CreateVersion7(), CorrelationId.New())));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await AnswerFor(IngressOutcome.Failed(CorrelationId.New())));

        foreach (var rejection in IngressErrors.Rejections)
        {
            var expected = rejection == IngressErrors.SourceNotVerified ? HttpStatusCode.Unauthorized
                : rejection == IngressErrors.UnknownConnection || rejection == IngressErrors.TenantInactive || rejection == IngressErrors.SourceMismatch
                    ? HttpStatusCode.NotFound
                    : HttpStatusCode.BadRequest;

            Assert.Equal(expected, await AnswerFor(IngressOutcome.Rejected(rejection, CorrelationId.New())));
        }
    }

    [Fact]
    public async Task An_ingress_failure_is_answered_retryably_and_the_retry_converges_on_one_receipt()
    {
        var connection = await NewConnectionAsync();
        var body = WebhookData.EventBody(IngressData.NewMarker(), WebhookData.NewTrigger());

        await using (var down = await MondayWebhookHost.StartAsync(UnreachableDatabase))
        {
            await AssertRefusedAsync(await down.PostEventAsync(connection, body), HttpStatusCode.ServiceUnavailable, MondayWebhookErrors.Unavailable);
        }

        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));

        await using var up = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await up.PostEventAsync(connection, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await up.PostEventAsync(connection, body)).StatusCode);
        Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
    }

    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=2";

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string SizedEvent(string marker, int bytes)
    {
        var trigger = WebhookData.NewTrigger();
        var padding = bytes - Encoding.UTF8.GetByteCount(WebhookData.EventBody(marker, trigger));
        var body = WebhookData.EventBody(marker + new string('x', padding), trigger);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(body));
        return body;
    }

    private static void AssertHygienicHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
    }

    private async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, Error error)
    {
        Assert.Equal(status, response.StatusCode);
        AssertHygienicHeaders(response);

        // A fixed code, a fixed message and the correlation id: nothing from the request.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(["code", "message", "correlationId"], body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(error.Code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(error.Message, body.RootElement.GetProperty("message").GetString());
        Assert.Equal(response.Headers.GetValues("X-Correlation-Id").Single(), body.RootElement.GetProperty("correlationId").GetString());
    }

    private Task<MondayWebhookHost> StartAsync(
        bool configured = true,
        bool withSecretStore = true,
        Action<IServiceCollection>? configure = null,
        bool hideServerBodyLimit = false) =>
        MondayWebhookHost.StartAsync(fixture.ApplicationConnectionString, configured, withSecretStore, configure, hideServerBodyLimit);

    private Task<SeededConnection> NewConnectionAsync(string account = "9001") =>
        WebhookData.NewMondayConnectionAsync(fixture, Token, account);

    /// <summary>The real ingress behind a gate the test opens, announcing when a delivery reaches it.</summary>
    private sealed class GatedIngress(IEventIngress inner, TaskCompletionSource entered, TaskCompletionSource release) : IEventIngress
    {
        public static void Install(IServiceCollection services, TaskCompletionSource entered, TaskCompletionSource release)
        {
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(IEventIngress));
            services.Remove(real);
            services.AddScoped<IEventIngress>(provider =>
                new GatedIngress((IEventIngress)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), entered, release));
        }

        public async Task<IngressOutcome> SubmitAsync(IngressSubmission submission, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return await inner.SubmitAsync(submission, cancellationToken);
        }
    }

    /// <summary>An ingress that settles every delivery as the test says, storing nothing.</summary>
    private sealed class StubIngress(Func<IngressOutcome> outcome) : IEventIngress
    {
        public static void Install(IServiceCollection services, Func<IngressOutcome> outcome)
        {
            services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IEventIngress)));
            services.AddScoped<IEventIngress>(_ => new StubIngress(outcome));
        }

        public Task<IngressOutcome> SubmitAsync(IngressSubmission submission, CancellationToken cancellationToken = default) =>
            Task.FromResult(outcome());
    }
}
