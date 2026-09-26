using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Integration.Tests.Database;

/// <summary>
/// Proves that a tenant's database session context cannot survive on a pooled connection and be
/// observed by the next tenant to borrow it.
/// </summary>
/// <remarks>
/// This is the failure mode that makes naive RLS implementations unsafe: setting the tenant with a
/// plain <c>SET</c> leaves it on the physical connection, so whoever borrows that connection next
/// inherits it. The implementation uses <c>set_config(..., is_local =&gt; true)</c>, which PostgreSQL
/// discards when the transaction ends. These tests deliberately keep the pool tiny so the same
/// physical connection is guaranteed to be reused.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ConnectionPoolIsolationTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A single pooled connection, so reuse is certain rather than likely.</summary>
    private string SingleConnectionPool =>
        new NpgsqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            Pooling = true,
            MinPoolSize = 1,
            MaxPoolSize = 1,
            ApplicationName = "gate1d-pool-isolation",
        }.ConnectionString;

    [Fact]
    public async Task A_tenant_setting_does_not_survive_on_a_returned_pooled_connection()
    {
        var tenantA = TenantId.New();
        var connectionString = SingleConnectionPool;

        // Borrow the single pooled connection, set the tenant inside a transaction, commit, release.
        await using (var first = new NpgsqlConnection(connectionString))
        {
            await first.OpenAsync(Token);
            await using var transaction = await first.BeginTransactionAsync(Token);

            await using var setCommand = first.CreateCommand();
            setCommand.Transaction = transaction;
            setCommand.CommandText = "SELECT set_config('app.tenant_id', @tenant, true);";
            setCommand.Parameters.AddWithValue("tenant", tenantA.Value.ToString("D"));
            await setCommand.ExecuteNonQueryAsync(Token);

            await using var readBack = first.CreateCommand();
            readBack.Transaction = transaction;
            readBack.CommandText = "SELECT current_setting('app.tenant_id', true);";
            Assert.Equal(tenantA.Value.ToString("D"), await readBack.ExecuteScalarAsync(Token));

            await transaction.CommitAsync(Token);
        }

        // The same physical connection comes back out of the pool.
        await using var second = new NpgsqlConnection(connectionString);
        await second.OpenAsync(Token);

        await using var inspect = second.CreateCommand();
        inspect.CommandText = "SELECT current_setting('app.tenant_id', true);";

        var leaked = await inspect.ExecuteScalarAsync(Token);

        Assert.True(
            leaked is null or DBNull or "",
            $"Tenant context leaked through the connection pool: '{leaked}'.");
    }

    [Fact]
    public async Task Tenant_B_cannot_see_tenant_A_rows_after_reusing_the_same_pooled_connection()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        await fixture.SeedTenantAsync(tenantA, Token);
        await fixture.SeedTenantAsync(tenantB, Token);

        var connectionString = SingleConnectionPool;
        var receiptId = Guid.CreateVersion7();

        // Tenant A writes, using the pool.
        await RunAsTenantAsync(connectionString, tenantA, async (database, _) =>
        {
            var result = await database.ExecuteAsync(
                async (context, token) => await context.RawReceipts.AddAsync(
                    RawReceiptRecord.Receive(
                        receiptId,
                        tenantA,
                        "monday-connection-1",
                        "monday",
                        "evt-pool-" + receiptId.ToString("N"),
                        "1",
                        DateTimeOffset.UtcNow,
                        """{"kind":"pool"}""",
                        "fingerprint-" + receiptId.ToString("N")),
                    token),
                Token);

            Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
        });

        // Tenant B now borrows the very same physical connection.
        await RunAsTenantAsync(connectionString, tenantB, async (database, _) =>
        {
            var visible = await database.ExecuteAsync(
                async (context, token) => await context.RawReceipts
                    .AsNoTracking()
                    .Where(receipt => receipt.ReceiptId == receiptId)
                    .ToListAsync(token),
                Token);

            Assert.True(visible.IsSuccess, visible.IsSuccess ? "" : visible.Error.Code);
            Assert.Empty(visible.Value);
        });

        // And tenant A still sees its own row over that same connection.
        await RunAsTenantAsync(connectionString, tenantA, async (database, _) =>
        {
            var visible = await database.ExecuteAsync(
                async (context, token) => await context.RawReceipts
                    .AsNoTracking()
                    .Where(receipt => receipt.ReceiptId == receiptId)
                    .ToListAsync(token),
                Token);

            Assert.True(visible.IsSuccess, visible.IsSuccess ? "" : visible.Error.Code);
            Assert.Single(visible.Value);
        });
    }

    [Fact]
    public async Task An_unresolved_tenant_sees_nothing_on_a_connection_a_tenant_just_used()
    {
        var tenantA = TenantId.New();
        await fixture.SeedTenantAsync(tenantA, Token);

        var connectionString = SingleConnectionPool;
        var receiptId = Guid.CreateVersion7();

        await RunAsTenantAsync(connectionString, tenantA, async (database, _) =>
        {
            var result = await database.ExecuteAsync(
                async (context, token) => await context.RawReceipts.AddAsync(
                    RawReceiptRecord.Receive(
                        receiptId,
                        tenantA,
                        "monday-connection-1",
                        "monday",
                        "evt-unresolved-" + receiptId.ToString("N"),
                        "1",
                        DateTimeOffset.UtcNow,
                        """{"kind":"unresolved"}""",
                        "fingerprint-" + receiptId.ToString("N")),
                    token),
                Token);

            Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
        });

        // A raw borrow of the same pooled connection, with no tenant set at all.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM raw_receipts;";

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(Token))!);
    }

    private static async Task RunAsTenantAsync(
        string connectionString,
        TenantId tenantId,
        Func<ITenantScopedDatabase, WorkContinuityDbContext, Task> work)
    {
        var options = new DbContextOptionsBuilder<WorkContinuityDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var context = new WorkContinuityDbContext(options);

        var database = new TenantScopedDatabase(
            context,
            new FixedTenantAccessor(
                TenantContext.Resolved(tenantId, TenantResolutionSource.AuthenticatedPrincipal)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantScopedDatabase>.Instance);

        await work(database, context);
    }

    private sealed class FixedTenantAccessor(ITenantContext current) : ITenantContextAccessor
    {
        public ITenantContext Current { get; } = current;

        public void SetResolved(ITenantContext tenantContext) =>
            throw new NotSupportedException("The test supplies a fixed tenant context.");
    }
}
