using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.InitialSync;
using WorkContinuity.Infrastructure.Reconciliation;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Reconciliation;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.InitialSync;

/// <summary>
/// The whole Gate 3F path on the real local PostgreSQL: signed webhooks through the real 3C endpoint, the real processor
/// with the 3D normalizer behind the transport consumer, initial sync with the real monday reader and 3A read client, and
/// 3E reconciliation, all against one deterministic fake monday account. No real monday is contacted, and no webhook is
/// registered anywhere: deliveries are simulated exactly as 3C receives them.
/// </summary>
internal sealed class InitialSyncStack : IAsyncDisposable
{
    internal const string Board = ReconciliationStack.Board;
    internal const string OtherBoard = "1771812700";

    private readonly DatabaseFixture _fixture;

    private InitialSyncStack(
        DatabaseFixture fixture,
        SeededConnection connection,
        MondayWebhookHost webhooks,
        TransportHost processing,
        FakeMonday monday,
        FakeMondayWorkspace account,
        MovingClock clock)
    {
        _fixture = fixture;
        Connection = connection;
        Webhooks = webhooks;
        Processing = processing;
        Monday = monday;
        Account = account;
        Clock = clock;
    }

    internal SeededConnection Connection { get; }

    internal TenantId Tenant => Connection.Tenant;

    internal MondayWebhookHost Webhooks { get; }

    internal TransportHost Processing { get; }

    internal FakeMonday Monday { get; }

    internal FakeMondayWorkspace Account { get; }

    internal MovingClock Clock { get; }

    internal InitialSyncRun Run => new(Tenant, MondayWebhooks.SourceSystem, Connection.Connection);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<InitialSyncStack> StartAsync(
        DatabaseFixture fixture,
        Action<InitialSyncOptions>? options = null,
        Func<SeededConnection, MovingClock, IMondayCredentialSource>? credentials = null,
        SeededConnection? connection = null,
        Action<IServiceCollection>? more = null)
    {
        connection ??= await WebhookData.NewMondayConnectionAsync(fixture, Token);
        var account = new FakeMondayWorkspace();
        var monday = new FakeMonday();
        monday.AnswerApiAsync(account.AnswerAsync);
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        var source = credentials?.Invoke(connection, clock) ?? new FixedMondayCredentials(connection.Tenant, connection.Connection, clock, "58201774");
        var client = new MondayReadClient(new HttpClient(monday), source, clock, new NoWait(), () => 0.5);
        var processing = ProcessingHarness.Host(
            fixture,
            new InMemoryTransport(),
            services =>
            {
                services.AddSingleton<ISourceEventNormalizer, MondayWebhookNormalizer>();
                services.AddSourceReconciliation();
                services.AddSingleton<IAuthoritativeSourceReader>(new MondayStatusReader(client));
                services.AddInitialSync(options);
                services.AddSingleton<IInitialSyncReader>(new MondayInitialSyncReader(client));
                more?.Invoke(services);
            },
            clock);
        var webhooks = await MondayWebhookHost.StartAsync(fixture.ApplicationConnectionString);
        return new InitialSyncStack(fixture, connection, webhooks, processing, monday, account, clock);
    }

    /// <summary>Configures one of the connection's boards through the provider-neutral application path.</summary>
    internal async Task ConfigureAsync(string board, bool enabled = true)
    {
        var configured = await ConfigureAsAsync(Tenant, Connection.Connection, board, enabled);
        Assert.True(configured.IsSuccess, configured.IsSuccess ? string.Empty : configured.Error.Code);
    }

    /// <summary>Configures a board of any connection in the given trusted tenant's scope, and returns the result.</summary>
    internal Task<Result<ConnectorSourceScope>> ConfigureAsAsync(TenantId tenant, Guid connection, string board, bool enabled = true) =>
        InTenantAsync(tenant, services => services.GetRequiredService<IConnectorSourceScopes>()
            .ConfigureAsync(new ConnectorSourceScope(tenant, connection, MondayWebhooks.SourceSystem, board, enabled), Token));

    internal Task<Result<InitialSyncSummary>> StartSyncAsync() =>
        InTenantAsync(Tenant, services => services.GetRequiredService<IInitialSync>().StartAsync(Run, Token));

    internal Task<Result<InitialSyncSummary>> RunSyncAsync() =>
        InTenantAsync(Tenant, services => services.GetRequiredService<IInitialSync>().RunAsync(Run, Token));

    /// <summary>Starts a generation and requires it to start.</summary>
    internal async Task<InitialSyncSummary> StartedAsync()
    {
        var started = await StartSyncAsync();
        Assert.True(started.IsSuccess, started.IsSuccess ? string.Empty : started.Error.Code);
        Assert.Equal(InitialSyncState.Buffering, started.Value.State);
        return started.Value;
    }

    /// <summary>Runs the current generation once and requires the run to complete.</summary>
    internal async Task<InitialSyncSummary> RunOnceAsync()
    {
        var run = await RunSyncAsync();
        Assert.True(run.IsSuccess, run.IsSuccess ? string.Empty : run.Error.Code);
        return run.Value;
    }

    /// <summary>Starts a generation and runs it to Live.</summary>
    internal async Task<InitialSyncSummary> SyncToLiveAsync()
    {
        await StartedAsync();
        var run = await RunOnceAsync();
        Assert.Equal(InitialSyncState.Live, run.State);
        return run;
    }

    /// <summary>Delivers a signed status change through the real endpoint and returns its durable receipt.</summary>
    internal Task<Guid> PostAsync(string item, string label, string board = Board) =>
        Detached(async () =>
        {
            var trigger = WebhookData.NewTrigger();
            Assert.Equal(HttpStatusCode.OK, (await Webhooks.PostEventAsync(Connection, ReconciliationData.Webhook(item, label, trigger, board))).StatusCode);
            return await ReconciliationData.ReceiptOfTriggerAsync(_fixture, Tenant, trigger);
        });

    /// <summary>Hands the receipt's message to the real transport consumer, as the relay's publication would.</summary>
    internal Task<TransportSettlementKind> ConsumeAsync(Guid receiptId) =>
        Detached(async () =>
        {
            var envelope = await new ReconciliationStackEnvelope(_fixture, Tenant).ForAsync(receiptId);
            return (await Processing.Consumer.ConsumeAsync(ProcessingHarness.Received(envelope), Token)).Kind;
        });

    /// <summary>Delivers a status change and hands its message to the consumer.</summary>
    internal async Task<(Guid ReceiptId, TransportSettlementKind Settlement)> DeliverAsync(string item, string label, string board = Board)
    {
        var receipt = await PostAsync(item, label, board);
        return (receipt, await ConsumeAsync(receipt));
    }

    internal async Task<ReconciliationRunSummary> ReconcileAsync()
    {
        var run = await InTenantAsync(Tenant, services => services.GetRequiredService<ISourceReconciler>()
            .RunAsync(new ReconciliationRun(Tenant, MondayWebhooks.SourceSystem, Connection.Connection, 100), Token));
        Assert.True(run.IsSuccess, run.IsSuccess ? string.Empty : run.Error.Code);
        return run.Value;
    }

    internal async Task<int> SweepAsync()
    {
        var swept = await InTenantAsync(Tenant, services => services.GetRequiredService<ISourceReconciler>()
            .RequestSweepAsync(new ReconciliationRun(Tenant, MondayWebhooks.SourceSystem, Connection.Connection, 100), Token));
        Assert.True(swept.IsSuccess, swept.IsSuccess ? string.Empty : swept.Error.Code);
        return swept.Value;
    }

    /// <summary>Runs work in a scope of the given trusted tenant, in its own flow, as a worker would.</summary>
    internal Task<TResult> InTenantAsync<TResult>(TenantId tenant, Func<IServiceProvider, Task<TResult>> work) =>
        Task.Run(
            async () =>
            {
                await using var scope = Processing.Services.CreateAsyncScope();
                Processing.TenantContextAccessor.SetResolved(TenantContext.Resolved(tenant, TenantResolutionSource.TrustedServiceIdentity));
                return await work(scope.ServiceProvider);
            },
            CancellationToken.None);

    // Work that arrives on its own, as a webhook request or a delivered message does: outside the caller's flow, so a run
    // the fake account interleaves it with lends it no trusted tenant.
    private static Task<TResult> Detached<TResult>(Func<Task<TResult>> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Webhooks.DisposeAsync();
        await Processing.DisposeAsync();
    }

    // The message the relay would publish for a receipt, built as the 3E stack builds it.
    private sealed class ReconciliationStackEnvelope(DatabaseFixture fixture, TenantId tenant)
    {
        internal async Task<TransportEnvelope> ForAsync(Guid receiptId)
        {
            var outboxId = await TransportData.OutboxIdForReceiptAsync(fixture, receiptId, Token);
            var marker = Assert.Single(await Ingress.IngressData.MarkersAsync(fixture, tenant, Token), candidate => candidate.OutboxMessageId == outboxId);
            Assert.True(Core.Correlation.CorrelationId.TryParse(marker.CorrelationId, out var correlation));
            var envelope = TransportEnvelope.FromOutbox(outboxId, tenant, Core.Ingress.IngressMessageTypes.ReceiptReceived, TransportData.Marker(receiptId), correlation);
            Assert.True(envelope.IsSuccess);
            return envelope.Value;
        }
    }
}

/// <summary>A monday initial-sync reader that must never be called.</summary>
internal sealed class UncalledReader : IInitialSyncReader
{
    public string SourceSystem => MondayWebhooks.SourceSystem;

    public Task<ScopeValidation> ValidateScopeAsync(ScopeReadRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Starting a generation reads nothing from the source.");

    public Task<ScopePage> ReadPageAsync(ScopePageRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Starting a generation reads nothing from the source.");
}

/// <summary>A connection's sync row as the database holds it.</summary>
internal sealed record SyncRow(
    long Generation,
    string State,
    string? ProviderSubject,
    DateTimeOffset? AuthorizedAtUtc,
    long ReplayFloor,
    long BufferStartFence,
    long? ReplayFence,
    long? CutoverFence,
    Guid? ClaimId,
    DateTimeOffset? ClaimedUntilUtc,
    int Attempts,
    DateTimeOffset? NextAttemptAtUtc,
    string? LastOutcome,
    DateTimeOffset? LiveAtUtc,
    long ScopeVersion,
    long? GenerationScopeVersion);

/// <summary>A field of the current work view, as a reader in one tenant's scope sees it.</summary>
internal sealed record ActiveField(Guid Tenant, string ObjectId, string Field, string? Value);

/// <summary>One scope of one generation as the database holds it.</summary>
internal sealed record SyncScopeRow(long Generation, string ResourceId, string State, int Attempts, string? LastOutcome, int PagesRead, int ObjectsRead, int FieldsApplied);

/// <summary>Reads what initial sync stored, with the schema owner.</summary>
internal static class InitialSyncData
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Rows of the tenant in all three Gate 3F tables, through the real application path: one configured scope of the
    /// connection and a started generation. Starting reads nothing from the source, so the reader is never called.
    /// </summary>
    internal static async Task SeedStartedAsync(DatabaseFixture fixture, TenantId tenant, Guid connection)
    {
        await using var host = ProcessingHarness.Host(
            fixture,
            new InMemoryTransport(),
            services =>
            {
                services.AddSingleton<ISourceEventNormalizer, MondayWebhookNormalizer>();
                services.AddInitialSync();
                services.AddSingleton<IInitialSyncReader>(new UncalledReader());
            });
        await Task.Run(
            async () =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                host.TenantContextAccessor.SetResolved(TenantContext.Resolved(tenant, TenantResolutionSource.TrustedServiceIdentity));
                var configured = await scope.ServiceProvider.GetRequiredService<IConnectorSourceScopes>()
                    .ConfigureAsync(new ConnectorSourceScope(tenant, connection, MondayWebhooks.SourceSystem, "9001", enabled: true), Token);
                Assert.True(configured.IsSuccess, configured.IsSuccess ? string.Empty : configured.Error.Code);
                var started = await scope.ServiceProvider.GetRequiredService<IInitialSync>()
                    .StartAsync(new InitialSyncRun(tenant, MondayWebhooks.SourceSystem, connection), Token);
                Assert.True(started.IsSuccess, started.IsSuccess ? string.Empty : started.Error.Code);
            },
            CancellationToken.None);
    }

    internal static async Task<SyncRow?> SyncAsync(DatabaseFixture fixture, TenantId tenant, Guid connection)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT generation, state, provider_subject, authorized_at_utc, replay_floor, buffer_start_fence, replay_fence, cutover_fence,
                   claim_id, claimed_until_utc, attempts, next_attempt_at_utc, last_outcome, live_at_utc, scope_version, generation_scope_version
            FROM connection_syncs WHERE tenant_id = @tenant AND connection_id = @connection;
            """,
            admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("connection", connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        if (!await reader.ReadAsync(Token))
        {
            return null;
        }

        return new SyncRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            reader.GetInt64(14),
            reader.IsDBNull(15) ? null : reader.GetInt64(15));
    }

    internal static async Task<IReadOnlyList<SyncScopeRow>> ScopesAsync(DatabaseFixture fixture, TenantId tenant, Guid connection)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT generation, resource_id, state, attempts, last_outcome, pages_read, objects_read, fields_applied
            FROM connection_sync_scopes WHERE tenant_id = @tenant AND connection_id = @connection ORDER BY generation, resource_id;
            """,
            admin);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("connection", connection);
        var rows = new List<SyncScopeRow>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(new SyncScopeRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }

        return rows;
    }

    /// <summary>
    /// Writes an enabled scope straight into the table as the runtime role, in the tenant's scope, bypassing the
    /// configuration service: a scope the service never saw, with no sync row for its connection.
    /// </summary>
    internal static async Task InsertScopeDirectlyAsync(DatabaseFixture fixture, TenantId tenant, Guid connection, string resource)
    {
        await using var runtime = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await runtime.OpenAsync(Token);
        await using var transaction = await runtime.BeginTransactionAsync(Token);
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true);", runtime, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenant.Value.ToString("D"));
            await scope.ExecuteNonQueryAsync(Token);
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO connector_source_scopes (tenant_id, connection_id, source_system, resource_id, enabled, created_at_utc, updated_at_utc)
            VALUES (@tenant, @connection, 'monday', @resource, true, now(), now());
            """,
            runtime,
            transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenant.Value);
            insert.Parameters.AddWithValue("connection", connection);
            insert.Parameters.AddWithValue("resource", resource);
            await insert.ExecuteNonQueryAsync(Token);
        }

        await transaction.CommitAsync(Token);
    }

    /// <summary>A receipt's ingress position and processing state.</summary>
    internal static async Task<(long Sequence, string State)> ReceiptAsync(DatabaseFixture fixture, Guid receiptId)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand("SELECT ingress_sequence, processing_state FROM raw_receipts WHERE receipt_id = @receipt;", admin);
        command.Parameters.AddWithValue("receipt", receiptId);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        return (reader.GetInt64(0), reader.GetString(1));
    }

    /// <summary>
    /// Opens a transaction as the runtime role that stores one receipt exactly as ingress stores it, holding the connection
    /// row's key-share lock and leaving the receipt uncommitted until the caller commits: ingress caught mid-write.
    /// </summary>
    /// <summary>
    /// The current work view's fields as the runtime role reads them in the tenant's scope: every row it can see, so a row of
    /// another tenant would show.
    /// </summary>
    internal static async Task<IReadOnlyList<ActiveField>> ActiveFieldsAsync(DatabaseFixture fixture, TenantId tenant)
    {
        var fields = new List<ActiveField>();
        await AsRuntimeAsync(fixture, tenant, "SELECT tenant_id, object_id, field, value FROM active_snapshot_fields ORDER BY tenant_id, object_id, field;", async reader =>
        {
            while (await reader.ReadAsync(Token))
            {
                fields.Add(new ActiveField(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        });
        return fields;
    }

    /// <summary>The current work view's objects as the runtime role reads them in the tenant's scope.</summary>
    internal static async Task<IReadOnlyList<string>> ActiveObjectsAsync(DatabaseFixture fixture, TenantId tenant)
    {
        var objects = new List<string>();
        await AsRuntimeAsync(fixture, tenant, "SELECT object_id FROM active_snapshot_objects ORDER BY object_id;", async reader =>
        {
            while (await reader.ReadAsync(Token))
            {
                objects.Add(reader.GetString(0));
            }
        });
        return objects;
    }

    internal static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> BeginIngressAsync(
        DatabaseFixture fixture,
        SeededConnection connection,
        Guid receiptId,
        string item,
        string label)
    {
        var trigger = WebhookData.NewTrigger();
        var payload = ReconciliationData.Webhook(item, label, trigger);
        var runtime = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await runtime.OpenAsync(Token);
        var transaction = await runtime.BeginTransactionAsync(Token);
        await using (var tenant = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true);", runtime, transaction))
        {
            tenant.Parameters.AddWithValue("tenant", connection.Tenant.Value.ToString("D"));
            await tenant.ExecuteNonQueryAsync(Token);
        }

        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM connections WHERE tenant_id = @tenant AND connection_id = @connection FOR KEY SHARE;", runtime, transaction))
        {
            lockRow.Parameters.AddWithValue("tenant", connection.Tenant.Value);
            lockRow.Parameters.AddWithValue("connection", connection.Connection);
            Assert.Equal(1, Convert.ToInt32(await lockRow.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture));
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO raw_receipts (receipt_id, tenant_id, connection_reference, source_system, source_event_id, schema_version, payload, payload_fingerprint, processing_state)
            VALUES (@receipt, @tenant, @reference, 'monday', @event, @schema, @payload::jsonb, @fingerprint, 'Received');
            """,
            runtime,
            transaction))
        {
            insert.Parameters.AddWithValue("receipt", receiptId);
            insert.Parameters.AddWithValue("tenant", connection.Tenant.Value);
            insert.Parameters.AddWithValue("reference", connection.Connection.ToString("D"));
            insert.Parameters.AddWithValue("event", trigger);
            insert.Parameters.AddWithValue("schema", MondayWebhooks.SchemaVersion);
            insert.Parameters.AddWithValue("payload", payload);
            insert.Parameters.AddWithValue("fingerprint", "sha256:" + receiptId.ToString("N"));
            await insert.ExecuteNonQueryAsync(Token);
        }

        // Its outbox marker in the same transaction, as ingress queues it.
        await using (var marker = new NpgsqlCommand(
            """
            INSERT INTO outbox_messages (outbox_message_id, tenant_id, message_type, payload, occurred_at_utc, correlation_id)
            VALUES (@message, @tenant, @type, @payload::jsonb, now(), @correlation);
            """,
            runtime,
            transaction))
        {
            marker.Parameters.AddWithValue("message", Guid.CreateVersion7());
            marker.Parameters.AddWithValue("tenant", connection.Tenant.Value);
            marker.Parameters.AddWithValue("type", Core.Ingress.IngressMessageTypes.ReceiptReceived);
            marker.Parameters.AddWithValue("payload", TransportData.Marker(receiptId));
            marker.Parameters.AddWithValue("correlation", Core.Correlation.CorrelationId.New().Value);
            await marker.ExecuteNonQueryAsync(Token);
        }

        return (runtime, transaction);
    }

    private static async Task AsRuntimeAsync(DatabaseFixture fixture, TenantId tenant, string sql, Func<NpgsqlDataReader, Task> read)
    {
        await using var runtime = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await runtime.OpenAsync(Token);
        await using var transaction = await runtime.BeginTransactionAsync(Token);
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true);", runtime, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenant.Value.ToString("D"));
            await scope.ExecuteNonQueryAsync(Token);
        }

        await using (var command = new NpgsqlCommand(sql, runtime, transaction))
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            await read(reader);
        }

        await transaction.RollbackAsync(Token);
    }
}
