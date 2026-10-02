using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Reconciliation;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Reconciliation;

/// <summary>
/// The whole Gate 3E path on the real local PostgreSQL: a signed webhook through the real 3C endpoint, the real processor
/// with the 3D normalizer, and reconciliation with the real monday reader and 3A read client against a deterministic fake
/// of monday's API. No real monday is contacted. The credential is the connection's delegated identity, as 3B issues it,
/// unless a test brings the full 3B lifecycle.
/// </summary>
internal sealed class ReconciliationStack : IAsyncDisposable
{
    internal const string Board = "1771812698";

    private readonly DatabaseFixture _fixture;

    private ReconciliationStack(
        DatabaseFixture fixture,
        SeededConnection connection,
        MondayWebhookHost webhooks,
        TransportHost processing,
        FakeMonday monday,
        MovingClock clock)
    {
        _fixture = fixture;
        Connection = connection;
        Webhooks = webhooks;
        Processing = processing;
        Monday = monday;
        Clock = clock;
    }

    internal SeededConnection Connection { get; }

    internal TenantId Tenant => Connection.Tenant;

    internal MondayWebhookHost Webhooks { get; }

    internal TransportHost Processing { get; }

    internal FakeMonday Monday { get; }

    internal MovingClock Clock { get; }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<ReconciliationStack> StartAsync(
        DatabaseFixture fixture,
        Action<ReconciliationOptions>? options = null,
        string credentialSubject = "58201774",
        SeededConnection? connection = null,
        Func<SeededConnection, MovingClock, FakeMonday, IMondayCredentialSource>? credentials = null,
        FakeMonday? monday = null,
        MovingClock? clock = null)
    {
        connection ??= await WebhookData.NewMondayConnectionAsync(fixture, Token);
        monday ??= new FakeMonday();
        clock ??= new MovingClock(DateTimeOffset.UtcNow);
        var source = credentials?.Invoke(connection, clock, monday)
            ?? new FixedMondayCredentials(connection.Tenant, connection.Connection, clock, credentialSubject);
        var reader = new MondayStatusReader(new MondayReadClient(new HttpClient(monday), source, clock, new NoWait(), () => 0.5));
        var processing = ProcessingHarness.Host(
            fixture,
            new InMemoryTransport(),
            services =>
            {
                services.AddSingleton<ISourceEventNormalizer, MondayWebhookNormalizer>();
                services.AddSourceReconciliation(options);
                services.AddSingleton<IAuthoritativeSourceReader>(reader);
            },
            clock);
        var webhooks = await MondayWebhookHost.StartAsync(fixture.ApplicationConnectionString);
        return new ReconciliationStack(fixture, connection, webhooks, processing, monday, clock);
    }

    /// <summary>Delivers a signed webhook through the real endpoint and processes its durable receipt.</summary>
    internal async Task<EventProcessingResult> DeliverAsync(string body, string trigger)
    {
        Assert.Equal(HttpStatusCode.OK, (await Webhooks.PostEventAsync(Connection, body)).StatusCode);
        var receiptId = await ReconciliationData.ReceiptOfTriggerAsync(_fixture, Tenant, trigger);
        return await Processing.ProcessSettledAsync(await EnvelopeAsync(receiptId));
    }

    /// <summary>Holds one item's status change for reconciliation, as a webhook does.</summary>
    internal async Task<EventProcessingResult> HoldAsync(string itemId, string label = "Working on it", string board = Board)
    {
        var trigger = WebhookData.NewTrigger();
        var held = await DeliverAsync(ReconciliationData.Webhook(itemId, label, trigger, board), trigger);
        Assert.Equal(EventProcessingOutcome.ReconciliationRequired, held.Outcome);
        return held;
    }

    /// <summary>The message the relay would publish for a receipt: its outbox marker, in the receipt's tenant.</summary>
    internal async Task<TransportEnvelope> EnvelopeAsync(Guid receiptId)
    {
        var outboxId = await TransportData.OutboxIdForReceiptAsync(_fixture, receiptId, Token);
        var marker = Assert.Single(await IngressData.MarkersAsync(_fixture, Tenant, Token), candidate => candidate.OutboxMessageId == outboxId);
        Assert.True(CorrelationId.TryParse(marker.CorrelationId, out var correlation));
        var envelope = TransportEnvelope.FromOutbox(outboxId, Tenant, IngressMessageTypes.ReceiptReceived, TransportData.Marker(receiptId), correlation);
        Assert.True(envelope.IsSuccess);
        return envelope.Value;
    }

    /// <summary>Runs reconciliation for the connection and requires it to complete.</summary>
    internal async Task<ReconciliationRunSummary> RunAsync(int maxTargets = 100)
    {
        var run = await RunAsAsync(Tenant, Connection.Connection, maxTargets, Token);
        Assert.True(run.IsSuccess, run.IsSuccess ? string.Empty : run.Error.Code);
        return run.Value;
    }

    /// <summary>Runs reconciliation in a tenant scope, in its own flow, as a worker would.</summary>
    internal Task<Result<ReconciliationRunSummary>> RunAsAsync(TenantId scopeTenant, Guid connection, int maxTargets, CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                await using var scope = Processing.Services.CreateAsyncScope();
                Processing.TenantContextAccessor.SetResolved(TenantContext.Resolved(scopeTenant, TenantResolutionSource.TrustedServiceIdentity));
                return await scope.ServiceProvider.GetRequiredService<ISourceReconciler>()
                    .RunAsync(new ReconciliationRun(scopeTenant, MondayWebhooks.SourceSystem, connection, maxTargets), cancellationToken);
            },
            CancellationToken.None);

    /// <summary>Requests a sweep of the connection's known targets.</summary>
    internal Task<int> SweepAsync(int maxTargets = 100) =>
        Task.Run(
            async () =>
            {
                await using var scope = Processing.Services.CreateAsyncScope();
                Processing.TenantContextAccessor.SetResolved(TenantContext.Resolved(Tenant, TenantResolutionSource.TrustedServiceIdentity));
                var swept = await scope.ServiceProvider.GetRequiredService<ISourceReconciler>()
                    .RequestSweepAsync(new ReconciliationRun(Tenant, MondayWebhooks.SourceSystem, Connection.Connection, maxTargets), Token);
                Assert.True(swept.IsSuccess, swept.IsSuccess ? string.Empty : swept.Error.Code);
                return swept.Value;
            },
            CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await Webhooks.DisposeAsync();
        await Processing.DisposeAsync();
    }
}

/// <summary>
/// The connection's delegated credential, as 3B issues it for the seeded authorization, without 3B's token lifecycle. The
/// client still acquires it before every attempt.
/// </summary>
internal sealed class FixedMondayCredentials(TenantId tenant, Guid connection, IClock clock, string subject) : IMondayCredentialSource
{
    internal const string AccessToken = "integration-token-not-a-secret";

    private int _acquired;

    internal int Acquired => _acquired;

    public ValueTask<MondayCredentialResult> AcquireAsync(MondayRequestContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _acquired);
        return ValueTask.FromResult(MondayCredentialResult.Acquired(new MondayAccessCredential(
            AccessToken,
            new ConnectorIdentity(tenant, connection, ConnectorIdentityKind.DelegatedUser, subject),
            clock.UtcNow.AddHours(1))));
    }

    public ValueTask<MondayCredentialResult> RenewAfterRejectionAsync(
        MondayRequestContext context,
        MondayAccessCredential rejected,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(MondayCredentialResult.NotRenewed());
}

/// <summary>
/// The connection's delegated credential, as <see cref="FixedMondayCredentials"/> issues it, until the test makes it
/// unobtainable, as an unreadable secret store makes it: then every acquisition is refused and nothing is sent.
/// </summary>
internal sealed class SwitchableMondayCredentials(TenantId tenant, Guid connection, IClock clock) : IMondayCredentialSource
{
    private readonly FixedMondayCredentials _credential = new(tenant, connection, clock, "58201774");

    internal bool Unobtainable { get; set; }

    public ValueTask<MondayCredentialResult> AcquireAsync(MondayRequestContext context, CancellationToken cancellationToken) =>
        Unobtainable ? ValueTask.FromResult(MondayCredentialResult.Unavailable()) : _credential.AcquireAsync(context, cancellationToken);

    public ValueTask<MondayCredentialResult> RenewAfterRejectionAsync(
        MondayRequestContext context,
        MondayAccessCredential rejected,
        CancellationToken cancellationToken) =>
        _credential.RenewAfterRejectionAsync(context, rejected, cancellationToken);
}

/// <summary>One item as a monday status reread answers it.</summary>
internal sealed record ItemRead(
    string Id,
    string? Label,
    DateTimeOffset? UpdatedAt,
    string Board = ReconciliationStack.Board,
    string State = "active",
    string Column = "status",
    string Type = "status");

/// <summary>A reconciliation target as the database holds it.</summary>
internal sealed record TargetRow(
    string ObjectId,
    string Field,
    string DataType,
    string? SourceScope,
    string State,
    long Generation,
    long? ResolvedGeneration,
    string RequestKind,
    Guid? RequestedByEventId,
    Guid? ClaimId,
    long? ClaimedGeneration,
    int Attempts,
    DateTimeOffset? NextAttemptAtUtc,
    string? LastOutcome,
    Guid? ResolvedByEventId,
    DateTimeOffset? VerifiedAtUtc);

/// <summary>A durable receipt as the database holds it.</summary>
internal sealed record ReceiptRow(Guid ReceiptId, string SchemaVersion, string? SourceEventId, string ProcessingState, string? Payload, bool HasOutboxMessage);

/// <summary>Builds monday bodies and reads what reconciliation stored, with the schema owner.</summary>
internal static class ReconciliationData
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A documented status change of one item on one board, with the label and trigger the test chooses.</summary>
    internal static string Webhook(string itemId, string label, string trigger, string board = ReconciliationStack.Board) =>
        "{\"event\":{\"userId\":9603417,\"originalTriggerUuid\":null,\"boardId\":" + board + ",\"groupId\":\"topics\",\"pulseId\":" + itemId
        + ",\"pulseName\":\"Name marker\",\"columnId\":\"status\",\"columnType\":\"color\",\"columnTitle\":\"Status\",\"value\":{\"label\":{\"index\":3,\"text\":\""
        + label + "\",\"style\":{\"color\":\"#0086c0\"}},\"post_id\":null},\"previousValue\":null,\"changedAt\":1633944017.473193,"
        + "\"isTopGroup\":true,\"app\":\"monday\",\"type\":\"update_column_value\",\"triggerTime\":\"2021-10-11T09:20:18.022Z\","
        + "\"subscriptionId\":73761176,\"triggerUuid\":\"" + trigger + "\"}}";

    /// <summary>monday's answer to a status reread of the given items.</summary>
    internal static string StatusRead(params ItemRead[] items) =>
        "{\"data\":{\"items\":[" + string.Join(",", items.Select(item =>
            "{\"id\":\"" + item.Id + "\",\"state\":\"" + item.State + "\",\"board\":{\"id\":\"" + item.Board + "\"},\"column_values\":[{\"id\":\""
            + item.Column + "\",\"type\":\"" + item.Type + "\",\"label\":" + (item.Label is null ? "null" : "\"" + item.Label + "\"")
            + ",\"index\":" + (item.Label is null ? "null" : "4") + ",\"updated_at\":" + (item.UpdatedAt is { } at ? "\"" + Iso(at) + "\"" : "null")
            + "}]}")) + "]},\"account_id\":9001}";

    /// <summary>An instant as monday writes a <c>Date</c>: whole seconds with an offset.</summary>
    internal static string Iso(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

    /// <summary>An instant truncated to whole seconds, as monday reports it.</summary>
    internal static DateTimeOffset Seconds(DateTimeOffset instant) =>
        new(instant.Ticks - (instant.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    /// <summary>The item ids every status reread so far asked monday for.</summary>
    internal static List<string> RequestedItemIds(FakeMonday monday) =>
        monday.Requests
            .Where(request => request.Uri == MondayEndpoints.Api)
            .SelectMany(request =>
            {
                using var body = System.Text.Json.JsonDocument.Parse(request.Body);
                return body.RootElement.GetProperty("variables").GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToList();
            })
            .ToList();

    internal static async Task<DateTimeOffset> DatabaseNowAsync(DatabaseFixture fixture)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand("SELECT now();", admin);
        return new DateTimeOffset(DateTime.SpecifyKind((DateTime)(await command.ExecuteScalarAsync(Token))!, DateTimeKind.Utc));
    }

    internal static async Task<Guid> ReceiptOfTriggerAsync(DatabaseFixture fixture, TenantId tenant, string trigger)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand("SELECT receipt_id FROM raw_receipts WHERE tenant_id = @tenant AND source_event_id = @trigger;", admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("trigger", trigger);
        return (Guid)(await command.ExecuteScalarAsync(Token))!;
    }

    internal static async Task<IReadOnlyList<TargetRow>> TargetsAsync(DatabaseFixture fixture, TenantId tenant)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT object_id, field, data_type, source_scope, state, generation, resolved_generation, request_kind, requested_by_event_id,
                   claim_id, claimed_generation, attempts, next_attempt_at_utc, last_outcome, resolved_by_event_id, verified_at_utc
            FROM reconciliation_targets WHERE tenant_id = @tenant ORDER BY object_id, field;
            """,
            admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        var rows = new List<TargetRow>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(new TargetRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetGuid(14),
                reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15)));
        }

        return rows;
    }

    internal static async Task<IReadOnlyList<ReceiptRow>> ReceiptsAsync(DatabaseFixture fixture, TenantId tenant)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT r.receipt_id, r.schema_version, r.source_event_id, r.processing_state, r.payload::text,
                   EXISTS (SELECT 1 FROM outbox_messages o WHERE o.tenant_id = r.tenant_id AND o.payload ->> 'receiptId' = r.receipt_id::text)
            FROM raw_receipts r WHERE r.tenant_id = @tenant ORDER BY r.received_at_utc, r.receipt_id;
            """,
            admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        var rows = new List<ReceiptRow>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(new ReceiptRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetBoolean(5)));
        }

        return rows;
    }

    /// <summary>
    /// Authorizes the connection again through the real 3B store, as its user's reauthorization does: the authorization
    /// first needs authorizing again, then is restored for the same monday user and account with a new credential.
    /// </summary>
    internal static async Task ReauthorizeAsync(DatabaseFixture fixture, SeededConnection connection)
    {
        await using var scoped = ConnectorAuthorizationData.Store(fixture, connection.Tenant);
        var current = await scoped.Store.ReadAsync(connection.Connection, Token);
        Assert.True(current.IsSuccess && current.Value is not null);
        var required = await scoped.Store.RequireReauthorizationAsync(connection.Connection, current.Value!.Generation, null, Token);
        Assert.True(required.IsSuccess && required.Value);
        var restored = await scoped.Store.ReauthorizeAsync(ConnectorAuthorizationData.Grant(connection.User, connection.Connection, account: connection.Account), Token);
        Assert.True(restored.IsSuccess && restored.Value);
    }

    /// <summary>Runs statements as the schema owner, as an administrator's change would.</summary>
    internal static async Task AdministerAsync(DatabaseFixture fixture, string sql, params (string Name, object Value)[] parameters)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(sql, admin);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Token);
    }
}
