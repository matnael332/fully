using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Reconciliation;

/// <summary>
/// Gate 3E's target table on databases created for the test, bootstrapped and then migrated from zero in the order the
/// rollout uses: the runtime role holds exactly read, insert and update; row-level security is enabled and forced with the
/// tenant policy and a restrictive policy that refuses every delete; a rerun of the unchanged global bootstrap re-grants
/// DELETE, which the restrictive policy still refuses and the recorded re-assertion removes; and the isolation checks are
/// not vacuous: with row-level security disabled, they see another tenant's targets.
/// </summary>
/// <remarks>
/// The shared test database cannot give this evidence: the suite reruns the global bootstrap there. Each test creates its
/// own database by an exact name, drops it first in case a stopped run left it behind, and drops it at the end.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ReconciliationTargetDatabaseSecurityTests(DatabaseFixture fixture)
{
    private const string ReassertPrivilegesSql = """
        REVOKE ALL PRIVILEGES ON reconciliation_targets FROM workcontinuity_app;
        GRANT SELECT, INSERT, UPDATE ON reconciliation_targets TO workcontinuity_app;
        """;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task After_bootstrap_then_migration_the_runtime_role_reads_inserts_and_updates_targets_under_forced_row_level_security()
    {
        await using var fresh = await FreshDatabase.CreateAsync(fixture, "wce_it_gate3e_targets", Token);
        await fresh.BootstrapAsync(Token);
        await fresh.MigrateAsync(Token);

        Assert.Equal("INSERT,SELECT,UPDATE", await fresh.ScalarAsync(Privileges, Token));
        Assert.Equal("true:true", await fresh.ScalarAsync("SELECT relrowsecurity || ':' || relforcerowsecurity FROM pg_class WHERE relname = 'reconciliation_targets';", Token));
        Assert.Equal(
            "no_delete_reconciliation_targets:RESTRICTIVE:DELETE,tenant_isolation_reconciliation_targets:PERMISSIVE:ALL",
            await fresh.ScalarAsync(
                "SELECT string_agg(policyname || ':' || permissive || ':' || cmd, ',' ORDER BY policyname) FROM pg_policies WHERE tablename = 'reconciliation_targets';",
                Token));

        // The intended path works as the runtime role; delete and truncate are refused by privilege.
        var tenant = await fresh.SeedTenantAsync(Token);
        Assert.Equal(1L, await fresh.RuntimeAsync(tenant, Insert(tenant, "1001"), Token, commit: true));
        Assert.Equal(1L, await fresh.RuntimeAsync(tenant, "UPDATE reconciliation_targets SET attempts = attempts + 1;", Token));
        Assert.Equal(1L, await fresh.RuntimeAsync(tenant, "SELECT count(*) FROM reconciliation_targets;", Token));
        foreach (var sql in new[] { "DELETE FROM reconciliation_targets;", "TRUNCATE reconciliation_targets;" })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => fresh.RuntimeAsync(tenant, sql, Token));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
    }

    [Fact]
    public async Task A_bootstrap_rerun_re_grants_delete_which_the_restrictive_policy_still_refuses_until_the_reassertion_removes_it()
    {
        await using var fresh = await FreshDatabase.CreateAsync(fixture, "wce_it_gate3e_rerun", Token);
        await fresh.BootstrapAsync(Token);
        await fresh.MigrateAsync(Token);
        var tenant = await fresh.SeedTenantAsync(Token);
        await fresh.RuntimeAsync(tenant, Insert(tenant, "1001"), Token, commit: true);

        // The hazard R3B-2 records for the Azure rollout: the unchanged bootstrap, rerun, re-grants DELETE.
        await fresh.BootstrapAsync(Token);
        Assert.Equal("DELETE,INSERT,SELECT,UPDATE", await fresh.ScalarAsync(Privileges, Token));

        // The restrictive policy keeps every target regardless: a delete finds nothing it may remove.
        Assert.Equal(0L, await fresh.RuntimeAsync(tenant, "DELETE FROM reconciliation_targets;", Token));
        Assert.Equal(1L, await fresh.RuntimeAsync(tenant, "SELECT count(*) FROM reconciliation_targets;", Token));

        // The recorded re-assertion restores exactly read, insert and update.
        await fresh.AdministrativeAsync(ReassertPrivilegesSql, Token);
        Assert.Equal("INSERT,SELECT,UPDATE", await fresh.ScalarAsync(Privileges, Token));
    }

    [Fact]
    public async Task Another_tenant_sees_and_changes_no_target_and_the_check_is_not_vacuous()
    {
        await using var fresh = await FreshDatabase.CreateAsync(fixture, "wce_it_gate3e_isolation", Token);
        await fresh.BootstrapAsync(Token);
        await fresh.MigrateAsync(Token);
        var tenantA = await fresh.SeedTenantAsync(Token);
        var tenantB = await fresh.SeedTenantAsync(Token);
        await fresh.RuntimeAsync(tenantA, Insert(tenantA, "1001"), Token, commit: true);

        var seenByB = $"SELECT count(*) FROM reconciliation_targets WHERE tenant_id = '{tenantA.Value:D}';";
        Assert.Equal(1L, await fresh.RuntimeAsync(tenantA, seenByB, Token));
        Assert.Equal(0L, await fresh.RuntimeAsync(tenantB, seenByB, Token));
        Assert.Equal(0L, await fresh.RuntimeAsync(tenantB, $"UPDATE reconciliation_targets SET state = 'Held' WHERE tenant_id = '{tenantA.Value:D}';", Token));
        var labelled = await Assert.ThrowsAsync<PostgresException>(() => fresh.RuntimeAsync(tenantB, Insert(tenantA, "1002"), Token));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, labelled.SqlState);

        // Vacuity: without row-level security the same check sees the other tenant's target, so it would have failed.
        await fresh.AdministrativeAsync("ALTER TABLE reconciliation_targets NO FORCE ROW LEVEL SECURITY; ALTER TABLE reconciliation_targets DISABLE ROW LEVEL SECURITY;", Token);
        Assert.Equal(1L, await fresh.RuntimeAsync(tenantB, seenByB, Token));
    }

    private const string Privileges = """
        SELECT string_agg(p.privilege, ',' ORDER BY p.privilege)
        FROM (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER')) AS p(privilege)
        WHERE has_table_privilege('workcontinuity_app', 'public.reconciliation_targets', p.privilege);
        """;

    private static string Insert(TenantId tenant, string objectId) =>
        $"""
        INSERT INTO reconciliation_targets (tenant_id, object_system, object_connection, object_type, object_id, field, data_type,
            source_scope, state, generation, request_kind, requested_at_utc, attempts, created_at_utc)
        VALUES ('{tenant.Value:D}', 'monday', '{Guid.Empty:D}', 'item', '{objectId}', 'status', 'TaskStatus', '9001', 'Pending', 1, 'Sweep', now(), 0, now());
        """;

    /// <summary>A database created for one test on the local server, named exactly, dropped at the end.</summary>
    private sealed class FreshDatabase : IAsyncDisposable
    {
        private readonly DatabaseFixture _fixture;
        private readonly string _name;
        private readonly string _administrative;
        private readonly string _runtime;
        private readonly string _runtimeRole;
        private readonly string _runtimePassword;

        private FreshDatabase(DatabaseFixture fixture, string name)
        {
            _fixture = fixture;
            _name = name;
            _administrative = new NpgsqlConnectionStringBuilder(fixture.AdministrativeConnectionString) { Database = name, Pooling = false }.ConnectionString;
            var runtime = new NpgsqlConnectionStringBuilder(fixture.ApplicationConnectionString) { Database = name, Pooling = false };
            _runtime = runtime.ConnectionString;
            _runtimeRole = runtime.Username!;
            _runtimePassword = runtime.Password!;
        }

        internal static async Task<FreshDatabase> CreateAsync(DatabaseFixture fixture, string name, CancellationToken cancellationToken)
        {
            await OnServerAsync(fixture, $"DROP DATABASE IF EXISTS {name} WITH (FORCE);", cancellationToken);
            await OnServerAsync(fixture, $"CREATE DATABASE {name};", cancellationToken);
            return new FreshDatabase(fixture, name);
        }

        internal async Task BootstrapAsync(CancellationToken cancellationToken)
        {
            var result = await RuntimeRoleBootstrap.ApplyAsync(_administrative, _runtimeRole, _runtimePassword, cancellationToken);
            Assert.True(result.LoginVerified && result.RowLevelSecurityEnforced);
        }

        internal async Task MigrateAsync(CancellationToken cancellationToken)
        {
            var status = await DatabaseMigrator.ForConnection(_administrative, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(cancellationToken);
            Assert.Contains(status.Applied, migration => migration.EndsWith("_ReconciliationTargets", StringComparison.Ordinal));
        }

        internal async Task<string?> ScalarAsync(string sql, CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(_administrative);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            return (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
        }

        internal async Task AdministrativeAsync(string sql, CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(_administrative);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        internal async Task<TenantId> SeedTenantAsync(CancellationToken cancellationToken)
        {
            var tenant = TenantId.New();
            await AdministrativeAsync($"INSERT INTO tenants (tenant_id, status, created_at_utc) VALUES ('{tenant.Value:D}', 'Active', now());", cancellationToken);
            return tenant;
        }

        /// <summary>Runs a statement as the runtime role in the tenant's scope: a query's count, or the rows a change affected.</summary>
        internal async Task<long> RuntimeAsync(TenantId tenant, string sql, CancellationToken cancellationToken, bool commit = false)
        {
            await using var connection = new NpgsqlConnection(_runtime);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("tenant", tenant.Value.ToString("D"));
                await scope.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            long result = sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                ? (long)(await command.ExecuteScalarAsync(cancellationToken))!
                : await command.ExecuteNonQueryAsync(cancellationToken);
            if (commit)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        }

        public async ValueTask DisposeAsync() => await OnServerAsync(_fixture, $"DROP DATABASE IF EXISTS {_name} WITH (FORCE);", CancellationToken.None);

        private static async Task OnServerAsync(DatabaseFixture fixture, string sql, CancellationToken cancellationToken)
        {
            await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
