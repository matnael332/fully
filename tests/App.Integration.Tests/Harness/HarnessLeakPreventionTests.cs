using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Diagnostics;
using WorkContinuity.Harness.Monday.Hosting;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.InitialSync;
using WorkContinuity.Integration.Tests.Reconciliation;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, offline slice: nothing secret leaves the harness. A whole synthetic flow is run with sentinels for every
/// credential and callback secret, and the harness's log pipeline, its operator output, its diagnostics, the activities
/// and metrics the process emits, and every text column of the database are searched for them. Positive controls show
/// that each search can find what is there, and that the protection is the harness's log policy, not a quiet level.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessLeakPreventionTests(DatabaseFixture fixture)
{
    private const string Board = "1771812698";
    private const string ItemA = "1772099001";
    private const string ItemB = "1772099002";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_credential_callback_secret_cursor_or_provider_text_reaches_a_log_the_console_diagnostics_telemetry_or_the_database()
    {
        using var telemetry = new TelemetryCapture();
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var secrets = Assert.IsType<MemorySecrets>(harness.Secrets);
        var at = ReconciliationData.Seconds(await ReconciliationData.DatabaseNowAsync(fixture)).AddMinutes(-10);
        harness.Account1.AddBoard(Board);
        harness.Account1.AddItem(ItemA, Board, "Working on it", at);
        harness.Account1.AddItem(ItemB, Board, "Stuck", at);
        harness.Account1.PageSize = 1;

        // The whole flow. The callback also carries a parameter the harness does not know.
        var consoleAnswer = await harness.ConsoleAsync("authorize");
        var startAddress = consoleAnswer.Split('\n', StringSplitOptions.TrimEntries).Single(line => line.StartsWith("https://", StringComparison.Ordinal));
        var capability = new Uri(startAddress).Query[3..];
        using var started = await harness.GetOperatorAsync(new Uri(startAddress).PathAndQuery);
        var state = HarnessTestHost.QueryValue(started.Headers.Location!, "state");
        var binding = HarnessTestHost.BindingOf(started);
        var verifierName = Assert.Single(secrets.Names, name => name.StartsWith("wce-monday-pkce-", StringComparison.Ordinal));
        string verifier;
        using (var stored = System.Text.Json.JsonDocument.Parse(secrets.Text(verifierName)))
        {
            verifier = stored.RootElement.GetProperty("verifier").GetString()!;
        }

        var access = harness.ScriptGrant();
        var unknown = $"&{HarnessSentinels.UnknownCallbackName}={HarnessSentinels.UnknownCallbackValue}";
        using (var callback = await harness.CallbackAsync(state, HarnessSentinels.Code, binding, unknown))
        {
            Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);
        }

        var connection = Assert.Single(await harness.ConnectionsAsync());
        await harness.ConsoleAsync($"scope {connection:D} {Board} enable");
        await harness.DeliverAsync(connection, ItemA, "Stuck", Board);
        await harness.RelayAndPumpAsync();
        await harness.ConsoleAsync($"sync-start {connection:D}");
        Assert.Contains("state=Live", await harness.ConsoleAsync($"sync-run {connection:D}"), StringComparison.Ordinal);
        await harness.ConsoleAsync($"reconcile {connection:D}");
        harness.Account1.SetStatus(ItemA, "Review ready", at.AddMinutes(5));
        await harness.DeliverAsync(connection, ItemA, "Review ready", Board);
        await harness.RelayAndPumpAsync();
        await harness.ConsoleAsync($"reconcile {connection:D}");
        await harness.TickCleanupAsync();
        var currentValues = (await harness.Host.Operator.ReadCurrentWorkAsync(Token)).Value.Fields.Select(field => field.Value).Order(StringComparer.Ordinal).ToList();
        foreach (var command in new[] { "cleanup", "relay", "work", "evidence", "diagnostics", $"sweep {connection:D}", $"disconnect {connection:D}" })
        {
            await harness.ConsoleAsync(command);
        }

        // A refused delivery and a refused callback pass through the same pipelines.
        using (var refused = await harness.PostWebhookAsync(connection, "{}", "Bearer " + HarnessSentinels.ShortLivedToken))
        {
            Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        }

        using (var refused = await harness.CallbackAsync(state, HarnessSentinels.Code, binding))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var cursor = Assert.Single(harness.Account1.IssuedCursors);
        var webhookToken = harness.PresentedWebhookTokens[0];
        var secret = new (string Name, string Value)[]
        {
            ("client credential", HarnessSentinels.ClientCredential),
            ("Signing Secret", WebhookTestTokens.SigningSecret),
            ("authorization code", HarnessSentinels.Code),
            ("state", state),
            ("state secret", state[33..]),
            ("PKCE verifier", verifier),
            ("access token", access),
            ("access token claims", access.Split('.')[1]),
            ("refresh token", HarnessSentinels.RefreshToken),
            ("browser binding", binding),
            ("webhook token", webhookToken),
            ("webhook token claims", webhookToken.Split('.')[1]),
            ("webhook token signature", webhookToken.Split('.')[2]),
            ("short-lived token", HarnessSentinels.ShortLivedToken),
            ("unknown callback value", HarnessSentinels.UnknownCallbackValue),
            ("unknown callback name", HarnessSentinels.UnknownCallbackName),
            ("cursor", cursor),
            ("provider board title", FakeMondayWorkspace.BoardTitle),
            ("provider item name", FakeMondayWorkspace.ItemName),
        };

        // Logs: everything the harness's pipeline let through.
        var logs = harness.Logs.Text;
        AssertAbsent(secret.Append(("start capability", capability)).Append(("business body marker", HarnessSentinels.BodyMarker)), logs, "the harness log");

        // Positive control: the pipeline is live. The product's own categories and the harness's are there, with ids and
        // fixed codes, and no framework category is.
        Assert.Contains(harness.Logs.Records, record => record.Category == "WorkContinuity.Infrastructure.Ingress.EventIngressService" && record.Message.Contains(connection.ToString("D"), StringComparison.Ordinal));
        Assert.Contains(harness.Logs.Records, record => record.Category == "WorkContinuity.Infrastructure.Transport.OutboxRelay" && record.Message.Contains(harness.Tenant.ToString(), StringComparison.Ordinal));
        Assert.Contains(harness.Logs.Records, record => record.Category == "WorkContinuity.Harness.Monday.Operator" && record.Message.Contains("BindingMismatch", StringComparison.Ordinal));
        Assert.Contains(harness.Logs.Records, record => record.Category == "WorkContinuity.Harness.Monday.VerifierCleanup");
        Assert.All(harness.Logs.Records, record => Assert.StartsWith("WorkContinuity.", record.Category, StringComparison.Ordinal));

        // Operator output. The start capability is in it once, by design, inside the address the operator opens.
        var transcript = harness.Transcript;
        AssertAbsent(secret.Append(("business body marker", HarnessSentinels.BodyMarker)), transcript, "the operator console");
        Assert.Equal(1, Occurrences(transcript, capability));
        foreach (var value in new[] { "Working on it", "Stuck", "Review ready" })
        {
            Assert.DoesNotContain(value, transcript, StringComparison.Ordinal);
        }

        // Positive control: those values were current work, as the reader returned them to its caller.
        Assert.Equal(["Review ready", "Stuck"], currentValues);

        // Diagnostics: closed names and numbers.
        var diagnostics = string.Join('\n', harness.Host.Diagnostics.Render());
        AssertAbsent(secret.Append(("start capability", capability)).Append(("business body marker", HarnessSentinels.BodyMarker)), diagnostics, "the diagnostics");
        var snapshot = harness.Host.Diagnostics.Snapshot();
        Assert.Equal(1, snapshot.CallbackParameters[CallbackParameter.Other]);
        Assert.Equal(2, snapshot.CallbackParameters[CallbackParameter.State]);
        Assert.Equal(2, snapshot.CallbackParameters[CallbackParameter.Code]);
        Assert.Equal(1, snapshot.CallbackIssuers[IssuerRelation.Absent]);

        // Telemetry: every activity and every metric measurement the process emitted while the flow ran.
        var emitted = telemetry.Text;
        AssertAbsent(secret.Append(("start capability", capability)).Append(("business body marker", HarnessSentinels.BodyMarker)), emitted, "the process telemetry");

        // Positive controls: the capture sees a tag it was given; the framework's request activities of the harness's
        // own listeners were observed, the webhook listener's with the correlation tag the product sets on them; and so
        // were the Event Backbone's metrics.
        Assert.Contains(TelemetryCapture.ControlValue, emitted, StringComparison.Ordinal);
        Assert.Contains(telemetry.Activities, activity => activity.StartsWith("Microsoft.AspNetCore|Microsoft.AspNetCore.Hosting.HttpRequestIn|", StringComparison.Ordinal));
        Assert.Contains(telemetry.Activities, activity => activity.StartsWith("Microsoft.AspNetCore|", StringComparison.Ordinal) && activity.Contains(" wce.correlation_id=", StringComparison.Ordinal));
        Assert.Contains(telemetry.Instruments, instrument => instrument.StartsWith(EventBackboneMetrics.MeterName + "/", StringComparison.Ordinal));

        // The database: no column of any table holds a credential, a callback secret, a cursor or provider text.
        var found = await WebhookData.FindAnywhereAsync(
            fixture,
            [HarnessSentinels.BodyMarker, .. secret.Select(entry => entry.Value), capability],
            Token);

        // A distinct assertion for the business body: it is in its authorized raw receipt, which also proves the search
        // can find what is there, and it is nowhere else.
        Assert.Contains("raw_receipts.payload holds sentinel #1", found);
        Assert.Equal(["raw_receipts.payload holds sentinel #1"], found.Where(finding => finding.EndsWith(" #1", StringComparison.Ordinal)));
        Assert.DoesNotContain(found, finding => !finding.EndsWith(" #1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_frameworks_own_request_log_would_write_a_callbacks_query_string_and_the_harness_pipeline_has_no_such_category()
    {
        const string Sentinel = "SENTINEL-control-callback-code";

        // The hazard, shown on a plain host with the framework's logging at its most verbose: the request log writes the
        // whole query string. This is what a callback's code and state would meet.
        var framework = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter((_, _) => true);
        builder.Logging.AddProvider(framework);

        // On such a host a level is only a setting: these three lines of configuration are all it takes to turn the
        // request log on, whatever an appsettings file next to the tests says.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
        });
        await using (var plain = builder.Build())
        {
            plain.MapGet("/operator/callback", (RequestDelegate)(context => context.Response.WriteAsync("ok")));
            await plain.StartAsync(Token);
            var address = plain.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var answered = await client.GetAsync("/operator/callback?code=" + Sentinel + "&state=s", Token);
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
            await plain.StopAsync(Token);
        }

        Assert.Contains(framework.Entries, entry => entry.Category.StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal) && entry.Text.Contains(Sentinel, StringComparison.Ordinal));

        // The harness: the same kind of request, through its own listeners, with its sink keeping every level. No
        // framework category exists in its pipeline at all, so there is no level to raise.
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        using (var refused = await harness.GetOperatorAsync("/operator/callback?code=" + Sentinel + "&state=s"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var missing = await harness.WebhookClient.GetAsync("/missing?code=" + Sentinel, Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        Assert.DoesNotContain(Sentinel, harness.Logs.Text, StringComparison.Ordinal);
        Assert.NotEmpty(harness.Logs.Records);
        Assert.All(harness.Logs.Records, record => Assert.StartsWith("WorkContinuity.", record.Category, StringComparison.Ordinal));
        Assert.Contains(harness.Logs.Records, record => record.Level < LogLevel.Information || record.Category.StartsWith("WorkContinuity.Harness.Monday", StringComparison.Ordinal));
    }

    [Fact]
    public void The_logger_factory_passes_only_allowed_categories_and_never_an_exceptions_text()
    {
        const string Sentinel = "SENTINEL-inside-an-exception";
        var tenant = TenantId.New();
        var sink = new CapturingHarnessSink();
        var diagnostics = new HarnessDiagnostics();
        using var factory = new HarnessLoggerFactory(sink, diagnostics, tenant, new TenantContextAccessor());

        // Framework and library categories get a logger that keeps nothing, at any level.
        foreach (var category in new[]
                 {
                     "Microsoft.AspNetCore.Hosting.Diagnostics",
                     "Microsoft.AspNetCore.Routing.EndpointMiddleware",
                     "Microsoft.AspNetCore.Server.Kestrel",
                     "Microsoft.EntityFrameworkCore.Database.Command",
                     "Microsoft.Hosting.Lifetime",
                     "Npgsql.Command",
                     "Azure.Messaging.ServiceBus",
                     "System.Net.Http.HttpClient",
                     "workcontinuity.lowercase",
                     "WorkContinuityButNotTheNamespace",
                     string.Empty,
                 })
        {
            var silenced = factory.CreateLogger(category);
            Assert.False(silenced.IsEnabled(LogLevel.Critical));
            silenced.LogCritical(new InvalidOperationException(Sentinel), "Request starting {Target}", Sentinel);
        }

        Assert.Empty(sink.Records);
        Assert.False(HarnessLogPolicy.Allows(null));

        // An allowed category's record of the configured tenant keeps its rendered message and, of an exception, only the
        // type name.
        var allowed = factory.CreateLogger("WorkContinuity.Infrastructure.Persistence.TenantScopedDatabase");
        allowed.LogWarning(
            new InvalidOperationException("duplicate key value violates unique constraint; Key (value)=(" + Sentinel + ") already exists"),
            "Persistence write rejected for tenant {TenantId}: {ErrorCode}",
            tenant,
            "persistence.duplicate");
        using (allowed.BeginScope("scope " + Sentinel))
        {
            allowed.LogInformation("Inside a scope, for tenant {TenantId}.", tenant);
        }

        Assert.Equal(2, sink.Records.Count);
        Assert.Equal(
            ("WorkContinuity.Infrastructure.Persistence.TenantScopedDatabase", LogLevel.Warning, $"Persistence write rejected for tenant {tenant}: persistence.duplicate", (string?)"InvalidOperationException"),
            (sink.Records[0].Category, sink.Records[0].Level, sink.Records[0].Message, sink.Records[0].ExceptionType));
        Assert.DoesNotContain(Sentinel, sink.Text, StringComparison.Ordinal);

        // Nothing can be attached behind the policy.
        var provider = new CapturingLoggerProvider();
        factory.AddProvider(provider);
        factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogCritical("after a provider was offered {Value}", Sentinel);
        Assert.Empty(provider.Entries);
        Assert.Equal(1, diagnostics.Snapshot().RefusedLogProviders);

        // The console sink keeps to the same record: no exception text can reach it either.
        using var written = new StringWriter();
        new TextWriterLogSink(written).Write(sink.Records[0]);
        Assert.EndsWith($"Warning WorkContinuity.Infrastructure.Persistence.TenantScopedDatabase Persistence write rejected for tenant {tenant}: persistence.duplicate [InvalidOperationException]" + Environment.NewLine, written.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_can_hold_only_closed_values_booleans_counts_and_bounded_numbers()
    {
        // Structurally: every value a snapshot can carry is an enumeration, a boolean, a count or a bounded number.
        var leaves = new HashSet<Type>();
        Collect(typeof(HarnessDiagnosticsSnapshot), leaves, []);
        Assert.NotEmpty(leaves);
        Assert.All(leaves, leaf => Assert.True(leaf.IsEnum || leaf == typeof(bool) || leaf == typeof(int) || leaf == typeof(long), leaf.FullName));
        Assert.Contains(typeof(WebhookTokenAlgorithm), leaves);
        Assert.Contains(typeof(CallbackParameter), leaves);

        // Every recording method takes only those kinds too: there is no parameter a received text could travel in.
        var recorders = typeof(HarnessDiagnostics).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(void))
            .ToList();
        Assert.True(recorders.Count >= 12, recorders.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var parameter in recorders.SelectMany(method => method.GetParameters()))
        {
            var kinds = new HashSet<Type>();
            Collect(parameter.ParameterType, kinds, []);
            Assert.All(kinds, kind => Assert.True(kind.IsEnum || kind == typeof(bool) || kind == typeof(int) || kind == typeof(long), $"{parameter.Member.Name}({parameter.Name}): {kind.FullName}"));
        }

        // No instance state of any other kind.
        foreach (var field in typeof(HarnessDiagnostics).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            Assert.False(field.FieldType == typeof(string) || field.FieldType == typeof(object) || field.FieldType == typeof(byte[]), field.Name);
        }

        // Every closed set that classifies something received has a place for what it does not know.
        foreach (var closed in new[] { typeof(CallbackParameter), typeof(WebhookClaim), typeof(WebhookTokenAlgorithm), typeof(WebhookTokenType), typeof(WebhookAuthorizationForm), typeof(WebhookResponseClass), typeof(ProviderEndpoint), typeof(AuthorizationStartOutcome), typeof(AuthorizationCompletionOutcome), typeof(OperatorRoute) })
        {
            Assert.Contains("Other", Enum.GetNames(closed));
        }

        // Numbers are bounded before they are kept.
        var diagnostics = new HarnessDiagnostics();
        diagnostics.AcceptedWebhookLifetime(long.MaxValue);
        diagnostics.AcceptedWebhookLifetime(-5);
        diagnostics.VerifierCleanupRan(startup: true, CleanupOutcome.Succeeded, deleted: int.MaxValue, remaining: -1);
        diagnostics.RelayPass(published: int.MaxValue, failed: false);
        var snapshot = diagnostics.Snapshot();
        Assert.Equal((2L, 0, HarnessDiagnostics.MaxLifetimeSeconds), (snapshot.AcceptedWebhookLifetimes.Count, snapshot.AcceptedWebhookLifetimes.MinimumSeconds, snapshot.AcceptedWebhookLifetimes.MaximumSeconds));
        Assert.Equal((HarnessDiagnostics.MaxRunCount, 0), (snapshot.VerifierCleanup.LastDeleted, snapshot.VerifierCleanup.LastRemaining));
        Assert.Equal(HarnessDiagnostics.MaxRunCount, snapshot.Backbone.RelayPublished);
    }

    [Fact]
    public async Task A_webhook_tokens_shape_is_counted_by_closed_values_and_its_claims_only_when_the_endpoint_accepted_it()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var connection = await harness.AuthorizeAsync();
        var body = HarnessTestHost.WebhookBody(ItemA, "Done", WebhookData.NewTrigger(), Board);
        var now = harness.Clock.UtcNow;

        // Accepted, in both header forms the endpoint takes.
        var bare = WebhookTestTokens.Mint(connection, HarnessTestHost.Account, now, shortLivedToken: HarnessSentinels.ShortLivedToken);
        using (var accepted = await harness.PostWebhookAsync(connection, body, bare))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        var bearer = WebhookTestTokens.Mint(connection, HarnessTestHost.Account, now, lifetime: TimeSpan.FromMinutes(2));
        using (var accepted = await harness.PostWebhookAsync(connection, HarnessTestHost.WebhookBody(ItemA, "Done", WebhookData.NewTrigger(), Board), "Bearer " + bearer))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        // Refused: another app's secret, an unsigned token, a token of another kind, something that is not a token,
        // another scheme, no header, and a token for another connection's audience.
        var refused = new[]
        {
            WebhookTestTokens.Mint(connection, HarnessTestHost.Account, now, secret: WebhookTestTokens.OtherSecret),
            Unsigned("{\"alg\":\"none\"}", "{\"accountId\":9001,\"sentinel_claim\":\"" + HarnessSentinels.UnknownCallbackValue + "\"}"),
            Unsigned("{\"alg\":\"RS256\",\"typ\":\"at+jwt\"}", "{}"),
            "not-a-token",
            "Basic " + HarnessSentinels.ShortLivedToken,
            null,
            WebhookTestTokens.Mint(Guid.CreateVersion7(), HarnessTestHost.Account, now),
        };
        foreach (var authorization in refused)
        {
            using var response = await harness.PostWebhookAsync(connection, body, authorization);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var snapshot = harness.Host.Diagnostics.Snapshot();
        Assert.Equal(1, snapshot.WebhookRequests[new(WebhookAuthorizationForm.Bare, WebhookTokenAlgorithm.HS256, WebhookTokenType.Jwt, WebhookResponseClass.Accepted)]);
        Assert.Equal(1, snapshot.WebhookRequests[new(WebhookAuthorizationForm.Bearer, WebhookTokenAlgorithm.HS256, WebhookTokenType.Jwt, WebhookResponseClass.Accepted)]);
        Assert.Equal(2, snapshot.WebhookRequests[new(WebhookAuthorizationForm.Bare, WebhookTokenAlgorithm.HS256, WebhookTokenType.Jwt, WebhookResponseClass.Unauthenticated)]);
        Assert.Equal(1, snapshot.WebhookRequests[new(WebhookAuthorizationForm.Bare, WebhookTokenAlgorithm.None, WebhookTokenType.Absent, WebhookResponseClass.Unauthenticated)]);
        Assert.Equal(1, snapshot.WebhookRequests[new(WebhookAuthorizationForm.Bare, WebhookTokenAlgorithm.RS256, WebhookTokenType.Other, WebhookResponseClass.Unauthenticated)]);
        Assert.Equal(1, snapshot.WebhookRequests[new(WebhookAuthorizationForm.Bare, WebhookTokenAlgorithm.Unreadable, WebhookTokenType.Unreadable, WebhookResponseClass.Unauthenticated)]);
        Assert.Equal(1, snapshot.WebhookRequests[new(WebhookAuthorizationForm.Other, WebhookTokenAlgorithm.NotApplicable, WebhookTokenType.NotApplicable, WebhookResponseClass.Unauthenticated)]);
        Assert.Equal(1, snapshot.WebhookRequests[new(WebhookAuthorizationForm.Absent, WebhookTokenAlgorithm.NotApplicable, WebhookTokenType.NotApplicable, WebhookResponseClass.Unauthenticated)]);
        Assert.Equal(9, snapshot.WebhookRequests.Values.Sum());

        // Claims are counted for the two accepted tokens only, by known names: the refused tokens' claims, a claim the
        // harness does not know among them, left nothing.
        foreach (var claim in new[] { WebhookClaim.Account, WebhookClaim.Member, WebhookClaim.Audience, WebhookClaim.Expiry, WebhookClaim.IssuedAt, WebhookClaim.ShortLivedToken })
        {
            Assert.Equal(2, snapshot.AcceptedWebhookClaims[claim]);
        }

        Assert.False(snapshot.AcceptedWebhookClaims.ContainsKey(WebhookClaim.Other));
        Assert.Equal(new Dictionary<AudienceRelation, long> { [AudienceRelation.Equal] = 2 }, snapshot.AcceptedWebhookAudiences);
        Assert.Equal((2L, 120, 300), (snapshot.AcceptedWebhookLifetimes.Count, snapshot.AcceptedWebhookLifetimes.MinimumSeconds, snapshot.AcceptedWebhookLifetimes.MaximumSeconds));

        var rendered = string.Join('\n', harness.Host.Diagnostics.Render());
        foreach (var value in new[] { HarnessSentinels.ShortLivedToken, HarnessSentinels.UnknownCallbackValue, "sentinel_claim", bare, bearer, "at+jwt" })
        {
            Assert.DoesNotContain(value, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(value, harness.Logs.Text, StringComparison.Ordinal);
        }
    }

    private static string Unsigned(string header, string claims) =>
        System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header)) + "."
        + System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims)) + ".c2ln";

    private static void AssertAbsent(IEnumerable<(string Name, string Value)> sentinels, string text, string where)
    {
        // A finding names the sentinel, never its value.
        var found = sentinels.Where(sentinel => text.Contains(sentinel.Value, StringComparison.Ordinal)).Select(sentinel => sentinel.Name).ToList();
        Assert.True(found.Count == 0, $"{where} holds: {string.Join(", ", found)}");
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // Every type a value of the given type can carry, down to its leaves.
    private static void Collect(Type type, HashSet<Type> leaves, HashSet<Type> seen)
    {
        if (!seen.Add(type))
        {
            return;
        }

        if (type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(object) || type == typeof(decimal))
        {
            leaves.Add(type);
            return;
        }

        if (type.IsArray)
        {
            Collect(type.GetElementType()!, leaves, seen);
            return;
        }

        if (type.IsGenericType && (typeof(IEnumerable).IsAssignableFrom(type) || type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal)))
        {
            foreach (var argument in type.GetGenericArguments())
            {
                Collect(argument, leaves, seen);
            }

            return;
        }

        var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.GetIndexParameters().Length == 0).ToList();
        Assert.True(members.Count > 0, $"{type.FullName} has no readable member to inspect.");
        foreach (var member in members.Where(property => property.Name != "EqualityContract"))
        {
            Collect(member.PropertyType, leaves, seen);
        }
    }

    /// <summary>Records every activity and every metric measurement the process emits while it lives.</summary>
    private sealed class TelemetryCapture : IDisposable
    {
        /// <summary>A tag value the capture gives itself, to show that it sees tags.</summary>
        internal const string ControlValue = "CONTROL-telemetry-capture-sees-tags";

        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        private readonly ConcurrentQueue<string> _activityText = new();
        private readonly ConcurrentQueue<string> _measurements = new();
        private readonly ConcurrentDictionary<string, bool> _instruments = new(StringComparer.Ordinal);

        internal TelemetryCapture()
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _activityText.Enqueue(Describe(activity)),
            };
            ActivitySource.AddActivityListener(_activities);

            _meters.InstrumentPublished = (instrument, listener) =>
            {
                _instruments[instrument.Meter.Name + "/" + instrument.Name] = true;
                listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
            _meters.SetMeasurementEventCallback<int>((instrument, _, tags, _) => Record(instrument, tags));
            _meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
            _meters.Start();

            using var control = new ActivitySource("WorkContinuity.Integration.Tests.Harness.Control");
            using var activity = control.StartActivity("control");
            activity?.SetTag("control", ControlValue);
        }

        internal IReadOnlyList<string> Activities => [.. _activityText];

        internal IReadOnlyCollection<string> Instruments => [.. _instruments.Keys];

        /// <summary>Everything observed, as one text to search.</summary>
        internal string Text => string.Join('\n', _activityText) + "\n" + string.Join('\n', _measurements);

        public void Dispose()
        {
            _activities.Dispose();
            _meters.Dispose();
        }

        private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var text = new StringBuilder(instrument.Meter.Name).Append('/').Append(instrument.Name);
            foreach (var tag in tags)
            {
                text.Append(' ').Append(tag.Key).Append('=').Append(tag.Value);
            }

            _measurements.Enqueue(text.ToString());
        }

        private static string Describe(Activity activity)
        {
            var text = new StringBuilder(activity.Source.Name).Append('|').Append(activity.OperationName).Append('|').Append(activity.DisplayName)
                .Append('|').Append(activity.StatusDescription);
            foreach (var tag in activity.TagObjects)
            {
                text.Append(' ').Append(tag.Key).Append('=').Append(tag.Value);
            }

            foreach (var item in activity.Baggage)
            {
                text.Append(' ').Append(item.Key).Append('=').Append(item.Value);
            }

            foreach (var recorded in activity.Events)
            {
                text.Append(' ').Append(recorded.Name);
                foreach (var tag in recorded.Tags)
                {
                    text.Append(' ').Append(tag.Key).Append('=').Append(tag.Value);
                }
            }

            return text.ToString();
        }
    }
}
