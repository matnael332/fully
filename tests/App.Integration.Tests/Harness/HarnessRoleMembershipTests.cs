using System.Globalization;
using System.Text;
using Npgsql;
using WorkContinuity.Harness.Monday.Evidence;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G Stage B3, area A (owner decision B-14): the runtime-role safety check follows role membership through
/// <c>pg_auth_members</c> recursively, and reports whether a superuser, a role with BYPASSRLS or the owner of a table
/// of the schema is reached at any depth. Since the owner's Decision 3 of 2026-10-06, membership itself is
/// disqualifying: only a role that is a member of no role at all is accepted, so a chain of ordinary roles is refused
/// too; and any answer the check cannot read fails closed.
/// </summary>
/// <remarks>
/// The local server has no role that is a member of another, and these tests create, grant and alter no role. They run
/// the harness's own recursive statement, unchanged except for its two catalog projections, over synthetic catalogs
/// written as <c>VALUES</c> lists, on the real PostgreSQL engine and as the runtime role, so the recursion, the cycle
/// handling and the decision are PostgreSQL's own. A pinned check keeps the two projections exactly as the harness has
/// them, and the real catalog is read once as each local role.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessRoleMembershipTests(DatabaseFixture fixture)
{
    /// <summary>The catalog projection of each role and whether it is privileged, exactly as the harness reads it.</summary>
    internal const string RoleProjection =
        "SELECT r.oid, r.rolname, (r.rolsuper OR r.rolbypassrls OR EXISTS (SELECT 1 FROM pg_tables AS t WHERE t.schemaname = 'public' AND t.tableowner = r.rolname)) FROM pg_roles AS r";

    /// <summary>The catalog projection of every membership, exactly as the harness reads it.</summary>
    internal const string MembershipProjection = "SELECT m.member, m.roleid FROM pg_auth_members AS m";

    private const string Self = "self";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_statement_reads_the_catalog_through_exactly_the_two_pinned_projections_and_follows_memberships_by_union()
    {
        var statement = DatabaseRoleCheck.MembershipStatement;
        Assert.Equal(1, Count(statement, RoleProjection));
        Assert.Equal(1, Count(statement, MembershipProjection));
        Assert.StartsWith("SELECT ", statement, StringComparison.Ordinal);

        // The recursion starts at the current role, walks outwards from member to granted role, and drops every role
        // already reached; the decision counts a role with no catalog entry as privileged.
        Assert.Equal(1, Count(statement, "WITH RECURSIVE "));
        Assert.Equal(1, Count(statement, "SELECT m.roleid FROM memberships AS m JOIN current_role_row AS c ON m.member = c.oid UNION SELECT m.roleid FROM memberships AS m JOIN granted AS g ON m.member = g.roleid)"));
        Assert.Equal(0, Count(statement, "UNION ALL"));
        Assert.Equal(1, Count(statement, "SELECT f.oid FROM role_facts AS f WHERE f.rolname = current_user"));
        Assert.Equal(1, Count(statement, "LEFT JOIN role_facts AS f ON f.oid = g.roleid WHERE f.privileged IS NOT FALSE"));
    }

    [Fact]
    public async Task Only_no_membership_is_accepted_and_every_chain_of_ordinary_roles_is_refused()
    {
        // No membership at all: the one answer that passes.
        var none = await RunAsync(Roles(), Edges());
        Assert.Equal(new RoleMembershipFacts(1, 0, false), none);
        Assert.False(RoleMembershipFacts.IsPrivileged([none]));

        // A direct membership in an ordinary role: refused, because membership itself is disqualifying (Decision 3).
        var direct = await RunAsync(Roles(("a", false)), Edges((Self, "a")));
        Assert.Equal(new RoleMembershipFacts(1, 1, false), direct);
        Assert.True(RoleMembershipFacts.IsPrivileged([direct]));

        // Four hops through ordinary roles: refused.
        var chain = await RunAsync(Roles(("a", false), ("b", false), ("c", false), ("d", false)), Edges((Self, "a"), ("a", "b"), ("b", "c"), ("c", "d")));
        Assert.Equal(new RoleMembershipFacts(1, 4, false), chain);
        Assert.True(RoleMembershipFacts.IsPrivileged([chain]));

        // Two paths to the same ordinary role: each role is counted once, and the answer is refused.
        var paths = await RunAsync(Roles(("a", false), ("b", false), ("c", false)), Edges((Self, "a"), (Self, "b"), ("a", "c"), ("b", "c")));
        Assert.Equal(new RoleMembershipFacts(1, 3, false), paths);
        Assert.True(RoleMembershipFacts.IsPrivileged([paths]));

        // A cycle of ordinary roles through the current role: refused.
        var cycle = await RunAsync(Roles(("a", false)), Edges((Self, "a"), ("a", Self)));
        Assert.True(RoleMembershipFacts.IsPrivileged([cycle]));
    }

    [Fact]
    public async Task A_direct_membership_in_a_privileged_role_is_privileged()
    {
        Assert.Equal(new RoleMembershipFacts(1, 1, true), await RunAsync(Roles(("admin", true)), Edges((Self, "admin"))));
        Assert.True(RoleMembershipFacts.IsPrivileged([await RunAsync(Roles(("admin", true)), Edges((Self, "admin")))]));
    }

    [Fact]
    public async Task A_privileged_role_at_the_end_of_a_chain_is_found_at_every_depth()
    {
        for (var depth = 2; depth <= 6; depth++)
        {
            var names = Enumerable.Range(1, depth).Select(index => "hop" + index.ToString(CultureInfo.InvariantCulture)).ToList();
            var roles = names.Select((name, index) => (name, index == depth - 1)).ToArray();
            var edges = new List<(string, string)> { (Self, names[0]) };
            edges.AddRange(names.Zip(names.Skip(1)));

            Assert.Equal(new RoleMembershipFacts(1, depth, true), await RunAsync(Roles(roles), Edges([.. edges])));
        }
    }

    [Fact]
    public async Task One_privileged_path_among_several_is_enough()
    {
        // Two paths from the current role: a safe one and one that ends, two hops away, in a privileged role.
        Assert.Equal(
            new RoleMembershipFacts(1, 4, true),
            await RunAsync(
                Roles(("safe", false), ("via", false), ("other", false), ("owner", true)),
                Edges((Self, "safe"), (Self, "via"), ("via", "other"), ("other", "owner"))));

        // A diamond that rejoins before a privileged role.
        Assert.Equal(
            new RoleMembershipFacts(1, 4, true),
            await RunAsync(
                Roles(("left", false), ("right", false), ("join", false), ("super", true)),
                Edges((Self, "left"), (Self, "right"), ("left", "join"), ("right", "join"), ("join", "super"))));
    }

    [Fact]
    public async Task Privileged_roles_the_current_role_is_not_a_member_of_do_not_count()
    {
        // A privileged role elsewhere in the catalog, with its own members.
        Assert.Equal(
            new RoleMembershipFacts(1, 1, false),
            await RunAsync(Roles(("a", false), ("elsewhere", true), ("member", false)), Edges((Self, "a"), ("member", "elsewhere"))));

        // Membership in the other direction: a privileged role that is a member of the current role confers nothing on it.
        Assert.Equal(new RoleMembershipFacts(1, 0, false), await RunAsync(Roles(("admin", true)), Edges(("admin", Self))));

        // The current role being privileged itself is the role check's business, not the membership check's.
        Assert.Equal(new RoleMembershipFacts(1, 0, false), await RunAsync(Roles(("unrelated", false)), Edges(), selfPrivileged: true));
    }

    [Fact]
    public async Task Cycles_end_the_walk_and_a_privileged_role_inside_a_cycle_is_still_found()
    {
        // A cycle of ordinary roles ends: each role is reached once.
        Assert.Equal(
            new RoleMembershipFacts(1, 3, false),
            await RunAsync(Roles(("a", false), ("b", false), ("c", false)), Edges((Self, "a"), ("a", "b"), ("b", "c"), ("c", "a"))));

        // A cycle back through the current role ends too.
        Assert.Equal(new RoleMembershipFacts(1, 2, false), await RunAsync(Roles(("a", false)), Edges((Self, "a"), ("a", Self))));

        // A privileged role on the cycle is found.
        Assert.Equal(
            new RoleMembershipFacts(1, 3, true),
            await RunAsync(Roles(("a", false), ("b", true), ("c", false)), Edges((Self, "a"), ("a", "b"), ("b", "c"), ("c", "a"))));
    }

    [Fact]
    public async Task A_catalog_the_check_cannot_read_fails_closed()
    {
        // A membership in a role that has no catalog entry.
        var dangling = await RunAsync(Roles(), Edges(), extraEdges: "(1::oid, 999::oid)");
        Assert.Equal(new RoleMembershipFacts(1, 1, true), dangling);
        Assert.True(RoleMembershipFacts.IsPrivileged([dangling]));

        // A role whose privilege the catalog does not state.
        var unknown = await RunAsync(Roles(("a", false)), Edges((Self, "a")), extraRoles: "(50::oid, 'b'::name, NULL::boolean)", extraEdges: "(2::oid, 50::oid)");
        Assert.Equal(new RoleMembershipFacts(1, 2, true), unknown);

        // The current role is missing from the catalog, or named twice.
        var missing = await RunAsync(Roles(), Edges(), omitSelf: true);
        Assert.Equal(new RoleMembershipFacts(0, 0, false), missing);
        Assert.True(RoleMembershipFacts.IsPrivileged([missing]));
        var twice = await RunAsync(Roles(), Edges(), extraRoles: "(77::oid, current_user, false)");
        Assert.Equal(2, twice.CurrentRoleRows);
        Assert.True(RoleMembershipFacts.IsPrivileged([twice]));
    }

    [Fact]
    public void Every_answer_other_than_one_well_formed_row_without_any_membership_is_privileged()
    {
        Assert.False(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(1, 0, false)]));

        // Any membership, however ordinary, is enough by itself (Decision 3).
        Assert.True(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(1, 1, false)]));
        Assert.True(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(1, 7, false)]));

        Assert.True(RoleMembershipFacts.IsPrivileged([]));
        Assert.True(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(1, 0, false), new RoleMembershipFacts(1, 0, false)]));
        Assert.True(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(0, 0, false)]));
        Assert.True(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(2, 0, false)]));
        Assert.True(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(-1, 0, false)]));
        Assert.True(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(1, -1, false)]));
        Assert.True(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(1, 0, null)]));
        Assert.True(RoleMembershipFacts.IsPrivileged([new RoleMembershipFacts(1, 3, true)]));

        // The role facts: membership in any role is a privilege of its own, enough by itself.
        Assert.True(new DatabaseRoleFacts(false, false, false, false, false, false, false, false, false, false, MemberOfAnyRole: true).IsPrivileged);
        Assert.False(new DatabaseRoleFacts(false, false, false, false, false, false, false, false, false, false, false).IsPrivileged);
        Assert.True(DatabaseRoleFacts.Unknown.MemberOfAnyRole);
    }

    [Fact]
    public async Task On_the_real_catalog_the_statement_agrees_with_postgres_own_membership_test_for_both_local_roles()
    {
        // The runtime role: PostgreSQL's own recursive membership test finds exactly the roles the statement finds.
        await using (var runtime = new NpgsqlConnection(fixture.ApplicationConnectionString))
        {
            await runtime.OpenAsync(Token);
            var facts = await ReadAsync(runtime, DatabaseRoleCheck.MembershipStatement);
            Assert.Equal(new RoleMembershipFacts(1, await ScalarAsync(runtime, ReferenceMembers), false), facts);
        }

        // The schema owner is a superuser, for which PostgreSQL's test answers true for every role, so the reference there
        // is the membership catalog itself, recursively. The statement finds no membership, and the role check refuses
        // this role for being a superuser, not through membership.
        await using var owner = new NpgsqlConnection(fixture.AdministrativeConnectionString);
        await owner.OpenAsync(Token);
        Assert.Equal(1L, await ScalarAsync(owner, "SELECT count(*) FROM pg_roles WHERE rolname = current_user AND rolsuper;"));
        Assert.Equal(
            new RoleMembershipFacts(1, await ScalarAsync(owner, ReferenceCatalogMembers), false),
            await ReadAsync(owner, DatabaseRoleCheck.MembershipStatement));
    }

    private const string ReferenceMembers =
        "SELECT count(*) FROM pg_roles AS r WHERE r.rolname <> current_user AND pg_has_role(current_user, r.oid, 'MEMBER');";

    private const string ReferenceCatalogMembers =
        "WITH RECURSIVE up (roleid) AS (SELECT m.roleid FROM pg_auth_members AS m JOIN pg_roles AS r ON r.oid = m.member WHERE r.rolname = current_user "
        + "UNION SELECT m.roleid FROM pg_auth_members AS m JOIN up ON m.member = up.roleid) SELECT count(*) FROM up;";

    private async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private static int Count(string text, string fragment)
    {
        var count = 0;
        for (var index = text.IndexOf(fragment, StringComparison.Ordinal); index >= 0; index = text.IndexOf(fragment, index + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static (string Name, bool Privileged)[] Roles(params (string Name, bool Privileged)[] roles) => roles;

    private static (string Member, string Role)[] Edges(params (string Member, string Role)[] edges) => edges;

    // Runs the harness's statement with its two catalog projections replaced by a synthetic catalog. The current role is
    // oid 1 and named like the session's role; the others follow from oid 2. Read-only, as the runtime role.
    private async Task<RoleMembershipFacts> RunAsync(
        (string Name, bool Privileged)[] roles,
        (string Member, string Role)[] edges,
        bool selfPrivileged = false,
        bool omitSelf = false,
        string? extraRoles = null,
        string? extraEdges = null)
    {
        var oids = new Dictionary<string, int>(StringComparer.Ordinal) { [Self] = 1 };
        var roleRows = new List<string>();
        if (!omitSelf)
        {
            roleRows.Add($"(1::oid, current_user, {(selfPrivileged ? "true" : "false")})");
        }

        foreach (var (name, privileged) in roles)
        {
            oids[name] = oids.Count + 1;
            roleRows.Add($"({oids[name]}::oid, '{name}'::name, {(privileged ? "true" : "false")})");
        }

        if (extraRoles is not null)
        {
            roleRows.Add(extraRoles);
        }

        var edgeRows = edges.Select(edge => $"({oids[edge.Member]}::oid, {oids[edge.Role]}::oid)").ToList();
        if (extraEdges is not null)
        {
            edgeRows.Add(extraEdges);
        }

        var roleSource = roleRows.Count == 0
            ? "SELECT NULL::oid, NULL::name, NULL::boolean WHERE false"
            : "SELECT * FROM (VALUES " + string.Join(", ", roleRows) + ") AS v(oid, rolname, privileged)";
        var edgeSource = edgeRows.Count == 0
            ? "SELECT NULL::oid, NULL::oid WHERE false"
            : "SELECT * FROM (VALUES " + string.Join(", ", edgeRows) + ") AS v(member, roleid)";

        var statement = new StringBuilder(DatabaseRoleCheck.MembershipStatement);
        Assert.Equal(1, Count(statement.ToString(), RoleProjection));
        Assert.Equal(1, Count(statement.ToString(), MembershipProjection));
        statement.Replace(RoleProjection, roleSource).Replace(MembershipProjection, edgeSource);

        await using var connection = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Token);
        return await ReadAsync(connection, statement.ToString());
    }

    private async Task<RoleMembershipFacts> ReadAsync(NpgsqlConnection connection, string sql)
    {
        // A walk that did not end would hold the command until it times out, and fail the test.
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        var facts = new RoleMembershipFacts(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetBoolean(2));
        Assert.False(await reader.ReadAsync(Token));
        return facts;
    }
}
