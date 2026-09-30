using System.Net;
using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Connectors;

/// <summary>
/// Gate 3C security over HTTP against the real local PostgreSQL: a monday account's token is accepted only into a
/// connection authorized for that account; the tenant comes only from the connection, never from the request; the
/// token's user is not an identity; the token, its short-lived credential and the Signing Secret reach no log and no
/// table; and monday's once-a-minute retries converge on one durable receipt.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MondayWebhookSecurityTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_token_of_monday_account_a_is_never_accepted_into_a_connection_of_account_b()
    {
        var connectionA = await WebhookData.NewMondayConnectionAsync(fixture, Token, account: "9001");
        var connectionB = await WebhookData.NewMondayConnectionAsync(fixture, Token, account: "9002");
        await using var host = await StartAsync();
        var marker = IngressData.NewMarker();
        var body = WebhookData.EventBody(marker, WebhookData.NewTrigger());
        var now = host.Clock.UtcNow;

        // Correctly signed, for B's own address, but naming account A: the signature alone does not suffice.
        var accountAForB = await host.PostAsync(MondayWebhookHost.PathOf(connectionB.Connection), body, WebhookTestTokens.Mint(connectionB.Connection, "9001", now));
        Assert.Equal(HttpStatusCode.Unauthorized, accountAForB.StatusCode);

        // A's own valid token replayed against B's address carries A's audience.
        var replayed = await host.PostAsync(MondayWebhookHost.PathOf(connectionB.Connection), body, WebhookTestTokens.Mint(connectionA.Connection, "9001", now));
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);

        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connectionB.Tenant, Token));
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connectionA.Tenant, Token));
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));

        // B's own account is accepted into B, and only into B.
        Assert.Equal(HttpStatusCode.OK, (await host.PostEventAsync(connectionB, body)).StatusCode);
        Assert.Single(await IngressData.ReceiptsAsync(fixture, connectionB.Tenant, Token));
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connectionA.Tenant, Token));
    }

    [Fact]
    public async Task A_tenant_named_in_the_body_the_query_or_a_header_is_never_used()
    {
        var own = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        var victim = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var host = await StartAsync();
        var forged = victim.Tenant.ToString();
        var body = WebhookData.EventBody(
            IngressData.NewMarker(),
            WebhookData.NewTrigger(),
            extraEvent: ",\"tenantId\":\"" + forged + "\",\"accountId\":9002",
            extraRoot: ",\"tenant_id\":\"" + forged + "\"");

        var response = await host.PostAsync(
            MondayWebhookHost.PathOf(own.Connection) + "?tenantId=" + forged + "&tenant=" + forged + "&connectionId=" + victim.Connection,
            body,
            WebhookTestTokens.Mint(own.Connection, own.Account, host.Clock.UtcNow),
            request =>
            {
                request.Headers.Add("X-Tenant-Id", forged);
                request.Headers.Add("X-Connection-Id", victim.Connection.ToString("D"));
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = Assert.Single(await IngressData.ReceiptsAsync(fixture, own.Tenant, Token));
        Assert.Equal(own.Tenant.Value, receipt.TenantId);
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, victim.Tenant, Token));
        Assert.Empty(await IngressData.MarkersAsync(fixture, victim.Tenant, Token));
    }

    [Fact]
    public async Task A_connection_id_of_another_tenant_only_reaches_that_tenant_with_that_tenants_own_valid_token()
    {
        var attacker = await WebhookData.NewMondayConnectionAsync(fixture, Token, account: "9001");
        var victim = await WebhookData.NewMondayConnectionAsync(fixture, Token, account: "9001");
        await using var host = await StartAsync();
        var marker = IngressData.NewMarker();
        var body = WebhookData.EventBody(marker, WebhookData.NewTrigger());

        // The attacker's valid token, sent to the victim connection's address: its audience is the attacker's.
        var response = await host.PostAsync(
            MondayWebhookHost.PathOf(victim.Connection),
            body,
            WebhookTestTokens.Mint(attacker.Connection, attacker.Account, host.Clock.UtcNow));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, victim.Tenant, Token));
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, attacker.Tenant, Token));
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));
    }

    [Fact]
    public async Task The_monday_user_in_the_token_is_not_an_identity_and_is_stored_nowhere()
    {
        const string tokenUser = "918273645";
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var host = await StartAsync();
        var usersBefore = await CountUsersAsync(connection.Tenant);

        // Another monday user than the one who authorized the connection triggered the event: still accepted.
        var response = await host.PostEventAsync(connection, WebhookData.EventBody(IngressData.NewMarker(), WebhookData.NewTrigger()), userId: tokenUser);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Equal(usersBefore, await CountUsersAsync(connection.Tenant));
        Assert.Empty(await WebhookData.FindAnywhereAsync(fixture, [tokenUser], Token));
    }

    [Fact]
    public async Task The_token_its_short_lived_credential_and_the_signing_secret_reach_no_table_and_no_log()
    {
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var host = await StartAsync();
        var shortLived = "slt-" + Guid.NewGuid().ToString("N");
        var marker = IngressData.NewMarker();
        var challenge = "challenge-" + Guid.NewGuid().ToString("N");
        var valid = WebhookTestTokens.Mint(connection.Connection, connection.Account, host.Clock.UtcNow, shortLivedToken: shortLived);
        var foreign = WebhookTestTokens.Mint(Guid.CreateVersion7(), connection.Account, host.Clock.UtcNow, shortLivedToken: shortLived);
        var path = MondayWebhookHost.PathOf(connection.Connection);
        var body = WebhookData.EventBody(marker, WebhookData.NewTrigger());

        // Every path: a challenge, an accepted event, its duplicate, a refused token, a malformed body, an oversized one.
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(path, "{\"challenge\":\"" + challenge + "\"}", valid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(path, body, valid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(path, body, "Bearer " + valid)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(path, body, foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(path, "{\"event\":{\"pulseName\":\"" + marker + "\"", valid)).StatusCode);
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (await host.PostAsync(path, "{\"event\":{\"pulseName\":\"" + marker + new string('x', MondayWebhooks.MaxBodyBytes) + "\"}}", valid)).StatusCode);

        // The body is stored as work data (the positive control); no token, credential or secret is stored anywhere.
        var found = await WebhookData.FindAnywhereAsync(fixture, [marker, valid, shortLived, WebhookTestTokens.SigningSecret, foreign], Token);
        Assert.Contains("raw_receipts.payload holds sentinel #1", found);
        Assert.Equal(["raw_receipts.payload holds sentinel #1"], found.Distinct());

        // No log of any component, at any level, carries the body, a token, the credential, the secret or the challenge.
        // The capture is not vacuous: the web server's, routing's and Entity Framework Core's own records are in it.
        var entries = host.Logs.Entries;
        Assert.Contains(entries, entry => entry.Category.StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Category.StartsWith("Microsoft.AspNetCore.Routing", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Category.StartsWith("Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Category.StartsWith("WorkContinuity.Infrastructure.Ingress", StringComparison.Ordinal));
        Assert.All(entries, entry =>
        {
            foreach (var sentinel in new[] { marker, valid, foreign, shortLived, WebhookTestTokens.SigningSecret, challenge, "Authorization", "pulseName" })
            {
                Assert.DoesNotContain(sentinel, entry.Text, StringComparison.Ordinal);
            }
        });

        // What operators need is there instead: the connection, fixed codes and outcomes.
        Assert.Contains(entries, entry => entry.Message.Contains(connection.Connection.ToString("D"), StringComparison.Ordinal)
            && entry.Message.Contains("webhook.unauthenticated", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains("acknowledged as Duplicate", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains("challenge answered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mondays_once_a_minute_retries_for_thirty_minutes_converge_on_one_durable_receipt()
    {
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var host = await StartAsync();
        var body = WebhookData.EventBody(IngressData.NewMarker(), WebhookData.NewTrigger());
        var answers = new List<HttpStatusCode>();

        // The first three attempts meet an unreadable Signing Secret; monday retries once a minute, each time with a
        // freshly issued token, for 30 minutes after the first attempt. No attempt waits on a real clock.
        host.Secrets!.Unavailable = true;
        for (var attempt = 1; attempt <= 31; attempt++)
        {
            host.Secrets.Unavailable = attempt <= 3;
            answers.Add((await host.PostEventAsync(connection, body)).StatusCode);
            host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(1);
        }

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.ServiceUnavailable, 3), .. Enumerable.Repeat(HttpStatusCode.OK, 28)], answers);
        Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
        Assert.Single(await IngressData.MarkersAsync(fixture, connection.Tenant, Token));
        Assert.Single(host.Logs.Entries, entry => entry.Message.Contains("acknowledged as Accepted", StringComparison.Ordinal));
        Assert.Equal(27, host.Logs.Entries.Count(entry => entry.Message.Contains("acknowledged as Duplicate", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_retry_whose_token_has_expired_is_refused_and_a_fresh_one_is_accepted()
    {
        var connection = await WebhookData.NewMondayConnectionAsync(fixture, Token);
        await using var host = await StartAsync();
        var body = WebhookData.EventBody(IngressData.NewMarker(), WebhookData.NewTrigger());
        var issued = WebhookTestTokens.Mint(connection.Connection, connection.Account, host.Clock.UtcNow);

        // The same token presented again ten minutes later has expired: it is not a key that opens forever.
        host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(10);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(MondayWebhookHost.PathOf(connection.Connection), body, issued)).StatusCode);
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));

        Assert.Equal(HttpStatusCode.OK, (await host.PostEventAsync(connection, body)).StatusCode);
        Assert.Single(await IngressData.ReceiptsAsync(fixture, connection.Tenant, Token));
    }

    private Task<MondayWebhookHost> StartAsync() => MondayWebhookHost.StartAsync(fixture.ApplicationConnectionString);

    private async Task<long> CountUsersAsync(TenantId tenant)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM users WHERE tenant_id = @tenant;", connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
}
