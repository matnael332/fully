using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Evidence;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.InitialSync;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G Stage B3, area C (owner decision B-14; review decision R3B-2): the read-only procedure that will establish
/// fresh row-level security and runtime-privilege evidence on the persistent standalone harness database,
/// <c>eng/harness-standalone-evidence.sql</c>. Stage B3 does not have that database, so nothing here is evidence of it.
/// What these tests prove is the procedure itself: it writes nothing and shows no payload; its R3B-2 expectations are
/// exactly the recorded ones; and, run on the shared suite database, which is not and can never be live evidence, it
/// reports the facts as they are, PASS where that database meets a rule and FAIL where it does not (its name, and the
/// privileges the suite's bootstrap rerun re-grants there), and FAIL for a role that steps around row-level security.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessStandaloneEvidenceTests(DatabaseFixture fixture)
{
    private const string ProcedureFile = "eng/harness-standalone-evidence.sql";

    private static readonly string[] CatalogChecks =
    [
        "transaction.read_only",
        "identity.expected_database",
        "identity.standalone_harness_database_name",
        "identity.session_user_is_current_user",
        "identity.current_user_is_runtime_role",
        "role.login_nosuperuser_nobypassrls_nocreaterole_nocreatedb_noreplication_noinherit",
        "role.owns_no_table_view_or_sequence",
        "role.owns_neither_database_nor_schema",
        "role.member_of_no_role",
        "rls.every_tenant_table_enabled_and_forced",
        "rls.only_the_migration_history_has_no_tenant",
        "rls.every_tenant_table_has_its_isolation_policy",
        "rls.no_other_permissive_policy_applies_to_the_runtime_role",
        "r3b2.oauth_and_reconciliation_tables_exactly_insert_select_update",
        "r3b2.initial_sync_tables_exactly_insert_select",
        "r3b2.initial_sync_update_columns_exact",
        "r3b2.current_work_views_select_only",
        "runtime.no_administrative_path_beyond_the_approved_functions",
    ];

    private static readonly string[] IsolationChecks =
    [
        "isolation.without_tenant_no_row_is_visible",
        "isolation.fresh_tenant_sees_no_other_tenant_row",
        "isolation.fresh_tenant_present_once_with_one_active_user",
        "isolation.other_tenant_sees_no_fresh_tenant_row",
    ];

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_procedure_is_one_read_only_transaction_that_writes_nothing_and_selects_no_payload_or_identifier()
    {
        var text = Read();
        var code = Code();

        Assert.StartsWith("BEGIN TRANSACTION READ ONLY;\n", code, StringComparison.Ordinal);
        Assert.EndsWith("\nROLLBACK;", code, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(code, @"\bBEGIN\b"));
        Assert.Equal(1, Regex.Count(code, @"\bROLLBACK\b"));
        Assert.DoesNotContain("COMMIT", code, StringComparison.Ordinal);

        // No write, no change of a role, a privilege, a policy or a setting outside the transaction, and no psql command.
        Assert.DoesNotMatch(
            new Regex(@"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|CREATE|DROP|ALTER|GRANT|REVOKE|COPY|VACUUM|ANALYZE|LOCK|COMMENT|SECURITY|NOTIFY|DO|CALL|PERFORM|EXECUTE|SET)\b"),
            Regex.Replace(code, @"'[^']*'", "''"));
        Assert.DoesNotContain(@"\", code.Replace(@"'\", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // The only setting it changes is the tenant context, and only for its own transaction.
        var settings = Regex.Matches(code, @"set_config\('([\w.]+)', [^;]*?, (true|false)\)").ToList();
        Assert.Equal(3, settings.Count);
        Assert.All(settings, setting => Assert.Equal(("app.tenant_id", "true"), (setting.Groups[1].Value, setting.Groups[2].Value)));
        Assert.Equal(3, Regex.Count(code, "set_config"));

        // Closed values only: no payload, value, hash, provider identifier or row identifier is selected or shown. Column
        // names inside quoted text are the expected update-privilege lists, compared, never selected.
        var selected = Regex.Replace(code, @"'[^']*'", "''");
        foreach (var column in new[] { "payload", "value", "provider_subject", "provider_account", "state_hash", "redirect_hash", "granted_scopes", "material_version", "source_event_id", "correlation_id", "connection_id", "user_id", "resource_id" })
        {
            Assert.DoesNotMatch(new Regex(@"\b" + column + @"\b"), selected);
        }

        foreach (var forbidden in new[] { "PASSWORD", "password", "Host=", "Username=", "sb://", "SharedAccessKey", "monday.com", "vault.azure.net" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        // Each check, once, in order; every status is closed.
        Assert.Equal([.. CatalogChecks, .. IsolationChecks], Regex.Matches(code, @"'((?:transaction|identity|role|rls|r3b2|runtime|isolation)\.[a-z_]+)'").Select(match => match.Groups[1].Value));
        Assert.Equal(5, Regex.Count(code, @"CASE WHEN .*? THEN 'PASS' ELSE 'FAIL' END", RegexOptions.Singleline));
    }

    [Fact]
    public void The_membership_check_is_the_harness_statement_itself_and_the_expectations_are_the_recorded_ones()
    {
        var code = Code();

        // The procedure's membership check runs the harness's own recursive statement, unchanged.
        Assert.Equal(2, Regex.Count(code, Regex.Escape(DatabaseRoleCheck.MembershipStatement)));

        // The runtime role exactly as the owner's Decision 3 of 2026-10-06 records it: the seven attributes; no ownership of
        // a table, view or sequence, of any schema or of the database; and membership in no role at all.
        foreach (var condition in new[]
                 {
                     "(SELECT r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls AND NOT r.rolcreaterole AND NOT r.rolcreatedb",
                     "AND NOT r.rolreplication AND NOT r.rolinherit",
                     "WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f') AND c.relpersistence <> 't'",
                     "AND pg_has_role(current_user, c.relowner, 'MEMBER')),",
                     "AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace AS n WHERE pg_has_role(current_user, n.nspowner, 'MEMBER')),",
                     "(SELECT m.current_role_rows = 1 AND m.granted_roles = 0 AND m.privileged_granted_role IS FALSE",
                 })
        {
            Assert.Equal(1, Regex.Count(code, Regex.Escape(condition)));
        }

        // R3B-2 for the initial-sync tables: exactly the re-assertion the Gate 3 scope records ("3F final owner review").
        var reassertion = Regex.Replace(InitialSyncDatabaseSecurityTests.ReassertPrivilegesSql, @"\s+", " ");
        var updateColumns = Regex.Matches(reassertion, @"GRANT UPDATE \(([^)]*)\) ON (\w+) TO workcontinuity_app;")
            .Select(match => match.Groups[2].Value + ":" + string.Join(",", match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal);
        var expected = Regex.Match(code, @"= '(connection_sync_scopes:[^']*)'\s*\|\| '( connection_syncs:[^']*)'\s*\|\| '( connector_source_scopes:[^']*)'");
        Assert.True(expected.Success);
        Assert.Equal(string.Join(" ", updateColumns), expected.Groups[1].Value + expected.Groups[2].Value + expected.Groups[3].Value);
        Assert.Equal(3, Regex.Count(reassertion, @"GRANT SELECT, INSERT ON (connector_source_scopes|connection_syncs|connection_sync_scopes) TO workcontinuity_app;"));
        Assert.Contains("= 'connection_sync_scopes:INSERT,SELECT connection_syncs:INSERT,SELECT connector_source_scopes:INSERT,SELECT'", code, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON active_snapshot_objects, active_snapshot_fields TO workcontinuity_app;", reassertion, StringComparison.Ordinal);
        Assert.Contains("= 'active_snapshot_fields:SELECT active_snapshot_objects:SELECT'", code, StringComparison.Ordinal);

        // R3B-2 for the OAuth tables (3B) and the reconciliation targets (3E): read, insert and update, never delete.
        var scope = File.ReadAllText(Path.Combine(Root().FullName, "docs", "gates", "gate-3-scope.md"));
        Assert.Contains("GRANT SELECT, INSERT, UPDATE ON connector_authorizations, connector_authorization_transactions TO workcontinuity_app;", scope, StringComparison.Ordinal);
        var reconciliation = File.ReadAllText(Path.Combine(Root().FullName, "src", "App.Infrastructure", "Persistence", "Migrations", "20261001073251_ReconciliationTargets.cs"));
        Assert.Contains("GRANT SELECT, INSERT, UPDATE ON reconciliation_targets TO workcontinuity_app;", reconciliation, StringComparison.Ordinal);
        Assert.Contains(
            "= 'connector_authorization_transactions:INSERT,SELECT,UPDATE connector_authorizations:INSERT,SELECT,UPDATE reconciliation_targets:INSERT,SELECT,UPDATE'",
            code,
            StringComparison.Ordinal);

        // Every table of the product model that carries a tenant is one of the tables the procedure counts.
        using var context = new WorkContinuityDbContext(new DbContextOptionsBuilder<WorkContinuityDbContext>().UseNpgsql("Host=unused").Options);
        var tenantTables = context.Model.GetEntityTypes()
            .Where(type => type.GetTableName() is not null && type.GetViewName() is null && type.GetProperties().Any(property => property.GetColumnName() == "tenant_id"))
            .Select(type => type.GetTableName())
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert.Equal(16, tenantTables);
        Assert.Equal(4, Regex.Count(code, @"count\(\*\) = 16\b"));
    }

    [Fact]
    public async Task On_the_shared_suite_database_it_reports_each_fact_as_it_is_and_never_a_pass_overall()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var database = new NpgsqlConnectionStringBuilder(fixture.ApplicationConnectionString).Database!;

        var results = await RunAsync(fixture.ApplicationConnectionString, database, tenant.Value.ToString("D"));
        Assert.Equal([.. CatalogChecks, .. IsolationChecks], results.Keys);

        // The shared suite database is never live evidence: its name is not a standalone harness database's, so the
        // procedure as a whole can never pass there.
        Assert.Equal("FAIL", results["identity.standalone_harness_database_name"]);

        // Everything it does meet is reported as met.
        string[] met =
        [
            "transaction.read_only", "identity.expected_database", "identity.session_user_is_current_user", "identity.current_user_is_runtime_role",
            "role.login_nosuperuser_nobypassrls_nocreaterole_nocreatedb_noreplication_noinherit", "role.owns_no_table_view_or_sequence",
            "role.owns_neither_database_nor_schema", "role.member_of_no_role", "rls.every_tenant_table_enabled_and_forced",
            "rls.only_the_migration_history_has_no_tenant",
            "rls.every_tenant_table_has_its_isolation_policy", "rls.no_other_permissive_policy_applies_to_the_runtime_role",
            "runtime.no_administrative_path_beyond_the_approved_functions", .. IsolationChecks,
        ];
        foreach (var check in met)
        {
            Assert.True(results[check] == "PASS", check);
        }

        // The R3B-2 checks say exactly what the catalog says: the suite reruns the global bootstrap on its database, which
        // re-grants DELETE, and the procedure must see that rather than assume the recorded grants.
        await using var connection = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Token);
        foreach (var (check, tables, expected) in new[]
                 {
                     ("r3b2.oauth_and_reconciliation_tables_exactly_insert_select_update", new[] { "connector_authorizations", "connector_authorization_transactions", "reconciliation_targets" }, "INSERT,SELECT,UPDATE"),
                     ("r3b2.initial_sync_tables_exactly_insert_select", new[] { "connector_source_scopes", "connection_syncs", "connection_sync_scopes" }, "INSERT,SELECT"),
                     ("r3b2.current_work_views_select_only", new[] { "active_snapshot_objects", "active_snapshot_fields" }, "SELECT"),
                 })
        {
            var actual = new List<string>();
            foreach (var table in tables)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT coalesce(string_agg(p, ',' ORDER BY p), '') FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS p WHERE has_table_privilege(current_user, 'public.' || @table, p);",
                    connection);
                command.Parameters.AddWithValue("table", table);
                actual.Add((string)(await command.ExecuteScalarAsync(Token))!);
            }

            Assert.Equal(actual.All(privileges => privileges == expected) ? "PASS" : "FAIL", results[check]);
        }
    }

    [Fact]
    public async Task A_session_that_steps_around_row_level_security_or_a_wrong_target_fails_and_a_missing_tenant_stops_it()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        var database = new NpgsqlConnectionStringBuilder(fixture.ApplicationConnectionString).Database!;

        // The schema owner, a superuser that owns the database, its schemas and its tables: the role checks fail, and so
        // does isolation, because it sees every tenant's rows.
        var asOwner = await RunAsync(fixture.AdministrativeConnectionString, database, tenant.Value.ToString("D"));
        foreach (var check in new[]
                 {
                     "identity.current_user_is_runtime_role", "role.login_nosuperuser_nobypassrls_nocreaterole_nocreatedb_noreplication_noinherit",
                     "role.owns_no_table_view_or_sequence", "role.owns_neither_database_nor_schema",
                     "isolation.without_tenant_no_row_is_visible", "isolation.fresh_tenant_sees_no_other_tenant_row", "isolation.fresh_tenant_present_once_with_one_active_user",
                 })
        {
            Assert.True(asOwner[check] == "FAIL", check);
        }

        // A database other than the one named, or none named.
        Assert.Equal("FAIL", (await RunAsync(fixture.ApplicationConnectionString, "wce_harness_live_20261006", tenant.Value.ToString("D")))["identity.expected_database"]);
        Assert.Equal("FAIL", (await RunAsync(fixture.ApplicationConnectionString, null, tenant.Value.ToString("D")))["identity.expected_database"]);

        // A tenant with no rows: present-once fails; one with a second user fails it too.
        Assert.Equal("FAIL", (await RunAsync(fixture.ApplicationConnectionString, database, TenantId.New().Value.ToString("D")))["isolation.fresh_tenant_present_once_with_one_active_user"]);
        await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        Assert.Equal("FAIL", (await RunAsync(fixture.ApplicationConnectionString, database, tenant.Value.ToString("D")))["isolation.fresh_tenant_present_once_with_one_active_user"]);

        // No tenant given: the procedure stops with an error rather than answer.
        await Assert.ThrowsAsync<PostgresException>(() => RunAsync(fixture.ApplicationConnectionString, database, null));
    }

    // Runs the whole procedure file, as given, on one connection; the checks' statuses by name, in order.
    private async Task<Dictionary<string, string>> RunAsync(string connectionString, string? expectedDatabase, string? tenant)
    {
        var options = string.Empty;
        if (expectedDatabase is not null)
        {
            options += "-c wce_evidence.expected_database=" + expectedDatabase;
        }

        if (tenant is not null)
        {
            options += " -c wce_evidence.tenant_id=" + tenant;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Options = options.Trim(), Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(Read(), connection) { CommandTimeout = 120 };
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(Token);
        do
        {
            while (await reader.ReadAsync(Token))
            {
                if (reader.FieldCount == 3 && reader.GetName(0) == "check_name")
                {
                    results.Add(reader.GetString(0), reader.GetString(1));
                }
            }
        }
        while (await reader.NextResultAsync(Token));

        return results;
    }

    private static string Read() =>
        File.ReadAllText(Path.Combine(Root().FullName, ProcedureFile)).Replace("\r\n", "\n", StringComparison.Ordinal);

    // The procedure without its comment lines.
    private static string Code() =>
        string.Join("\n", Read().Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal) && line.Trim().Length > 0));

    private static DirectoryInfo Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.EnumerateFiles("WorkContinuityEngine.sln").Any())
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
