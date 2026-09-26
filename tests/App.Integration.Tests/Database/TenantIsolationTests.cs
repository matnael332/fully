using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Integration.Tests.Database;

/// <summary>
/// Proves storage-level tenant isolation against the real database.
/// </summary>
/// <remarks>
/// Application authorization is the first boundary; these tests cover the second. Every assertion
/// here runs as the non-owner runtime role, because a PostgreSQL superuser bypasses row-level
/// security outright and would make the whole suite pass vacuously.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class TenantIsolationTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_runtime_role_is_not_a_superuser_and_cannot_bypass_row_level_security()
    {
        // Without this, every other test in this file would pass for the wrong reason.
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT rolsuper, rolbypassrls
            FROM pg_roles
            WHERE rolname = @role;
            """;
        command.Parameters.AddWithValue("role", PersistenceOptions.RuntimeRoleName);

        await using var reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token), "The runtime role does not exist.");
        Assert.False(reader.GetBoolean(0), "The runtime role must not be a superuser.");
        Assert.False(reader.GetBoolean(1), "The runtime role must not hold BYPASSRLS.");
    }

    [Fact]
    public async Task Tenant_B_cannot_read_a_row_owned_by_tenant_A()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        await fixture.SeedTenantAsync(tenantA, Token);
        await fixture.SeedTenantAsync(tenantB, Token);

        var receiptId = Guid.CreateVersion7();

        var (writeDatabase, writeContext) = fixture.CreateTenantScoped(tenantA);
        await using (writeContext)
        {
            var written = await writeDatabase.ExecuteAsync(
                async (context, token) =>
                {
                    await context.RawReceipts.AddAsync(NewReceipt(receiptId, tenantA, "evt-read"), token);
                },
                Token);

            Assert.True(written.IsSuccess, Describe(written));
        }

        var (readDatabase, readContext) = fixture.CreateTenantScoped(tenantB);
        await using (readContext)
        {
            var read = await readDatabase.ExecuteAsync(
                async (context, token) => await context.RawReceipts
                    .AsNoTracking()
                    .Where(receipt => receipt.ReceiptId == receiptId)
                    .ToListAsync(token),
                Token);

            Assert.True(read.IsSuccess, Describe(read));
            Assert.Empty(read.Value);
        }

        // Tenant A still sees its own row, so the policy filters rather than hides everything.
        var (ownerDatabase, ownerContext) = fixture.CreateTenantScoped(tenantA);
        await using (ownerContext)
        {
            var own = await ownerDatabase.ExecuteAsync(
                async (context, token) => await context.RawReceipts
                    .AsNoTracking()
                    .Where(receipt => receipt.ReceiptId == receiptId)
                    .ToListAsync(token),
                Token);

            Assert.True(own.IsSuccess, Describe(own));
            Assert.Single(own.Value);
        }
    }

    [Fact]
    public async Task Tenant_B_cannot_update_a_row_owned_by_tenant_A()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        await fixture.SeedTenantAsync(tenantA, Token);
        await fixture.SeedTenantAsync(tenantB, Token);

        var receiptId = Guid.CreateVersion7();
        await InsertReceiptAsync(tenantA, receiptId, "evt-update");

        var (database, context) = fixture.CreateTenantScoped(tenantB);
        await using (context)
        {
            var result = await database.ExecuteAsync(
                async (dbContext, token) => await dbContext.RawReceipts
                    .Where(receipt => receipt.ReceiptId == receiptId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            receipt => receipt.ProcessingState,
                            RawReceiptState.Quarantined),
                        token),
                Token);

            Assert.True(result.IsSuccess, Describe(result));

            // The statement is legal but matches no visible row, so nothing is modified.
            Assert.Equal(0, result.Value);
        }

        Assert.Equal(RawReceiptState.Received.ToString(), await ReadStateAsync(receiptId));
    }

    [Fact]
    public async Task Tenant_B_cannot_delete_a_row_owned_by_tenant_A()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        await fixture.SeedTenantAsync(tenantA, Token);
        await fixture.SeedTenantAsync(tenantB, Token);

        var receiptId = Guid.CreateVersion7();
        await InsertReceiptAsync(tenantA, receiptId, "evt-delete");

        var (database, context) = fixture.CreateTenantScoped(tenantB);
        await using (context)
        {
            var result = await database.ExecuteAsync(
                async (dbContext, token) => await dbContext.RawReceipts
                    .Where(receipt => receipt.ReceiptId == receiptId)
                    .ExecuteDeleteAsync(token),
                Token);

            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal(0, result.Value);
        }

        Assert.True(await ReceiptExistsAsync(receiptId), "Tenant A's row was deleted by tenant B.");
    }

    [Fact]
    public async Task A_trusted_tenant_A_session_cannot_insert_a_row_labelled_tenant_B()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        await fixture.SeedTenantAsync(tenantA, Token);
        await fixture.SeedTenantAsync(tenantB, Token);

        var receiptId = Guid.CreateVersion7();

        var (database, context) = fixture.CreateTenantScoped(tenantA);
        await using (context)
        {
            var result = await database.ExecuteAsync(
                async (dbContext, token) =>
                    await dbContext.RawReceipts.AddAsync(
                        NewReceipt(receiptId, tenantB, "evt-mismatch"),
                        token),
                Token);

            // The WITH CHECK half of the policy rejects the write.
            Assert.True(result.IsFailure);
            Assert.Equal(PersistenceErrors.TenantIsolationViolation.Code, result.Error.Code);
            Assert.Equal(ErrorCategory.Forbidden, result.Error.Category);
        }

        Assert.False(await ReceiptExistsAsync(receiptId), "A mismatched row was persisted.");
    }

    [Fact]
    public async Task A_query_without_a_trusted_tenant_context_fails_closed()
    {
        var tenantA = TenantId.New();
        await fixture.SeedTenantAsync(tenantA, Token);
        await InsertReceiptAsync(tenantA, Guid.CreateVersion7(), "evt-failclosed");

        var (database, context) = fixture.CreateTenantScoped(TenantContext.Unresolved);
        await using (context)
        {
            var result = await database.ExecuteAsync(
                async (dbContext, token) => await dbContext.RawReceipts.AsNoTracking().ToListAsync(token),
                Token);

            // Refused before any statement is issued, and refused as a typed error rather than an
            // exception carrying database detail.
            Assert.True(result.IsFailure);
            Assert.Equal(PersistenceErrors.TenantContextMissing.Code, result.Error.Code);
        }
    }

    [Fact]
    public async Task Row_level_security_returns_no_rows_when_the_tenant_setting_is_absent()
    {
        // The layer beneath the application guard: even a raw connection that never sets
        // app.tenant_id sees nothing, so the policy defaults to deny rather than to "all rows".
        var tenantA = TenantId.New();
        await fixture.SeedTenantAsync(tenantA, Token);
        await InsertReceiptAsync(tenantA, Guid.CreateVersion7(), "evt-nosetting");

        await using var connection = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM raw_receipts;";

        var visible = (long)(await command.ExecuteScalarAsync(Token))!;

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task Every_tenant_owned_table_has_row_level_security_enabled_and_forced()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid) AS policy_count
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = ANY(@tables)
            ORDER BY c.relname;
            """;
        command.Parameters.AddWithValue("tables", WorkContinuityModel.TenantOwnedTables.ToArray());

        var inspected = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            var table = reader.GetString(0);
            inspected.Add(table);

            Assert.True(reader.GetBoolean(1), $"{table} does not have row-level security enabled.");
            Assert.True(reader.GetBoolean(2), $"{table} does not force row-level security.");
            Assert.True(reader.GetInt64(3) > 0, $"{table} has no isolation policy.");
        }

        Assert.Equal(
            WorkContinuityModel.TenantOwnedTables.OrderBy(name => name, StringComparer.Ordinal),
            inspected);
    }

    private static RawReceiptRecord NewReceipt(Guid receiptId, TenantId tenantId, string? sourceEventId) =>
        RawReceiptRecord.Receive(
            receiptId,
            tenantId,
            connectionReference: "monday-connection-1",
            sourceSystem: "monday",
            sourceEventId: sourceEventId,
            schemaVersion: "1",
            receivedAtUtc: DateTimeOffset.UtcNow,
            payload: """{"kind":"test"}""",
            payloadFingerprint: "fingerprint-" + receiptId.ToString("N"));

    private async Task InsertReceiptAsync(TenantId tenantId, Guid receiptId, string sourceEventId)
    {
        var (database, context) = fixture.CreateTenantScoped(tenantId);

        await using (context)
        {
            var result = await database.ExecuteAsync(
                async (dbContext, token) =>
                    await dbContext.RawReceipts.AddAsync(NewReceipt(receiptId, tenantId, sourceEventId), token),
                Token);

            Assert.True(result.IsSuccess, Describe(result));
        }
    }

    private async Task<string> ReadStateAsync(Guid receiptId)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT processing_state FROM raw_receipts WHERE receipt_id = @id;";
        command.Parameters.AddWithValue("id", receiptId);

        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task<bool> ReceiptExistsAsync(Guid receiptId)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM raw_receipts WHERE receipt_id = @id;";
        command.Parameters.AddWithValue("id", receiptId);

        return (long)(await command.ExecuteScalarAsync(Token))! > 0;
    }

    private static string Describe(Result result) =>
        result.IsSuccess ? "success" : $"{result.Error.Code}: {result.Error.Message}";

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Code}: {result.Error.Message}";
}
