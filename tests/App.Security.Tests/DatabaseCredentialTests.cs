namespace WorkContinuity.Security.Tests;

/// <summary>
/// Asserts that no database credential is embedded in source, configuration or migrations.
/// </summary>
/// <remarks>
/// Credentials belong in the untracked local <c>.env</c> now and in a managed secret store later. A
/// migration in particular must never contain one, because migrations are committed and replayed
/// everywhere.
/// </remarks>
public sealed class DatabaseCredentialTests
{
    private static IEnumerable<FileInfo> CommittedTextFiles()
    {
        // Bicep sources and parameter files are included: infrastructure is exactly where a connection
        // string with a literal password is most likely to be pasted.
        string[] extensions =
        [
            ".cs", ".json", ".yml", ".yaml", ".props", ".csproj", ".ps1", ".sh", ".sql", ".md",
            ".bicep", ".bicepparam",
        ];

        return RepositoryLayout.Root
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var path = file.FullName.Replace('\\', '/');

                return !path.Contains("/bin/", StringComparison.Ordinal)
                    && !path.Contains("/obj/", StringComparison.Ordinal)
                    && !path.Contains("/.git/", StringComparison.Ordinal)
                    // Test sources are excluded on purpose. They contain deliberately fake
                    // credential-shaped strings, including one whose whole job is to prove such a
                    // value never reaches an HTTP response. Flagging those would be noise, not a
                    // finding. Production source, configuration, scripts and migrations are covered.
                    && !path.Contains("/tests/", StringComparison.Ordinal)
                    && extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public void No_committed_file_contains_a_populated_password_in_a_connection_string()
    {
        var offenders = new List<string>();

        foreach (var file in CommittedTextFiles())
        {
            var lines = File.ReadAllLines(file.FullName);

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];

                if (!line.Contains("Password=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A value supplied by variable substitution or left as a placeholder is not a secret.
                // A value that comes from a variable, a placeholder, or an interpolation hole is
                // not a secret. Shell references such as "$POSTGRES_APP_PASSWORD" carry no braces,
                // so a bare $ followed by an identifier counts as substitution too.
                var isSubstituted = line.Contains("${", StringComparison.Ordinal)
                    || line.Contains("$env:", StringComparison.Ordinal)
                    || System.Text.RegularExpressions.Regex.IsMatch(line, @"\$[A-Za-z_][A-Za-z0-9_]*")
                    || line.Contains("Password=@", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("CHANGE_ME", StringComparison.Ordinal)
                    || line.Contains("Password=\"", StringComparison.Ordinal)
                    || line.Contains("Password={", StringComparison.Ordinal);

                if (!isSubstituted)
                {
                    offenders.Add($"{RepositoryLayout.RelativePath(file)}:{index + 1}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_migration_contains_a_credential_or_a_role_password()
    {
        var migrations = new DirectoryInfo(
                Path.Combine(
                    RepositoryLayout.Root.FullName,
                    "src",
                    "App.Infrastructure",
                    "Persistence",
                    "Migrations"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .ToList();

        Assert.NotEmpty(migrations);

        var offenders = new List<string>();

        foreach (var file in migrations)
        {
            var text = File.ReadAllText(file.FullName);

            foreach (var forbidden in new[] { "PASSWORD", "Password=", "SECRET", "CREATE ROLE", "CREATE USER" })
            {
                if (text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{RepositoryLayout.RelativePath(file)} contains '{forbidden}'");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_local_secret_file_is_never_committed()
    {
        var gitignore = File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, ".gitignore"));

        Assert.Contains(".env", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_template_contains_placeholders_only()
    {
        var example = File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, ".env.example"));

        // Both roles must be represented, and neither may ship a usable password.
        Assert.Contains("POSTGRES_PASSWORD=CHANGE_ME", example, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_APP_PASSWORD=CHANGE_ME", example, StringComparison.Ordinal);
    }

    [Fact]
    public void No_production_source_accepts_an_untrusted_request_tenant_into_persistence()
    {
        // The Gate 1C source scan covered tenant resolution generally. This narrows it to the
        // persistence surface: no file that touches the database may also read a tenant from request
        // input.
        string[] requestInputApis =
        [
            "Request.Query", "Request.Headers", "Request.Form", "Request.Cookies",
            "Request.Body", "Request.RouteValues", "FromQuery", "FromHeader", "FromRoute", "FromBody",
        ];

        var offenders = new List<string>();

        foreach (var file in RepositoryLayout.ProductionSourceFiles)
        {
            var text = File.ReadAllText(file.FullName);

            var touchesPersistence = text.Contains("ITenantScopedDatabase", StringComparison.Ordinal)
                || text.Contains("WorkContinuityDbContext", StringComparison.Ordinal)
                || text.Contains("AdministrativeDbContext", StringComparison.Ordinal);

            if (!touchesPersistence)
            {
                continue;
            }

            foreach (var api in requestInputApis)
            {
                if (text.Contains(api, StringComparison.Ordinal))
                {
                    offenders.Add(
                        $"{RepositoryLayout.RelativePath(file)} reaches persistence and reads request input '{api}'");
                }
            }
        }

        Assert.Empty(offenders);
    }
}
