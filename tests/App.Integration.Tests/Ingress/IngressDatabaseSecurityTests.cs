using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Ingress;

/// <summary>
/// The one tenant-less database function Gate 2B adds (owner decision 1), pinned against the real
/// database: it is the only <c>SECURITY DEFINER</c> function, its search path is fixed, only the runtime
/// role may execute it, and it returns one active connection's tenant and source and nothing else. The
/// owner's narrow read policy is the only policy beyond the tenant policies, and every other table's
/// row-level security is unchanged.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class IngressDatabaseSecurityTests(DatabaseFixture fixture)
{
    private const string Resolver = "public.resolve_ingress_connection(uuid)";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_resolver_is_the_only_security_definer_function_and_its_definition_is_pinned()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname || '.' || p.proname,
                   pg_get_function_identity_arguments(p.oid),
                   pg_get_function_result(p.oid),
                   p.provolatile::text,
                   p.proisstrict,
                   p.proretset,
                   l.lanname,
                   p.proconfig,
                   pg_get_userbyid(p.proowner),
                   (SELECT pg_get_userbyid(c.relowner) FROM pg_class c WHERE c.oid = 'public.connections'::regclass)
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_language l ON l.oid = p.prolang
            WHERE p.prosecdef
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY 1;
            """,
            connection);

        var definers = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            definers.Add(reader.GetString(0));

            Assert.Equal("public.resolve_ingress_connection", reader.GetString(0));
            Assert.Equal("p_connection_id uuid", reader.GetString(1));
            Assert.Equal("TABLE(tenant_id uuid, source_system character varying)", reader.GetString(2));
            Assert.Equal("s", reader.GetString(3));
            Assert.True(reader.GetBoolean(4), "The resolver must be STRICT.");
            Assert.True(reader.GetBoolean(5), "The resolver returns a set.");
            Assert.Equal("sql", reader.GetString(6));
            Assert.Equal(["search_path=pg_catalog, pg_temp"], reader.GetFieldValue<string[]>(7));

            // Owned by the migrating role that owns the table, which the owner read policy names.
            Assert.Equal(reader.GetString(9), reader.GetString(8));
        }

        Assert.Equal(["public.resolve_ingress_connection"], definers);
    }

    [Fact]
    public async Task Only_the_owner_and_the_runtime_role_may_execute_the_resolver()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);

        var owner = (string)(await ScalarAsync(
            connection, transaction: null, $"SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE oid = '{Resolver}'::regprocedure;"))!;

        await using var command = new NpgsqlCommand(
            $"""
            SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END,
                   a.privilege_type
            FROM pg_proc p
            CROSS JOIN LATERAL aclexplode(p.proacl) a
            WHERE p.oid = '{Resolver}'::regprocedure;
            """,
            connection);

        var grants = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                grants.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
            }
        }

        Assert.Equal(
            new[] { $"{owner}:EXECUTE", $"{PersistenceOptions.RuntimeRoleName}:EXECUTE" }.Order(StringComparer.Ordinal),
            grants.Order(StringComparer.Ordinal));

        // A role with no grant of its own gets nothing through PUBLIC.
        var probe = "wce_ingress_probe_" + Guid.NewGuid().ToString("N")[..12];
        await using var transaction = await connection.BeginTransactionAsync(Token);
        try
        {
            await ExecuteAsync(connection, transaction, $"CREATE ROLE {probe} NOLOGIN;");
            var allowed = (bool)(await ScalarAsync(
                connection, transaction, $"SELECT has_function_privilege('{probe}', '{Resolver}', 'EXECUTE');"))!;
            Assert.False(allowed);
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }
    }

    [Fact]
    public async Task The_resolver_returns_one_active_connections_tenant_and_source_and_nothing_else()
    {
        var tenant = TenantId.New();
        await IngressData.SeedTenantAsync(fixture, tenant, "Active", Token);
        var active = await IngressData.RegisterConnectionAsync(
            fixture, tenant, ConnectionStatus.Active, SyntheticSource.System, Token);
        var disabled = await IngressData.RegisterConnectionAsync(
            fixture, tenant, ConnectionStatus.Disabled, SyntheticSource.System, Token);

        // As the runtime role with no tenant set: exactly the state ingress is in before resolution.
        await using var connection = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Token);

        var resolved = Assert.Single(await ResolveAsync(connection, active));
        Assert.Equal(["tenant_id", "source_system"], resolved.Columns);
        Assert.Equal(tenant.Value, resolved.TenantId);
        Assert.Equal(SyntheticSource.System, resolved.SourceSystem);

        Assert.Empty(await ResolveAsync(connection, disabled));
        Assert.Empty(await ResolveAsync(connection, Guid.CreateVersion7()));
        Assert.Empty(await ResolveAsync(connection, connectionId: null));

        // The function is the only way in: the runtime role still reads no connection row directly.
        Assert.Equal(0L, await ScalarAsync(connection, transaction: null, "SELECT count(*) FROM connections;"));
    }

    [Fact]
    public async Task Connections_are_readable_and_writable_only_within_their_own_tenant()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        await IngressData.SeedTenantAsync(fixture, tenantA, "Active", Token);
        await IngressData.SeedTenantAsync(fixture, tenantB, "Active", Token);
        var connectionA = await IngressData.RegisterConnectionAsync(
            fixture, tenantA, ConnectionStatus.Active, SyntheticSource.System, Token);

        Assert.Equal(1, await VisibleConnectionCountAsync(tenantA, connectionA));
        Assert.Equal(0, await VisibleConnectionCountAsync(tenantB, connectionA));

        // Tenant B cannot register a connection that belongs to tenant A.
        var (database, context) = fixture.CreateTenantScoped(tenantB);
        await using (context)
        {
            var written = await database.ExecuteAsync(
                (dbContext, _) =>
                {
                    dbContext.Connections.Add(ConnectionRecord.Register(
                        Guid.CreateVersion7(), tenantA, SyntheticSource.System, ConnectionStatus.Active, IngressHost.Now));
                    return Task.CompletedTask;
                },
                Token);

            Assert.True(written.IsFailure);
            Assert.Equal(PersistenceErrors.TenantIsolationViolation, written.Error);
        }
    }

    [Fact]
    public async Task The_only_policies_are_the_tenant_policies_and_the_resolver_owners_read_policy()
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        var owner = (string)(await ScalarAsync(
            connection, transaction: null, $"SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE oid = '{Resolver}'::regprocedure;"))!;

        await using var command = new NpgsqlCommand(
            """
            SELECT tablename, policyname, permissive, roles::text[], cmd, qual, with_check
            FROM pg_policies
            WHERE schemaname = 'public'
            ORDER BY tablename, policyname;
            """,
            connection);

        var policies = new List<(string Table, string Name, string Permissive, string[] Roles, string Command, string? Using, string? Check)>();
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                policies.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<string[]>(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        Assert.Equal(
            [
                "connections.ingress_resolution_connections",
                "connections.tenant_isolation_connections",
                "outbox_messages.tenant_isolation_outbox_messages",
                "raw_receipts.tenant_isolation_raw_receipts",
                "tenants.tenant_isolation_tenants",
                "users.tenant_isolation_users",
            ],
            policies.Select(policy => $"{policy.Table}.{policy.Name}"));

        // Every tenant policy, old and new, is the same permissive, all-command, all-role predicate.
        var tenantPolicies = policies.Where(policy => policy.Name.StartsWith("tenant_isolation_", StringComparison.Ordinal)).ToList();
        var predicate = tenantPolicies[0].Using;
        Assert.NotNull(predicate);
        Assert.Contains("app.tenant_id", predicate, StringComparison.Ordinal);
        Assert.All(tenantPolicies, policy =>
        {
            Assert.Equal("PERMISSIVE", policy.Permissive);
            Assert.Equal(["public"], policy.Roles);
            Assert.Equal("ALL", policy.Command);
            Assert.Equal(predicate, policy.Using);
            Assert.Equal(predicate, policy.Check);
        });

        // The owner's policy only reads, only active rows, and only for the role the resolver runs as.
        var ownerPolicy = Assert.Single(policies, policy => policy.Name == "ingress_resolution_connections");
        Assert.Equal("PERMISSIVE", ownerPolicy.Permissive);
        Assert.Equal([owner], ownerPolicy.Roles);
        Assert.Equal("SELECT", ownerPolicy.Command);
        Assert.Null(ownerPolicy.Check);
        Assert.NotNull(ownerPolicy.Using);
        Assert.Contains("status", ownerPolicy.Using, StringComparison.Ordinal);
        Assert.Contains("'Active'", ownerPolicy.Using, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant", ownerPolicy.Using, StringComparison.Ordinal);
        Assert.NotEqual(PersistenceOptions.RuntimeRoleName, owner);
    }

    [Fact]
    public async Task A_non_superuser_owner_resolves_connections_only_through_its_own_read_policy()
    {
        // In Azure the migrating role that owns the table and the function is not a superuser, so forced
        // row-level security binds it. This rehearses that inside a transaction that is always rolled
        // back: a non-superuser role takes ownership of both, and the resolver finds the connection only
        // once that owner holds the migration's own read policy.
        var tenant = TenantId.New();
        var connectionId = Guid.CreateVersion7();
        var probe = "wce_ingress_owner_probe_" + Guid.NewGuid().ToString("N")[..12];

        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        var ownerPredicate = (string)(await ScalarAsync(
            connection,
            transaction: null,
            """
            SELECT qual FROM pg_policies
            WHERE schemaname = 'public' AND tablename = 'connections'
              AND policyname = 'ingress_resolution_connections';
            """))!;

        long withoutPolicy;
        long withPolicy;
        await using var transaction = await connection.BeginTransactionAsync(Token);
        try
        {
            await ExecuteAsync(connection, transaction, $"CREATE ROLE {probe} NOLOGIN NOSUPERUSER NOBYPASSRLS;");
            await ExecuteAsync(connection, transaction, $"ALTER TABLE public.connections OWNER TO {probe};");
            await ExecuteAsync(connection, transaction, $"ALTER FUNCTION {Resolver} OWNER TO {probe};");
            await ExecuteAsync(
                connection,
                transaction,
                $"""
                INSERT INTO public.connections (connection_id, tenant_id, source_system, status, created_at_utc)
                VALUES ('{connectionId}', '{tenant.Value}', 'synthetic', 'Active', now());
                """);

            withoutPolicy = await ResolveCountAsRuntimeRoleAsync(connection, transaction, connectionId);

            await ExecuteAsync(
                connection,
                transaction,
                $"CREATE POLICY probe_owner_read ON public.connections AS PERMISSIVE FOR SELECT TO {probe} USING ({ownerPredicate});");

            withPolicy = await ResolveCountAsRuntimeRoleAsync(connection, transaction, connectionId);
        }
        finally
        {
            await transaction.RollbackAsync(Token);
        }

        Assert.Equal(0L, withoutPolicy);
        Assert.Equal(1L, withPolicy);
    }

    [Fact]
    public async Task The_runtime_role_cannot_create_an_object_that_could_shadow_what_the_resolver_uses()
    {
        await using var connection = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Token);

        try
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                connection,
                transaction: null,
                "CREATE FUNCTION public.wce_shadow_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1';"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
        finally
        {
            await IngressData.ExecuteAdministrativeAsync(
                fixture, "DROP FUNCTION IF EXISTS public.wce_shadow_probe();", Token);
        }
    }

    private async Task<long> ResolveCountAsRuntimeRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid connectionId)
    {
        await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {PersistenceOptions.RuntimeRoleName};");
        var count = (long)(await ScalarAsync(
            connection, transaction, $"SELECT count(*) FROM public.resolve_ingress_connection('{connectionId}');"))!;
        await ExecuteAsync(connection, transaction, "RESET ROLE;");
        return count;
    }

    private async Task<List<(string[] Columns, Guid TenantId, string SourceSystem)>> ResolveAsync(
        NpgsqlConnection connection,
        Guid? connectionId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT * FROM public.resolve_ingress_connection(@connection_id);", connection);
        command.Parameters.Add(new NpgsqlParameter("connection_id", NpgsqlDbType.Uuid)
        {
            Value = connectionId is { } id ? id : (object)DBNull.Value,
        });

        var rows = new List<(string[] Columns, Guid TenantId, string SourceSystem)>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        while (await reader.ReadAsync(Token))
        {
            rows.Add((columns, reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
    }

    private async Task<int> VisibleConnectionCountAsync(TenantId tenant, Guid connectionId)
    {
        var (database, context) = fixture.CreateTenantScoped(tenant);
        await using (context)
        {
            var read = await database.ExecuteAsync(
                (dbContext, token) => dbContext.Connections
                    .AsNoTracking()
                    .CountAsync(record => record.ConnectionId == connectionId, token),
                Token);

            Assert.True(read.IsSuccess, read.IsSuccess ? string.Empty : read.Error.Code);
            return read.Value;
        }
    }

    private async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(Token);
    }
}
