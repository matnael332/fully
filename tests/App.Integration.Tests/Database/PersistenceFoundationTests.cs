using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Integration.Tests.Database;

/// <summary>
/// Proves migration, deduplication, transactional outbox, UTC and pgvector behaviour against the
/// real database.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PersistenceFoundationTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_database_migrates_to_the_current_schema_and_re_running_is_a_no_op()
    {
        var migrator = DatabaseMigrator.ForConnection(
            fixture.AdministrativeConnectionString,
            NullLogger<DatabaseMigrator>.Instance);

        // The fixture already migrated from zero; this proves the operation is idempotent, which is
        // what makes it safe to run on every deployment.
        var status = await migrator.GetStatusAsync(Token);

        Assert.Empty(status.Pending);
        Assert.Contains(status.Applied, id => id.EndsWith("InitialGate1DSchema", StringComparison.Ordinal));
        Assert.Contains(
            status.Applied,
            id => id.EndsWith("TenantIsolationAndVectorExtension", StringComparison.Ordinal));

        await migrator.MigrateAsync(Token);

        var afterSecondRun = await migrator.GetStatusAsync(Token);
        Assert.Empty(afterSecondRun.Pending);
        Assert.Equal(status.Applied, afterSecondRun.Applied);
    }

    [Fact]
    public async Task Every_expected_table_exists()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT tablename FROM pg_tables
            WHERE schemaname = 'public' AND tablename = ANY(@tables)
            ORDER BY tablename;
            """;
        command.Parameters.AddWithValue("tables", WorkContinuityModel.TenantOwnedTables.ToArray());

        var found = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            found.Add(reader.GetString(0));
        }

        Assert.Equal(
            WorkContinuityModel.TenantOwnedTables.OrderBy(name => name, StringComparer.Ordinal),
            found);
    }

    [Fact]
    public async Task A_duplicate_source_event_id_is_rejected_for_the_same_tenant_and_connection()
    {
        var tenant = TenantId.New();
        await fixture.SeedTenantAsync(tenant, Token);

        const string connectionReference = "monday-connection-dedup";
        var sourceEventId = "evt-" + Guid.CreateVersion7().ToString("N");

        var first = await AddReceiptAsync(tenant, Guid.CreateVersion7(), connectionReference, sourceEventId);
        Assert.True(first.IsSuccess);

        // Same tenant, same connection, same provider event id, different receipt id.
        var second = await AddReceiptAsync(tenant, Guid.CreateVersion7(), connectionReference, sourceEventId);

        Assert.True(second.IsFailure);
        Assert.Equal(PersistenceErrors.Duplicate.Code, second.Error.Code);
    }

    [Fact]
    public async Task The_same_source_event_id_remains_independent_in_another_tenant()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        await fixture.SeedTenantAsync(tenantA, Token);
        await fixture.SeedTenantAsync(tenantB, Token);

        const string connectionReference = "monday-connection-crosstenant";
        var sourceEventId = "evt-" + Guid.CreateVersion7().ToString("N");

        var inA = await AddReceiptAsync(tenantA, Guid.CreateVersion7(), connectionReference, sourceEventId);
        var inB = await AddReceiptAsync(tenantB, Guid.CreateVersion7(), connectionReference, sourceEventId);

        // Deduplication is scoped per tenant: one tenant's ids cannot collide with another's.
        Assert.True(inA.IsSuccess);
        Assert.True(inB.IsSuccess);
    }

    [Fact]
    public async Task Receipts_without_a_source_event_id_are_not_constrained_against_each_other()
    {
        var tenant = TenantId.New();
        await fixture.SeedTenantAsync(tenant, Token);

        const string connectionReference = "monday-connection-nullevent";

        // The unique index is filtered, so an absent provider event id never collides. A payload
        // fingerprint is only ever a duplicate *candidate*, never an authoritative key, because two
        // genuinely distinct events can be byte-identical.
        var first = await AddReceiptAsync(tenant, Guid.CreateVersion7(), connectionReference, sourceEventId: null);
        var second = await AddReceiptAsync(tenant, Guid.CreateVersion7(), connectionReference, sourceEventId: null);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task A_receipt_and_its_outbox_message_commit_together()
    {
        var tenant = TenantId.New();
        await fixture.SeedTenantAsync(tenant, Token);

        var receiptId = Guid.CreateVersion7();
        var outboxId = Guid.CreateVersion7();

        var (database, context) = fixture.CreateTenantScoped(tenant);
        await using (context)
        {
            var result = await database.ExecuteAsync(
                async (dbContext, token) =>
                {
                    await dbContext.RawReceipts.AddAsync(
                        NewReceipt(receiptId, tenant, "monday-connection-outbox", "evt-" + receiptId.ToString("N")),
                        token);

                    await dbContext.OutboxMessages.AddAsync(
                        OutboxMessageRecord.Queue(
                            outboxId,
                            tenant,
                            "receipt.received",
                            """{"receipt":"queued"}""",
                            DateTimeOffset.UtcNow,
                            CorrelationId.New()),
                        token);
                },
                Token);

            Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
        }

        Assert.True(await ExistsAsync("raw_receipts", "receipt_id", receiptId));
        Assert.True(await ExistsAsync("outbox_messages", "outbox_message_id", outboxId));
    }

    [Fact]
    public async Task A_failure_after_both_writes_leaves_neither_the_receipt_nor_the_outbox_message()
    {
        var tenant = TenantId.New();
        await fixture.SeedTenantAsync(tenant, Token);

        var receiptId = Guid.CreateVersion7();
        var outboxId = Guid.CreateVersion7();

        var (database, context) = fixture.CreateTenantScoped(tenant);
        await using (context)
        {
            var result = await database.ExecuteAsync(
                async (dbContext, token) =>
                {
                    await dbContext.RawReceipts.AddAsync(
                        NewReceipt(receiptId, tenant, "monday-connection-rollback", "evt-" + receiptId.ToString("N")),
                        token);

                    await dbContext.OutboxMessages.AddAsync(
                        OutboxMessageRecord.Queue(
                            outboxId,
                            tenant,
                            "receipt.received",
                            """{"receipt":"queued"}""",
                            DateTimeOffset.UtcNow,
                            CorrelationId.New()),
                        token);

                    // Force the transaction to unwind after both entities were staged.
                    throw new InvalidOperationException("Simulated downstream failure.");
                },
                Token);

            Assert.True(result.IsFailure);
            Assert.Equal(PersistenceErrors.Unavailable.Code, result.Error.Code);
        }

        // Neither write survives: the state change and its notification cannot diverge.
        Assert.False(await ExistsAsync("raw_receipts", "receipt_id", receiptId));
        Assert.False(await ExistsAsync("outbox_messages", "outbox_message_id", outboxId));
    }

    [Fact]
    public async Task A_rejected_outbox_insert_also_rolls_back_the_receipt()
    {
        var tenant = TenantId.New();
        var otherTenant = TenantId.New();
        await fixture.SeedTenantAsync(tenant, Token);
        await fixture.SeedTenantAsync(otherTenant, Token);

        var receiptId = Guid.CreateVersion7();

        var (database, context) = fixture.CreateTenantScoped(tenant);
        await using (context)
        {
            var result = await database.ExecuteAsync(
                async (dbContext, token) =>
                {
                    await dbContext.RawReceipts.AddAsync(
                        NewReceipt(receiptId, tenant, "monday-connection-rls-rollback", "evt-" + receiptId.ToString("N")),
                        token);

                    // Labelled with the wrong tenant, so row-level security rejects this row.
                    await dbContext.OutboxMessages.AddAsync(
                        OutboxMessageRecord.Queue(
                            Guid.CreateVersion7(),
                            otherTenant,
                            "receipt.received",
                            """{"receipt":"queued"}""",
                            DateTimeOffset.UtcNow,
                            CorrelationId.New()),
                        token);
                },
                Token);

            Assert.True(result.IsFailure);
            Assert.Equal(PersistenceErrors.TenantIsolationViolation.Code, result.Error.Code);
        }

        Assert.False(await ExistsAsync("raw_receipts", "receipt_id", receiptId));
    }

    [Fact]
    public async Task Timestamps_round_trip_as_utc()
    {
        var tenant = TenantId.New();
        await fixture.SeedTenantAsync(tenant, Token);

        // A non-UTC input must be normalised, not stored with its original offset.
        var localInstant = new DateTimeOffset(2026, 3, 4, 9, 30, 0, TimeSpan.FromHours(3));
        var expectedUtc = localInstant.ToUniversalTime();

        var receiptId = Guid.CreateVersion7();

        var (writeDatabase, writeContext) = fixture.CreateTenantScoped(tenant);
        await using (writeContext)
        {
            var written = await writeDatabase.ExecuteAsync(
                async (dbContext, token) => await dbContext.RawReceipts.AddAsync(
                    RawReceiptRecord.Receive(
                        receiptId,
                        tenant,
                        "monday-connection-utc",
                        "monday",
                        "evt-" + receiptId.ToString("N"),
                        "1",
                        localInstant,
                        """{"kind":"utc"}""",
                        "fingerprint-" + receiptId.ToString("N")),
                    token),
                Token);

            Assert.True(written.IsSuccess, written.IsSuccess ? "" : written.Error.Code);
        }

        var (readDatabase, readContext) = fixture.CreateTenantScoped(tenant);
        await using (readContext)
        {
            var read = await readDatabase.ExecuteAsync(
                async (dbContext, token) => await dbContext.RawReceipts
                    .AsNoTracking()
                    .Where(receipt => receipt.ReceiptId == receiptId)
                    .Select(receipt => receipt.ReceivedAtUtc)
                    .SingleAsync(token),
                Token);

            Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Error.Code);
            Assert.Equal(TimeSpan.Zero, read.Value.Offset);
            Assert.Equal(expectedUtc, read.Value);
            Assert.Equal(6, read.Value.Hour);
        }
    }

    [Fact]
    public async Task Timestamp_columns_use_a_timezone_aware_type()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT table_name, column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND column_name LIKE '%_at_utc'
            ORDER BY table_name, column_name;
            """;

        var inspected = 0;
        await using var reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            inspected++;

            Assert.Equal(
                "timestamp with time zone",
                reader.GetString(2));
        }

        Assert.True(inspected >= 5, $"Expected the UTC columns to be inspected, saw {inspected}.");
    }

    [Fact]
    public async Task The_pgvector_extension_is_available_and_usable()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);

        await using (var extensionCommand = connection.CreateCommand())
        {
            extensionCommand.CommandText = "SELECT extversion FROM pg_extension WHERE extname = 'vector';";

            var version = await extensionCommand.ExecuteScalarAsync(Token) as string;

            Assert.False(
                string.IsNullOrWhiteSpace(version),
                "The pgvector extension is not installed in the local database.");
        }

        // A real vector operation, not merely the extension's presence. No embedding table is
        // created: semantic retrieval belongs to a later Gate, and a speculative schema now would be
        // dead weight.
        await using var vectorCommand = connection.CreateCommand();
        vectorCommand.CommandText =
            "SELECT ('[1,2,3]'::vector <-> '[1,2,4]'::vector)::float8, vector_dims('[1,2,3]'::vector);";

        await using var reader = await vectorCommand.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(1.0, reader.GetDouble(0), precision: 6);
        Assert.Equal(3, reader.GetInt32(1));
    }

    [Fact]
    public async Task The_raw_receipt_retention_basis_is_indexed()
    {
        // A rolling 30-day purge of raw work payloads needs to find old rows cheaply. This does not
        // implement the purge; it makes sure the later job will not table-scan.
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'raw_receipts'
            ORDER BY indexname;
            """;

        var indexes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            indexes.Add(reader.GetString(0));
        }

        Assert.Contains("ix_raw_receipts_received_at_utc", indexes);
        Assert.Contains("ux_raw_receipts_tenant_connection_source_event", indexes);
    }

    [Fact]
    public async Task A_canonical_identity_round_trips_without_an_email()
    {
        var tenant = TenantId.New();
        await fixture.SeedTenantAsync(tenant, Token);

        var userId = UserId.New();
        var external = new ExternalIdentity(IdentityProviderId.EntraId, "subject-" + userId.Value.ToString("N"));

        var (writeDatabase, writeContext) = fixture.CreateTenantScoped(tenant);
        await using (writeContext)
        {
            var written = await writeDatabase.ExecuteAsync(
                async (dbContext, token) => await dbContext.Users.AddAsync(
                    UserRecord.Create(userId, tenant, external, DateTimeOffset.UtcNow),
                    token),
                Token);

            Assert.True(written.IsSuccess, written.IsSuccess ? "" : written.Error.Code);
        }

        var (readDatabase, readContext) = fixture.CreateTenantScoped(tenant);
        await using (readContext)
        {
            var read = await readDatabase.ExecuteAsync(
                async (dbContext, token) => await dbContext.Users
                    .AsNoTracking()
                    .SingleAsync(user => user.UserId == userId, token),
                Token);

            Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Error.Code);
            Assert.Equal(external, read.Value.ToExternalIdentity());
            Assert.Equal(tenant, read.Value.TenantId);
            Assert.True(read.Value.IsActive);
        }
    }

    [Fact]
    public async Task One_provider_identity_cannot_map_to_two_users_in_a_tenant()
    {
        var tenant = TenantId.New();
        await fixture.SeedTenantAsync(tenant, Token);

        var subject = "subject-" + Guid.CreateVersion7().ToString("N");
        var external = new ExternalIdentity(IdentityProviderId.EntraId, subject);

        var first = await AddUserAsync(tenant, UserId.New(), external);
        var second = await AddUserAsync(tenant, UserId.New(), external);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsFailure);
        Assert.Equal(PersistenceErrors.Duplicate.Code, second.Error.Code);
    }

    private async Task<Core.Results.Result<int>> AddReceiptAsync(
        TenantId tenant,
        Guid receiptId,
        string connectionReference,
        string? sourceEventId)
    {
        var (database, context) = fixture.CreateTenantScoped(tenant);

        await using (context)
        {
            return await database.ExecuteAsync(
                async (dbContext, token) =>
                {
                    await dbContext.RawReceipts.AddAsync(
                        NewReceipt(receiptId, tenant, connectionReference, sourceEventId),
                        token);

                    return 1;
                },
                Token);
        }
    }

    private async Task<Core.Results.Result<int>> AddUserAsync(
        TenantId tenant,
        UserId userId,
        ExternalIdentity external)
    {
        var (database, context) = fixture.CreateTenantScoped(tenant);

        await using (context)
        {
            return await database.ExecuteAsync(
                async (dbContext, token) =>
                {
                    await dbContext.Users.AddAsync(
                        UserRecord.Create(userId, tenant, external, DateTimeOffset.UtcNow),
                        token);

                    return 1;
                },
                Token);
        }
    }

    private static RawReceiptRecord NewReceipt(
        Guid receiptId,
        TenantId tenantId,
        string connectionReference,
        string? sourceEventId) =>
        RawReceiptRecord.Receive(
            receiptId,
            tenantId,
            connectionReference,
            sourceSystem: "monday",
            sourceEventId: sourceEventId,
            schemaVersion: "1",
            receivedAtUtc: DateTimeOffset.UtcNow,
            payload: """{"kind":"test"}""",
            payloadFingerprint: "fingerprint-" + receiptId.ToString("N"));

    private async Task<bool> ExistsAsync(string table, string keyColumn, Guid value)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = connection.CreateCommand();

        // Table and column are compile-time constants from this test, never caller input.
        command.CommandText = $"SELECT count(*) FROM {table} WHERE {keyColumn} = @value;";
        command.Parameters.AddWithValue("value", value);

        return (long)(await command.ExecuteScalarAsync(Token))! > 0;
    }
}
