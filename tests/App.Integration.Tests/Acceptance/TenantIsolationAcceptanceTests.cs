using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Observability;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Acceptance;

/// <summary>
/// Gate 2 tenant isolation on the whole path. Tenants that share object ids and provider event ids run through ingress,
/// the relay, the broker, processing, the ledger and the Snapshot together while cross-tenant misuse is attempted at
/// every layer. Any cross-tenant read, write or projection is a Gate FAIL. Every database check runs as the non-owner
/// runtime role: a superuser bypasses row-level security and would prove nothing.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenantIsolationAcceptanceTests(DatabaseFixture fixture)
{
    private static readonly DateTimeOffset T0 = IngressHost.Now.AddHours(-1);

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tenants_sharing_object_and_provider_event_ids_run_the_whole_path_together_and_never_cross()
    {
        await using var path = await WholePath.StartAsync(fixture);
        using var relayMetrics = new MetricsRecorder(path.Source.Services.GetRequiredService<EventBackboneMetrics>());
        using var processingMetrics = new MetricsRecorder(path.Processing.Services.GetRequiredService<EventBackboneMetrics>());
        var marker = IngressData.NewMarker();
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, connectionB) = await IngressData.NewConnectionAsync(fixture, Token);
        var eventsA = Shared($"{marker}-a");
        var eventsB = Shared($"{marker}-b");

        // Interleaved, A in the source's order and B in reverse; A's first payload names tenant B, which counts for nothing.
        var owners = new Dictionary<Guid, TenantId>();
        for (var index = 0; index < eventsA.Count; index++)
        {
            var a = eventsA[index];
            var payload = index == 0 ? NamingAnotherTenant(a, tenantB) : a.Event.ToPayload();
            var storedA = await path.SubmitAsync(SyntheticSource.Delivery(connectionA, a.Id, payload));
            var storedB = await path.SubmitAsync(connectionB, eventsB[^(index + 1)]);
            Assert.Equal((IngressStatus.Accepted, IngressStatus.Accepted), (storedA.Status, storedB.Status));
            owners[storedA.ReceiptId!.Value] = tenantA;
            owners[storedB.ReceiptId!.Value] = tenantB;
        }

        await path.FlushAsync();

        // Each tenant converged on its own truth, with its own provenance, alone.
        foreach (var (tenant, events) in new[] { (tenantA, eventsA), (tenantB, eventsB) })
        {
            await path.AssertFactsAsync(tenant, GroundTruth.Of(events));
            await path.AssertProvenanceAsync(tenant);
            await path.AssertVersionsAsync(tenant);
            var accounting = await path.AssertNothingSilentlyLostAsync(tenant, owners.Where(owner => owner.Value == tenant).Select(owner => owner.Key));
            Assert.Empty(accounting.DeadLetters);
        }

        // Every message was published under its receipt's tenant, in that tenant's own session.
        Assert.Equal(owners.Count, path.Broker.Sends.Count);
        foreach (var wire in path.Broker.Sends)
        {
            var owner = owners[(Guid)wire.ApplicationProperties[TransportProperties.Causation]];
            Assert.Equal(owner.Value, (Guid)wire.ApplicationProperties[TransportProperties.Tenant]);
            Assert.Equal(TransportEnvelope.SessionIdFor(owner), wire.SessionId);
        }

        // Through the runtime role, each tenant sees only its own rows of every table the path wrote.
        await AssertSeesOnlyItsOwnAsync(tenantA);
        await AssertSeesOnlyItsOwnAsync(tenantB);

        // No log record speaks of both tenants or carries content; metrics carry bounded tags and no identifier.
        Assert.DoesNotContain(path.Logs, entry =>
            entry.Text.Contains(tenantA.ToString(), StringComparison.Ordinal) && entry.Text.Contains(tenantB.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(path.Logs, entry => entry.Text.Contains(marker, StringComparison.Ordinal));
        relayMetrics.AssertBoundedTags(tenantA.ToString(), tenantB.ToString(), marker);
        processingMetrics.AssertBoundedTags(tenantA.ToString(), tenantB.ToString(), marker);
    }

    [Fact]
    public async Task Every_tenant_owned_table_refuses_cross_tenant_reads_updates_deletes_moves_and_foreign_labels()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var tenants = new List<TenantId>();
        foreach (var label in new[] { "a", "b" })
        {
            var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
            await SeedUserAsync(tenant);
            foreach (var stated in Shared(label))
            {
                await path.SubmitAcceptedAsync(connection, stated);
            }

            tenants.Add(tenant);
        }

        await path.FlushAsync();
        var before = await RowCountsAsync(tenants);

        await using var runtime = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await runtime.OpenAsync(Token);
        var attempts = 0;
        foreach (var table in WorkContinuityModel.TenantOwnedTables)
        {
            foreach (var (self, other) in new[] { (tenants[0], tenants[1]), (tenants[1], tenants[0]) })
            {
                // Not vacuous: in its own scope each tenant sees its own rows of every table.
                Assert.True(await CountAsync(runtime, self, other, $"SELECT count(*) FROM {table} WHERE tenant_id = @self;") > 0, $"{table} has no rows of its own.");
                var foreign = await CountAsync(runtime, self, other, $"SELECT count(*) FROM {table} WHERE tenant_id = @other;");
                Assert.True(foreign == 0, $"{table} shows {foreign} rows of another tenant.");

                foreach (var (attempt, sql) in new[]
                {
                    ("update another tenant's rows", $"UPDATE {table} SET tenant_id = tenant_id WHERE tenant_id = @other;"),
                    ("delete another tenant's rows", $"DELETE FROM {table} WHERE tenant_id = @other;"),
                    ("move its own rows to another tenant", $"UPDATE {table} SET tenant_id = @other WHERE tenant_id = @self;"),
                    ("write a row labelled with another tenant", $"INSERT INTO {table} SELECT (jsonb_populate_record(NULL::{table}, to_jsonb(r) || jsonb_build_object('tenant_id', @other))).* FROM {table} r WHERE r.tenant_id = @self LIMIT 1;"),
                })
                {
                    // Refused either by row-level security, as nothing visible or a violated policy, or by a missing
                    // privilege: both are SQLSTATE 42501 or no row at all, and nothing else is accepted.
                    var (affected, state) = await AttemptAsync(runtime, self, other, sql);
                    Assert.True(affected == 0 && state is null or PostgresErrorCodes.InsufficientPrivilege, $"{table}, {attempt}: {affected} rows, SQLSTATE {state}.");
                    attempts++;
                }
            }
        }

        Assert.Equal(WorkContinuityModel.TenantOwnedTables.Count * 2 * 4, attempts);
        Assert.Equal(before, await RowCountsAsync(tenants));
    }

    [Fact]
    public async Task Forged_broker_metadata_and_a_foreign_processing_scope_are_refused_and_change_no_tenant()
    {
        await using var path = await WholePath.StartAsync(fixture);
        var (tenantA, connectionA) = await IngressData.NewConnectionAsync(fixture, Token);
        var (tenantB, connectionB) = await IngressData.NewConnectionAsync(fixture, Token);
        var a1 = Stated("evt-victim-1", "2001", 1, ("status", "TaskStatus", "a-working"));
        var a2 = Stated("evt-victim-2", "2001", 2, ("status", "TaskStatus", "a-done"));
        var b1 = Stated("evt-victim-1", "2001", 1, ("status", "TaskStatus", "b-working"));
        var receiptA1 = await path.SubmitAcceptedAsync(connectionA, a1);
        var receiptB1 = await path.SubmitAcceptedAsync(connectionB, b1);
        await path.FlushAsync();

        // A's second event is stored and not yet relayed: the most useful target a forger could have.
        var receiptA2 = await path.SubmitAcceptedAsync(connectionA, a2);
        var beforeA = await TenantStateAsync(tenantA);
        var beforeB = await TenantStateAsync(tenantB);

        // Well-formed messages from someone with send rights: B's tenant and session naming A's receipts, processed and
        // unprocessed; A's genuine tenant moved into B's session; and A's tenant naming B's receipt.
        (string Case, TransportWireMessage Wire, string Reason)[] forged =
        [
            ("B names A's processed receipt", Forge(tenantB, receiptA1), ProcessingErrors.ReceiptNotVisible.Code),
            ("B names A's unprocessed receipt", Forge(tenantB, receiptA2), ProcessingErrors.ReceiptNotVisible.Code),
            ("A's tenant in B's session", Forge(tenantA, receiptA2) with { SessionId = TransportEnvelope.SessionIdFor(tenantB) }, TransportErrors.SessionMismatch.Code),
            ("A names B's receipt", Forge(tenantA, receiptB1), ProcessingErrors.ReceiptNotVisible.Code),
        ];
        foreach (var (_, wire, _) in forged)
        {
            path.Broker.SendRaw(wire.MessageId, wire.SessionId, wire.CorrelationId, wire.ContentType, wire.ApplicationProperties, wire.Body.ToArray());
        }

        var deliveries = await path.DrainAsync();

        // Each is dead-lettered at once with its reason, never retried as if the tenant could change.
        foreach (var (name, wire, reason) in forged)
        {
            var delivery = Assert.Single(deliveries, candidate => candidate.Message.MessageId == wire.MessageId);
            Assert.True(
                delivery.Settlement.Kind == TransportSettlementKind.DeadLetter && delivery.Settlement.DeadLetterReason!.Code == reason && delivery.Message.DeliveryCount == 1,
                $"{name}: {delivery.Settlement.Kind} {delivery.Settlement.DeadLetterReason?.Code} on delivery {delivery.Message.DeliveryCount}");
        }

        // A genuine message of A processed inside B's scope is refused before anything is read.
        var genuine = TransportEnvelope.FromOutbox(
            await TransportData.OutboxIdForReceiptAsync(fixture, receiptA2, Token), tenantA, IngressMessageTypes.ReceiptReceived, TransportData.Marker(receiptA2), CorrelationId.New()).Value;
        var foreignScope = await Task.Run(
            async () =>
            {
                await using var scope = path.Processing.Services.CreateAsyncScope();
                path.Processing.TenantContextAccessor.SetResolved(TenantContext.Resolved(tenantB, TenantResolutionSource.TrustedServiceIdentity));
                return await scope.ServiceProvider.GetRequiredService<IEventProcessor>().ProcessAsync(genuine, Token);
            },
            Token);
        Assert.Equal(ProcessingErrors.TenantMismatch, foreignScope.Error);

        // Nothing forged changed either tenant, and A's unprocessed receipt still waits for its genuine message.
        await AssertUnchangedAsync(beforeA, tenantA);
        await AssertUnchangedAsync(beforeB, tenantB);
        Assert.Equal("Received", await ProcessingHarness.ReceiptStateAsync(fixture, receiptA2));

        // The genuine message then processes in A alone.
        await path.FlushAsync();
        await path.AssertFactsAsync(tenantA, GroundTruth.Of([a1, a2]));
        await path.AssertFactsAsync(tenantB, GroundTruth.Of([b1]));
        var accountingA = await path.AssertNothingSilentlyLostAsync(tenantA, [receiptA1, receiptA2]);
        var accountingB = await path.AssertNothingSilentlyLostAsync(tenantB, [receiptB1]);

        // The forgeries stay visible under the tenant each claimed, naming receipts that are not that tenant's own.
        Assert.Equal([receiptB1], accountingA.DeadLetters.Select(letter => letter.Envelope!.CausationId));
        Assert.Equal(new[] { receiptA1, receiptA2 }.Order(), accountingB.DeadLetters.Select(letter => letter.Envelope!.CausationId).Order());
    }

    private static IReadOnlyList<SourceEvent> Shared(string valuePrefix) =>
    [
        Stated("evt-shared-1", "2001", 1, ("status", "TaskStatus", $"{valuePrefix}-working")),
        Stated("evt-shared-2", "2001", 2, ("status", "TaskStatus", $"{valuePrefix}-review"), ("due_date", "Deadline", $"{valuePrefix}-2026-10-01")),
        Stated("evt-shared-3", "2001", 3, ("due_date", "Deadline", $"{valuePrefix}-2026-10-05")),
        Stated("evt-shared-4", "2002", 1, ("status", "TaskStatus", $"{valuePrefix}-blocked")),
        Stated("evt-shared-5", "2002", 2, ("status", "TaskStatus", $"{valuePrefix}-done")),
    ];

    private static SourceEvent Stated(string id, string objectId, long revision, params (string Field, string DataType, string Value)[] changes) =>
        new(id, new SyntheticEvent
        {
            ObjectId = objectId,
            Revision = revision,
            UpdatedAt = T0.AddMinutes(revision),
            Changes = [.. changes.Select(change => new SyntheticChange(change.Field, change.DataType, null, change.Value))],
        });

    // The event's payload with properties naming another tenant, as a hostile or confused source could send.
    private static string NamingAnotherTenant(SourceEvent stated, TenantId other)
    {
        var payload = JsonNode.Parse(stated.Event.ToPayload())!.AsObject();
        payload["tenantId"] = other.ToString();
        payload["tenant"] = other.ToString();
        return payload.ToJsonString();
    }

    private static TransportWireMessage Forge(TenantId claimed, Guid receipt) =>
        TransportEnvelope.FromOutbox(Guid.CreateVersion7(), claimed, IngressMessageTypes.ReceiptReceived, TransportData.Marker(receipt), CorrelationId.New()).Value.ToWire();

    private async Task SeedUserAsync(TenantId tenant)
    {
        var (database, context) = fixture.CreateTenantScoped(tenant);
        await using (context)
        {
            var written = await database.ExecuteAsync(
                async (dbContext, token) => await dbContext.Users.AddAsync(
                    UserRecord.Create(UserId.New(), tenant, new ExternalIdentity(IdentityProviderId.EntraId, "subject-" + Guid.NewGuid().ToString("N")), DateTimeOffset.UtcNow),
                    token),
                Token);
            Assert.True(written.IsSuccess, written.IsSuccess ? string.Empty : written.Error.Code);
        }
    }

    private async Task AssertSeesOnlyItsOwnAsync(TenantId tenant)
    {
        var (database, context) = fixture.CreateTenantScoped(tenant);
        await using (context)
        {
            var seen = await database.ExecuteAsync(
                async (dbContext, token) => new (string Table, List<TenantId> Owners)[]
                {
                    ("tenants", await dbContext.Tenants.AsNoTracking().Select(row => row.TenantId).ToListAsync(token)),
                    ("connections", await dbContext.Connections.AsNoTracking().Select(row => row.TenantId).ToListAsync(token)),
                    ("raw_receipts", await dbContext.RawReceipts.AsNoTracking().Select(row => row.TenantId).ToListAsync(token)),
                    ("outbox_messages", await dbContext.OutboxMessages.AsNoTracking().Select(row => row.TenantId).ToListAsync(token)),
                    ("event_ledger", await dbContext.EventLedger.AsNoTracking().Select(row => row.TenantId).ToListAsync(token)),
                    ("event_ledger_fields", await dbContext.EventLedgerFields.AsNoTracking().Select(row => row.TenantId).ToListAsync(token)),
                    ("field_ordering_states", await dbContext.FieldOrderingStates.AsNoTracking().Select(row => row.TenantId).ToListAsync(token)),
                    ("snapshot_objects", await dbContext.SnapshotObjects.AsNoTracking().Select(row => row.TenantId).ToListAsync(token)),
                    ("snapshot_fields", await dbContext.SnapshotFields.AsNoTracking().Select(row => row.TenantId).ToListAsync(token)),
                },
                Token);

            Assert.True(seen.IsSuccess, seen.IsSuccess ? string.Empty : seen.Error.Code);
            Assert.All(seen.Value, table =>
            {
                Assert.NotEmpty(table.Owners);
                Assert.All(table.Owners, owner => Assert.Equal(tenant, owner));
            });
        }
    }

    // Runs one statement as the runtime role in the tenant's own scope, then rolls back whatever it did.
    private async Task<(int Affected, string? SqlState)> AttemptAsync(NpgsqlConnection runtime, TenantId self, TenantId other, string sql)
    {
        await using var transaction = await runtime.BeginTransactionAsync(Token);
        try
        {
            await ScopeAsync(runtime, transaction, self);
            await using var command = new NpgsqlCommand(sql, runtime, transaction);
            command.Parameters.AddWithValue("self", self.Value);
            command.Parameters.AddWithValue("other", other.Value);
            return (await command.ExecuteNonQueryAsync(Token), null);
        }
        catch (PostgresException exception)
        {
            return (0, exception.SqlState);
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }
    }

    private async Task<long> CountAsync(NpgsqlConnection runtime, TenantId self, TenantId other, string sql)
    {
        await using var transaction = await runtime.BeginTransactionAsync(Token);
        try
        {
            await ScopeAsync(runtime, transaction, self);
            await using var command = new NpgsqlCommand(sql, runtime, transaction);
            command.Parameters.AddWithValue("self", self.Value);
            command.Parameters.AddWithValue("other", other.Value);
            return (long)(await command.ExecuteScalarAsync(Token))!;
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }
    }

    private async Task ScopeAsync(NpgsqlConnection runtime, NpgsqlTransaction transaction, TenantId tenant)
    {
        await using var command = new NpgsqlCommand("SELECT set_config(@variable, @tenant, true);", runtime, transaction);
        command.Parameters.AddWithValue("variable", WorkContinuityDbContext.TenantSessionVariable);
        command.Parameters.AddWithValue("tenant", tenant.Value.ToString("D"));
        await command.ExecuteNonQueryAsync(Token);
    }

    // Every tenant-owned table's rows of the given tenants, counted with the schema owner.
    private async Task<IReadOnlyList<(string Table, Guid Tenant, long Rows)>> RowCountsAsync(IReadOnlyList<TenantId> tenants)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        var counts = new List<(string, Guid, long)>();
        foreach (var table in WorkContinuityModel.TenantOwnedTables)
        {
            foreach (var tenant in tenants)
            {
                await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE tenant_id = @tenant;", connection);
                command.Parameters.AddWithValue("tenant", tenant.Value);
                counts.Add((table, tenant.Value, (long)(await command.ExecuteScalarAsync(Token))!));
            }
        }

        return counts;
    }

    private async Task<TenantState> TenantStateAsync(TenantId tenant) => new(
        await ProcessingHarness.LedgerAsync(fixture, tenant),
        await ProcessingHarness.FieldStatesAsync(fixture, tenant),
        await ProcessingHarness.SnapshotObjectsAsync(fixture, tenant),
        await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant));

    private async Task AssertUnchangedAsync(TenantState before, TenantId tenant)
    {
        var after = await TenantStateAsync(tenant);
        Assert.NotEmpty(before.Ledger);
        Assert.Equal(before.Ledger, after.Ledger);
        Assert.Equal(before.States, after.States);
        Assert.Equal(before.Objects, after.Objects);
        Assert.Equal(before.Facts, after.Facts);
    }

    private sealed record TenantState(
        IReadOnlyList<LedgerRow> Ledger,
        IReadOnlyList<FieldStateRow> States,
        IReadOnlyList<SnapshotObjectRow> Objects,
        IReadOnlyList<SnapshotFieldRow> Facts);
}
