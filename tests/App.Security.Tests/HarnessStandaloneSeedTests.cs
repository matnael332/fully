using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3G Stage B3, area B (owner decision B-14): the reviewed, owner-run seed of the future persistent standalone
/// harness database, <c>eng/harness-standalone-seed.sql</c>. Stage B3 authors it and never runs it, so every rule here is
/// proven from its text: it holds no credential and no connection details, it requires each value and defaults none,
/// it can only name a standalone harness database and must be connected to exactly that one, it writes one tenant and
/// one canonical user and nothing else, and it changes no database, role or privilege.
/// </summary>
public sealed class HarnessStandaloneSeedTests
{
    private const string SeedFile = "eng/harness-standalone-seed.sql";

    private static readonly string[] RequiredVariables = ["target_database", "tenant_id", "user_id", "identity_provider", "provider_subject"];

    [Fact]
    public void The_seed_holds_no_credential_no_connection_detail_and_no_real_identifier()
    {
        var text = Read();

        foreach (var forbidden in new[]
                 {
                     "PASSWORD", "Password", "password=", "PGPASSWORD", "Host=", "Username=", "User Id=", "Server=", "sslkey", "sslcert",
                     "postgres://", "postgresql://", "sb://", "SharedAccessKey", "Bearer ", "client_secret", "signing", "monday.com",
                     "vault.azure.net", "login.microsoftonline.com", "workcontinuity_app",
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        // No GUID, e-mail address or address of any kind is written into it: every value comes from a psql variable.
        Assert.DoesNotMatch(new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"), text.Replace("00000000-0000-0000-0000-000000000000", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotMatch(new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[a-z]{2,}"), text);
        Assert.DoesNotMatch(new Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"), text);
    }

    [Fact]
    public void Every_value_is_required_validated_and_never_defaulted()
    {
        var text = Read();
        var statements = Code();

        // psql stops at the first error, and each missing variable is an error before anything else runs.
        Assert.StartsWith(@"\set ON_ERROR_STOP on", statements[0], StringComparison.Ordinal);
        foreach (var variable in RequiredVariables)
        {
            var guard = $"\\if :{{?{variable}}}\n\\else\nDO $refuse$ BEGIN RAISE EXCEPTION 'seed: refused ({variable} is missing)'; END $refuse$;\n\\endif";
            Assert.Equal(1, Count(text, guard));
            Assert.True(text.IndexOf(guard, StringComparison.Ordinal) < text.IndexOf("BEGIN;", StringComparison.Ordinal));
            Assert.Equal(1, Count(text, $"set_config('wce_seed.{variable}', :'{variable}', true)"));
        }

        // The only psql variables it reads are the five required ones, and none is ever given a value in the file.
        var used = Regex.Matches(string.Join("\n", statements), @"(?<!:):'(\w+)'|(?<!:):\{\?(\w+)\}|(?<!:):""(\w+)""|(?<![:\w]):(\w+)\b(?!')")
            .Select(match => match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value)
            .Where(name => !Regex.IsMatch(name, @"^\d"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        Assert.Equal(RequiredVariables.Order(StringComparer.Ordinal), used);
        Assert.Equal(1, Regex.Count(text, @"^\\set ", RegexOptions.Multiline));

        // Each value is checked before any row is written.
        var insert = text.IndexOf("INSERT INTO", StringComparison.Ordinal);
        foreach (var check in new[]
                 {
                     "IF target IS NULL OR target <> current_database() THEN",
                     "IF current_database() !~ '^wce_harness_[a-z0-9_]{1,51}$' THEN",
                     "IF tenant_text !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'",
                     "OR user_text !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN",
                     "IF tenant = '00000000-0000-0000-0000-000000000000'::uuid",
                     "OR tenant = canonical_user THEN",
                     "IF provider IS NULL OR provider !~ '^[a-z0-9._-]{1,64}$' THEN",
                     "IF subject IS NULL OR subject !~ '^[!-~]{1,200}$' THEN",
                 })
        {
            Assert.Equal(1, Count(text, check));
            Assert.True(text.IndexOf(check, StringComparison.Ordinal) < insert, check);
        }
    }

    [Fact]
    public void It_can_be_aimed_only_at_the_connected_standalone_harness_database_and_never_at_the_shared_suite_database()
    {
        var text = Read();

        // It connects nowhere itself: no \connect, no include, no shell, no output redirection, no database link.
        foreach (var forbidden in new[] { @"\c ", @"\connect", @"\i ", @"\ir ", @"\include", @"\!", @"\o", @"\g ", @"\gexec", @"\copy", "dblink", "postgres_fdw", "COPY " })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        // The name rule, as PostgreSQL applies it (an anchored, case-sensitive POSIX pattern of literal characters, whose
        // meaning is the same in .NET), refuses the shared suite database, the suite's own test databases and the
        // runtime role's name, and accepts the form B-14's plan gives as an example.
        var rule = new Regex("^wce_harness_[a-z0-9_]{1,51}$", RegexOptions.CultureInvariant);
        foreach (var refused in new[] { "workcontinuity", "postgres", "template1", "wce_it_gate3b_privileges", "wce_harness_", "WCE_HARNESS_LIVE", "wce_harness_live;drop", "x_wce_harness_live", "wce_harness_" + new string('a', 52) })
        {
            Assert.DoesNotMatch(rule, refused);
        }

        Assert.Matches(rule, "wce_harness_live_20261006");
        Assert.Matches(rule, "wce_harness_" + new string('a', 51));
        Assert.True(("wce_harness_" + new string('a', 51)).Length <= 63, "PostgreSQL identifiers are at most 63 bytes.");

        // Emptiness must be provable by the session, and the target must be migrated exactly to this repository's latest
        // migration and hold no row at all.
        foreach (var check in new[]
                 {
                     "SELECT r.rolsuper OR r.rolbypassrls INTO sees_every_row FROM pg_catalog.pg_roles AS r WHERE r.rolname = current_user;",
                     "IF sees_every_row IS NOT TRUE THEN",
                     "IF to_regclass('public.\"__EFMigrationsHistory\"') IS NULL THEN",
                     "WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relname <> '__EFMigrationsHistory'",
                     "EXECUTE format('SELECT EXISTS (SELECT 1 FROM public.%I)', table_name) INTO occupied;",
                     "IF occupied THEN",
                     "AND c.relrowsecurity AND c.relforcerowsecurity) <> 2 THEN",
                 })
        {
            Assert.Equal(1, Count(text, check));
        }

        var migrations = typeof(WorkContinuityDbContext).Assembly.GetTypes()
            .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Id)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(1, Count(text, $"IF applied <> {migrations.Count} OR latest IS DISTINCT FROM '{migrations[^1]}' THEN"));
    }

    [Fact]
    public void It_writes_exactly_one_tenant_and_one_canonical_user_in_one_transaction_and_changes_nothing_else()
    {
        var text = Read();
        var code = string.Join("\n", Code());

        // Nothing that creates, removes or reconfigures a database, a schema, a role or a privilege, and no other write.
        Assert.DoesNotMatch(
            new Regex(@"\b(CREATE|DROP|ALTER|GRANT|REVOKE|TRUNCATE|DELETE|UPDATE|MERGE|VACUUM|REINDEX|CLUSTER|COMMENT|SECURITY|OWNER|REASSIGN|LISTEN|NOTIFY|LOCK|PERFORM)\b", RegexOptions.IgnoreCase),
            Regex.Replace(code, @"'[^']*'", "''"));
        Assert.DoesNotMatch(new Regex(@"\bSET\s+(ROLE|SESSION|LOCAL\s+ROLE)\b|\bset_role\b|pg_terminate|pg_cancel|lo_import|lo_export|pg_read_file|pg_write", RegexOptions.IgnoreCase), code);

        // One transaction around everything that touches data.
        Assert.Equal(1, Regex.Count(code, @"^BEGIN;$", RegexOptions.Multiline));
        Assert.Equal(1, Regex.Count(code, @"^COMMIT;$", RegexOptions.Multiline));
        Assert.DoesNotContain("ROLLBACK", code, StringComparison.Ordinal);
        var begin = code.IndexOf("\nBEGIN;\n", StringComparison.Ordinal);
        var commit = code.IndexOf("\nCOMMIT;\n", StringComparison.Ordinal);
        Assert.True(begin > 0 && commit > begin);

        // Exactly two inserts, inside the transaction and after every refusal.
        var inserts = Regex.Matches(code, @"INSERT INTO ([a-z_.]+) \(([^)]*)\)").ToList();
        Assert.Equal(["public.tenants", "public.users"], inserts.Select(match => match.Groups[1].Value));
        Assert.All(inserts, match => Assert.True(match.Index > begin && match.Index < commit));
        Assert.True(inserts[0].Index > code.LastIndexOf("RAISE EXCEPTION 'seed: refused (tenants and users", StringComparison.Ordinal));
        Assert.Equal(1, Count(text, "VALUES (tenant, 'Active', now());"));
        Assert.Equal(1, Count(text, "VALUES (canonical_user, tenant, provider, subject, true, now());"));

        // Each insert names every column its table has, as the product model defines it, so nothing is left to a default.
        using var context = new WorkContinuityDbContext(new DbContextOptionsBuilder<WorkContinuityDbContext>().UseNpgsql("Host=unused").Options);
        foreach (var insert in inserts)
        {
            var table = insert.Groups[1].Value["public.".Length..];
            var entity = Assert.Single(context.Model.GetEntityTypes(), type => type.GetTableName() == table);
            Assert.Equal(
                entity.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal),
                insert.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal));
        }

        // Afterwards the target holds exactly that tenant and that user, and no connection: the product's own Connect
        // creates the connection and its authorization together.
        Assert.Equal(1, Count(text, "IF (SELECT count(*) FROM public.tenants) <> 1 OR (SELECT count(*) FROM public.users) <> 1"));
        Assert.Equal(1, Count(text, "OR (SELECT count(*) FROM public.connections) <> 0 THEN"));
        Assert.DoesNotContain("connector_authorizations", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_in_the_repository_runs_the_seed()
    {
        // It is not an init script of the local server and not part of the local stack. Apart from documentation and the
        // tests, only its one supported entry point names it (owner decision, 2026-10-06), and nothing in the repository
        // runs that entry point (HarnessStandaloneWrapperTests).
        Assert.False(File.Exists(Path.Combine(RepositoryLayout.Root.FullName, "infra", "local", "postgres", "init", "harness-standalone-seed.sql")));
        var holders = Directory.EnumerateFiles(RepositoryLayout.Root.FullName, "*", SearchOption.AllDirectories)
            .Where(path => !path.Replace('\\', '/').Contains("/.git/", StringComparison.Ordinal)
                && !path.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal)
                && !path.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal)
                && !path.Replace('\\', '/').Contains("/.claude/", StringComparison.Ordinal))
            .Where(path => Path.GetExtension(path) is ".cs" or ".ps1" or ".sh" or ".yml" or ".yaml" or ".json" or ".py" or ".bicep" or ".sql" or ".csproj" or ".props" or ".targets")
            .Where(path => File.ReadAllText(path).Contains("harness-standalone-seed", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryLayout.Root.FullName, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            ["eng/harness-standalone.ps1", "tests/App.Security.Tests/HarnessStandaloneSeedTests.cs", "tests/App.Security.Tests/HarnessStandaloneWrapperTests.cs"],
            holders);
    }

    private static string Read() =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, SeedFile)).Replace("\r\n", "\n", StringComparison.Ordinal);

    // The file's lines without its comments.
    private static List<string> Code() =>
        Read().Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal) && line.Trim().Length > 0).ToList();

    private static int Count(string text, string fragment) => Regex.Count(text, Regex.Escape(fragment));
}
