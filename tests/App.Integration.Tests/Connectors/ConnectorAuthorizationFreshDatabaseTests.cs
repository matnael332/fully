using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Connectors;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Connectors;

/// <summary>
/// Gate 3B review item R3B-2 on databases created for the test, bootstrapped and then migrated from zero in the order the
/// rollout uses. The runtime role holds exactly read, insert and update on the two 3B tables; it reads, inserts and
/// updates through the store; DELETE and TRUNCATE are refused; another tenant can neither see nor change anything; and
/// row-level security stays enabled and forced. A later rerun of the unchanged global bootstrap re-grants DELETE, which
/// is why the Azure rollout must re-assert the privileges after one, and the recorded re-assertion restores them.
/// </summary>
/// <remarks>
/// The shared test database cannot give this evidence: the suite reruns the global bootstrap there on every run. Each
/// test here creates its own database by an exact name, drops it first in case a stopped run left it behind, and drops
/// it at the end.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ConnectorAuthorizationFreshDatabaseTests(DatabaseFixture fixture)
{
    private const string ExactlyReadInsertUpdate =
        "connector_authorization_transactions:INSERT,SELECT,UPDATE connector_authorizations:INSERT,SELECT,UPDATE";

    /// <summary>The re-assertion the Gate 3 scope records for the Azure rollout (R3B-2).</summary>
    private const string ReassertPrivilegesSql = """
        REVOKE ALL PRIVILEGES ON connector_authorizations, connector_authorization_transactions FROM workcontinuity_app;
        GRANT SELECT, INSERT, UPDATE ON connector_authorizations, connector_authorization_transactions TO workcontinuity_app;
        """;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task After_bootstrap_then_migration_the_runtime_role_holds_exactly_read_insert_and_update_under_forced_row_level_security()
    {
        await using var fresh = await FreshDatabase.CreateAsync(fixture, "wce_it_gate3b_privileges", Token);
        await fresh.BootstrapAsync(Token);
        await fresh.MigrateAsync(Token);

        // The bootstrap's default privileges gave the new tables SELECT, INSERT, UPDATE and DELETE; the migration left
        // exactly what the store needs.
        Assert.Equal(ExactlyReadInsertUpdate, await fresh.PrivilegesAsync(Token));
        Assert.Equal(
            "connector_authorization_transactions:true:true connector_authorizations:true:true",
            await fresh.ScalarsAsync(
                "SELECT relname || ':' || relrowsecurity || ':' || relforcerowsecurity FROM pg_class WHERE relname IN ('connector_authorizations', 'connector_authorization_transactions') ORDER BY relname;",
                Token));
        Assert.Equal(
            "connector_authorization_transactions.tenant_isolation_connector_authorization_transactions:PERMISSIVE:ALL connector_authorizations.tenant_isolation_connector_authorizations:PERMISSIVE:ALL",
            await fresh.ScalarsAsync(
                "SELECT tablename || '.' || policyname || ':' || permissive || ':' || cmd FROM pg_policies WHERE tablename IN ('connector_authorizations', 'connector_authorization_transactions') ORDER BY (tablename || '.' || policyname) COLLATE \"C\";",
                Token));

        // Pre-3G maintenance: a verifier's deletion is recordable only once its transaction is consumed or closed.
        Assert.Equal(
            "CHECK (((verifier_deleted_at_utc IS NULL) OR ((consumed_at_utc IS NOT NULL) AND (verifier_deleted_at_utc >= consumed_at_utc))))",
            await fresh.ScalarsAsync(
                "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_connector_authorization_transactions_verifier_deleted';",
                Token));
    }

    [Fact]
    public async Task There_the_store_reads_inserts_and_updates_while_delete_truncate_and_every_cross_tenant_access_are_refused()
    {
        await using var fresh = await FreshDatabase.CreateAsync(fixture, "wce_it_gate3b_behaviour", Token);
        await fresh.BootstrapAsync(Token);
        await fresh.MigrateAsync(Token);
        var (tenantA, userA) = await fresh.SeedTenantAndUserAsync(Token);
        var (tenantB, _) = await fresh.SeedTenantAndUserAsync(Token);
        await using var a = fresh.Store(tenantA);
        await using var b = fresh.Store(tenantB);

        // Insert and update through the intended path: open and consume a transaction, activate, renew, read.
        var transaction = Transaction(userA, "state-a-1");
        Assert.True((await a.Store.OpenTransactionAsync(transaction, TimeSpan.FromMinutes(10), Token)).Value);
        Assert.Equal(
            ConnectorTransactionOutcome.Consumed,
            (await a.Store.ConsumeTransactionAsync(transaction.TransactionId, userA, Hash("state-a-1"), Token)).Value.Outcome);
        var grant = new ConnectorAuthorizationGrant(
            Guid.CreateVersion7(), "monday", userA, "58201774", "9001", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), "boards:read me:read");
        Assert.True((await a.Store.ActivateAsync(grant, Token)).Value);
        var claim = Guid.CreateVersion7();
        Assert.True((await a.Store.ClaimRefreshAsync(grant.ConnectionId, 1, claim, Token)).Value);
        Assert.True((await a.Store.CompleteRefreshAsync(grant.ConnectionId, 1, claim, DateTimeOffset.UtcNow.AddHours(2), "boards:read me:read", Token)).Value);
        var renewed = (await a.Store.ReadAsync(grant.ConnectionId, Token)).Value!;
        Assert.Equal((2L, claim, tenantA), (renewed.Generation, renewed.MaterialVersion, renewed.Tenant));

        // Select: the tenant sees its own rows, as the runtime role.
        Assert.Equal(1L, await fresh.RuntimeCountAsync(tenantA, "SELECT count(*) FROM connector_authorizations;", Token));
        Assert.Equal(1L, await fresh.RuntimeCountAsync(tenantA, "SELECT count(*) FROM connector_authorization_transactions;", Token));

        // Delete and truncate are refused outright by privilege, whatever the tenant scope.
        foreach (var sql in new[]
                 {
                     "DELETE FROM connector_authorizations;",
                     "DELETE FROM connector_authorization_transactions;",
                     "TRUNCATE connector_authorizations;",
                     "TRUNCATE connector_authorization_transactions;",
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => fresh.RuntimeCountAsync(tenantA, sql, Token));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }

        // Another tenant sees nothing, changes nothing and cannot write rows labelled with the first tenant.
        var second = Transaction(userA, "state-a-2");
        Assert.True((await a.Store.OpenTransactionAsync(second, TimeSpan.FromMinutes(10), Token)).Value);
        Assert.Null((await b.Store.ReadAsync(grant.ConnectionId, Token)).Value);
        Assert.Equal(
            ConnectorTransactionOutcome.NotFound,
            (await b.Store.ConsumeTransactionAsync(second.TransactionId, userA, Hash("state-a-2"), Token)).Value.Outcome);
        Assert.False((await b.Store.ClaimRefreshAsync(grant.ConnectionId, 2, Guid.CreateVersion7(), Token)).Value);
        Assert.Equal(ConnectorDisconnectOutcome.NotFound, (await b.Store.DisconnectAsync(grant.ConnectionId, userA, Token)).Value.Outcome);
        Assert.False((await b.Store.ActivateAsync(
            new ConnectorAuthorizationGrant(Guid.CreateVersion7(), "monday", userA, "58201774", "9001", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), "boards:read me:read"),
            Token)).Value);
        Assert.Equal(0L, await fresh.RuntimeCountAsync(tenantB, "SELECT count(*) FROM connector_authorizations;", Token));
        Assert.Equal(0L, await fresh.RuntimeCountAsync(tenantB, "SELECT count(*) FROM connector_authorization_transactions;", Token));
        Assert.Equal(0L, await fresh.RuntimeCountAsync(tenantB, $"UPDATE connector_authorizations SET generation = generation + 1 WHERE tenant_id = '{tenantA.Value:D}';", Token));
        Assert.Equal(0L, await fresh.RuntimeCountAsync(tenantB, $"UPDATE connector_authorization_transactions SET consumed_at_utc = now() WHERE tenant_id = '{tenantA.Value:D}';", Token));
        var labelled = await Assert.ThrowsAsync<PostgresException>(() => fresh.RuntimeCountAsync(
            tenantB,
            $"""
            INSERT INTO connector_authorization_transactions
                (transaction_id, tenant_id, canonical_user_id, connection_id, source_system, purpose, state_hash, redirect_hash,
                 requested_scopes, created_at_utc, expires_at_utc)
            VALUES ('{Guid.CreateVersion7():D}', '{tenantA.Value:D}', '{userA.Value:D}', '{Guid.CreateVersion7():D}', 'monday', 'Connect',
                    decode(repeat('00', 32), 'hex'), decode(repeat('00', 32), 'hex'), 'me:read', now(), now() + interval '5 minutes');
            """,
            Token));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, labelled.SqlState);

        // Disconnect updates both tables through the store; the first tenant's rows are otherwise untouched.
        Assert.Equal(ConnectorDisconnectOutcome.Disconnected, (await a.Store.DisconnectAsync(grant.ConnectionId, userA, Token)).Value.Outcome);
        var revoked = (await a.Store.ReadAsync(grant.ConnectionId, Token)).Value!;
        Assert.Equal((ConnectorAuthorizationState.Revoked, false, 2L), (revoked.State, revoked.ConnectionEnabled, revoked.Generation));
        Assert.Equal(ExactlyReadInsertUpdate, await fresh.PrivilegesAsync(Token));
    }

    [Fact]
    public async Task A_later_global_bootstrap_rerun_re_grants_delete_until_the_recorded_reassertion_restores_the_3b_privileges()
    {
        await using var fresh = await FreshDatabase.CreateAsync(fixture, "wce_it_gate3b_rerun", Token);
        await fresh.BootstrapAsync(Token);
        await fresh.MigrateAsync(Token);
        Assert.Equal(ExactlyReadInsertUpdate, await fresh.PrivilegesAsync(Token));

        // The hazard R3B-2 records for the Azure rollout: the unchanged bootstrap, rerun, silently re-grants DELETE.
        await fresh.BootstrapAsync(Token);
        Assert.Equal(
            "connector_authorization_transactions:DELETE,INSERT,SELECT,UPDATE connector_authorizations:DELETE,INSERT,SELECT,UPDATE",
            await fresh.PrivilegesAsync(Token));

        // The recorded re-assertion restores exactly what the store needs, and DELETE is refused again.
        await fresh.AdministrativeAsync(ReassertPrivilegesSql, Token);
        Assert.Equal(ExactlyReadInsertUpdate, await fresh.PrivilegesAsync(Token));
        var (tenant, _) = await fresh.SeedTenantAndUserAsync(Token);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => fresh.RuntimeCountAsync(tenant, "DELETE FROM connector_authorizations;", Token));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    private static ConnectorAuthorizationTransaction Transaction(UserId user, string stateSecret) =>
        new(
            Guid.CreateVersion7(),
            user,
            Guid.CreateVersion7(),
            "monday",
            ConnectorAuthorizationPurpose.Connect,
            Hash(stateSecret),
            Hash("https://app.example.test/connectors/monday/callback"),
            "boards:read me:read");

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>A database created for one test on the local server, named exactly, dropped at the end.</summary>
    private sealed class FreshDatabase : IAsyncDisposable
    {
        private readonly DatabaseFixture _fixture;
        private readonly string _name;
        private readonly string _runtimeRole;
        private readonly string _runtimePassword;

        private FreshDatabase(DatabaseFixture fixture, string name)
        {
            _fixture = fixture;
            _name = name;
            Administrative = new NpgsqlConnectionStringBuilder(fixture.AdministrativeConnectionString) { Database = name, Pooling = false }.ConnectionString;
            var runtime = new NpgsqlConnectionStringBuilder(fixture.ApplicationConnectionString) { Database = name, Pooling = false };
            Runtime = runtime.ConnectionString;
            _runtimeRole = runtime.Username!;
            _runtimePassword = runtime.Password!;
        }

        private string Administrative { get; }

        private string Runtime { get; }

        internal static async Task<FreshDatabase> CreateAsync(DatabaseFixture fixture, string name, CancellationToken cancellationToken)
        {
            await OnServerAsync(fixture, $"DROP DATABASE IF EXISTS {name} WITH (FORCE);", cancellationToken);
            await OnServerAsync(fixture, $"CREATE DATABASE {name};", cancellationToken);
            return new FreshDatabase(fixture, name);
        }

        internal async Task BootstrapAsync(CancellationToken cancellationToken)
        {
            var result = await RuntimeRoleBootstrap.ApplyAsync(Administrative, _runtimeRole, _runtimePassword, cancellationToken);
            Assert.True(result.LoginVerified && result.RowLevelSecurityEnforced);
        }

        internal async Task MigrateAsync(CancellationToken cancellationToken)
        {
            var status = await DatabaseMigrator.ForConnection(Administrative, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(cancellationToken);
            Assert.Contains(status.Applied, migration => migration.EndsWith("_ConnectorAuthorizations", StringComparison.Ordinal));
        }

        internal async Task<string> PrivilegesAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(Administrative);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                SELECT t.table_name || ':' || string_agg(p.privilege, ',' ORDER BY p.privilege)
                FROM (VALUES ('connector_authorizations'), ('connector_authorization_transactions')) AS t(table_name)
                CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER')) AS p(privilege)
                WHERE has_table_privilege(@role, 'public.' || t.table_name, p.privilege)
                GROUP BY t.table_name
                ORDER BY t.table_name COLLATE "C";
                """,
                connection);
            command.Parameters.AddWithValue("role", _runtimeRole);
            return await JoinAsync(command, cancellationToken);
        }

        internal async Task<string> ScalarsAsync(string sql, CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(Administrative);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            return await JoinAsync(command, cancellationToken);
        }

        internal async Task AdministrativeAsync(string sql, CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(Administrative);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        internal async Task<(TenantId Tenant, UserId User)> SeedTenantAndUserAsync(CancellationToken cancellationToken)
        {
            var tenant = TenantId.New();
            var user = UserId.New();
            await using var connection = new NpgsqlConnection(Administrative);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO tenants (tenant_id, status, created_at_utc) VALUES (@tenant, 'Active', now());
                INSERT INTO users (user_id, tenant_id, identity_provider, provider_subject, is_active, created_at_utc)
                VALUES (@user, @tenant, 'entra-id', @subject, true, now());
                """,
                connection);
            command.Parameters.AddWithValue("tenant", tenant.Value);
            command.Parameters.AddWithValue("user", user.Value);
            command.Parameters.AddWithValue("subject", "subject-" + user.Value.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return (tenant, user);
        }

        /// <summary>Runs a statement as the runtime role in the tenant's scope; a query's count, or the rows a change affected.</summary>
        internal async Task<long> RuntimeCountAsync(TenantId tenant, string sql, CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(Runtime);
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
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        internal ScopedStore Store(TenantId tenant)
        {
            var context = new WorkContinuityDbContext(new DbContextOptionsBuilder<WorkContinuityDbContext>().UseNpgsql(Runtime).Options);
            var accessor = new ConnectorAuthorizationData.FixedTenantContextAccessor(tenant);
            return new ScopedStore(
                new ConnectorAuthorizationStore(new TenantScopedDatabase(context, accessor, NullLogger<TenantScopedDatabase>.Instance), accessor),
                context);
        }

        public async ValueTask DisposeAsync() => await OnServerAsync(_fixture, $"DROP DATABASE IF EXISTS {_name} WITH (FORCE);", CancellationToken.None);

        private static async Task OnServerAsync(DatabaseFixture fixture, string sql, CancellationToken cancellationToken)
        {
            await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<string> JoinAsync(NpgsqlCommand command, CancellationToken cancellationToken)
        {
            var values = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                values.Add(reader.GetString(0));
            }

            return string.Join(' ', values);
        }
    }

    /// <summary>A store on the fresh database and the context it owns.</summary>
    private sealed class ScopedStore(ConnectorAuthorizationStore store, WorkContinuityDbContext context) : IAsyncDisposable
    {
        internal ConnectorAuthorizationStore Store { get; } = store;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
