using System.Text.RegularExpressions;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Static guarantees for the GitHub Actions workflows: minimal token permissions, no stored secret
/// or long-lived Azure credential, every action and runner pinned, continuous delivery limited to a
/// manual dispatch from <c>main</c> that can write nothing but the two Container Apps' images, and a
/// secret scanner pinned by version and checksum.
/// </summary>
/// <remarks>
/// The workflows are read with a deliberately small, indentation-based reader rather than a YAML
/// library, so the security tests keep their single test dependency. The assertions target the
/// exact shapes committed in Gates 1F and 1G; a structural change to a workflow is meant to fail here
/// until the corresponding guarantee is re-established.
/// </remarks>
public sealed class WorkflowSecurityTests
{
    private static readonly IReadOnlyList<WorkflowFile> Workflows =
        new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, ".github", "workflows"))
            .EnumerateFiles("*.yml")
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(WorkflowFile.Load)
            .ToList();

    private static WorkflowFile Ci => Workflows.Single(workflow => workflow.Name == "ci.yml");

    private static WorkflowFile Cd => Workflows.Single(workflow => workflow.Name == "cd-dev.yml");

    [Fact]
    public void Exactly_the_two_expected_workflows_exist()
    {
        Assert.Equal(["cd-dev.yml", "ci.yml"], Workflows.Select(workflow => workflow.Name).ToArray());
    }

    [Fact]
    public void Every_workflow_declares_minimal_workflow_level_permissions_and_no_job_widens_them()
    {
        foreach (var workflow in Workflows)
        {
            var permissions = workflow.Entries(workflow.TopLevelBlock("permissions")).ToList();

            Assert.True(permissions.Count > 0, $"{workflow.Name} must declare workflow-level permissions");
            Assert.Equal(1, workflow.CountLines(@"^\s*permissions:"));
            Assert.DoesNotContain("write-all", workflow.Text, StringComparison.Ordinal);

            foreach (var (key, value) in permissions)
            {
                Assert.Contains(value, new[] { "read", "write" });

                if (value == "write")
                {
                    Assert.Equal("id-token", key);
                }
            }
        }

        Assert.Equal(["contents=read"], Render(Ci.Entries(Ci.TopLevelBlock("permissions"))));
        Assert.Equal(["contents=read", "id-token=write"], Render(Cd.Entries(Cd.TopLevelBlock("permissions"))));
    }

    [Fact]
    public void No_workflow_uses_a_stored_secret_or_a_long_lived_azure_credential()
    {
        foreach (var workflow in Workflows)
        {
            Assert.DoesNotMatch(@"\$\{\{\s*secrets\.", workflow.Text);

            foreach (var forbidden in new[] { "AZURE_CLIENT_SECRET", "creds:", "client-secret", "ClientSecret", "SharedAccessKey=" })
            {
                Assert.DoesNotContain(forbidden, workflow.Text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Every_action_is_pinned_to_a_full_commit_sha()
    {
        var uses = Workflows
            .SelectMany(workflow => workflow.Lines)
            .Where(line => Regex.IsMatch(line, @"^\s*-?\s*uses:\s*"))
            .ToList();

        Assert.NotEmpty(uses);

        foreach (var line in uses)
        {
            var reference = Regex.Match(line, @"uses:\s*(\S+)").Groups[1].Value;
            Assert.Matches(@"^[\w.-]+/[\w.-]+(/[\w./-]+)?@[0-9a-f]{40}$", reference);
        }
    }

    [Fact]
    public void Every_job_runs_on_the_pinned_ubuntu_release()
    {
        var runners = Workflows
            .SelectMany(workflow => workflow.Lines)
            .Where(line => line.TrimStart().StartsWith("runs-on:", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(runners);
        Assert.All(runners, line => Assert.Equal("runs-on: ubuntu-24.04", line.Trim()));
    }

    [Fact]
    public void Continuous_delivery_runs_only_by_manual_dispatch_from_main_under_a_strict_shell()
    {
        var triggers = Cd.TopLevelBlock("on")
            .Where(line => Regex.IsMatch(line, @"^  \S"))
            .Select(line => line.Trim().TrimEnd(':'))
            .ToArray();

        Assert.Equal(["workflow_dispatch"], triggers);

        var jobs = Cd.JobNames();
        Assert.Equal(["publish", "deploy"], jobs);

        foreach (var job in jobs)
        {
            var condition = Cd.JobBlock(job).SingleOrDefault(line => Regex.IsMatch(line, @"^    if:"));
            Assert.NotNull(condition);
            Assert.Contains("github.ref == 'refs/heads/main'", condition, StringComparison.Ordinal);
        }

        Assert.Contains(Cd.TopLevelBlock("defaults"), line => line.Trim() == "shell: bash");
        Assert.NotEmpty(Cd.TopLevelBlock("concurrency"));
    }

    [Fact]
    public void Continuous_delivery_writes_only_the_two_container_apps_and_asserts_it_cannot_read_the_migration_job()
    {
        Assert.Contains("Assert the privilege boundary", Cd.Text, StringComparison.Ordinal);
        Assert.Contains("AuthorizationFailed", Cd.Text, StringComparison.Ordinal);

        // The one and only write is the template-only PATCH of a Container App.
        var writes = Cd.Lines
            .Where(line => Regex.IsMatch(line, @"--method\s+(patch|put|post|delete)", RegexOptions.IgnoreCase))
            .ToList();

        var write = Assert.Single(writes);
        Assert.Contains("--method patch", write, StringComparison.Ordinal);
        Assert.Contains("app_url", write, StringComparison.Ordinal);

        foreach (var line in Cd.Lines.Where(line => line.Contains("Microsoft.App/jobs", StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch(@"--method\s+(patch|put|post|delete)", line);
        }

        string[] forbidden =
        [
            "az deployment", "az role assignment", "Microsoft.Authorization", "az containerapp job",
            "az keyvault", "az postgres", "--migrate-database", "--bootstrap-runtime-role",
        ];

        foreach (var fragment in forbidden)
        {
            Assert.DoesNotContain(fragment, Cd.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Continuous_integration_never_authenticates_to_azure_or_pushes_an_image()
    {
        foreach (var fragment in new[] { "azure/login", "id-token", "az login", "az deployment", "az acr login", "docker push" })
        {
            Assert.DoesNotContain(fragment, Ci.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_secret_scanner_is_pinned_by_version_and_checksum_and_scans_history_and_tree()
    {
        Assert.Contains("gitleaks_8.30.1_linux_x64.tar.gz", Ci.Text, StringComparison.Ordinal);
        Assert.Matches(@"[0-9a-f]{64}\s+gitleaks\.tar\.gz", Ci.Text);
        Assert.Contains("sha256sum --check", Ci.Text, StringComparison.Ordinal);
        Assert.Contains("gitleaks git ", Ci.Text, StringComparison.Ordinal);
        Assert.Contains("gitleaks dir ", Ci.Text, StringComparison.Ordinal);

        var configuration = File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, ".gitleaks.toml"));
        Assert.Contains("useDefault = true", configuration, StringComparison.Ordinal);
    }

    private static string[] Render(IEnumerable<(string Key, string Value)> entries) =>
        entries.Select(entry => $"{entry.Key}={entry.Value}").OrderBy(entry => entry, StringComparer.Ordinal).ToArray();

    private sealed class WorkflowFile
    {
        public required string Name { get; init; }

        public required string Text { get; init; }

        public required string[] Lines { get; init; }

        public static WorkflowFile Load(FileInfo file)
        {
            var text = File.ReadAllText(file.FullName);

            return new WorkflowFile
            {
                Name = file.Name,
                Text = text,
                Lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray(),
            };
        }

        /// <summary>The indented lines under a top-level key, up to the next top-level key.</summary>
        public IReadOnlyList<string> TopLevelBlock(string key)
        {
            var start = Array.FindIndex(
                Lines,
                line => line == key + ":" || line.StartsWith(key + ": ", StringComparison.Ordinal));

            if (start < 0)
            {
                return [];
            }

            var block = new List<string>();

            for (var index = start + 1; index < Lines.Length; index++)
            {
                var line = Lines[index];

                if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
                {
                    break;
                }

                block.Add(line);
            }

            return block;
        }

        /// <summary>The <c>key: value</c> pairs of a block, comments stripped.</summary>
        public IEnumerable<(string Key, string Value)> Entries(IEnumerable<string> block)
        {
            foreach (var raw in block)
            {
                var line = raw.Split('#')[0].Trim();
                var separator = line.IndexOf(':', StringComparison.Ordinal);

                if (line.Length == 0 || separator <= 0)
                {
                    continue;
                }

                yield return (line[..separator].Trim(), line[(separator + 1)..].Trim());
            }
        }

        public int CountLines(string pattern) => Lines.Count(line => Regex.IsMatch(line, pattern));

        public string[] JobNames() =>
            TopLevelBlock("jobs")
                .Where(line => Regex.IsMatch(line, @"^  [A-Za-z0-9_-]+:\s*$"))
                .Select(line => line.Trim().TrimEnd(':'))
                .ToArray();

        public IReadOnlyList<string> JobBlock(string job)
        {
            var jobs = TopLevelBlock("jobs").ToList();
            var start = jobs.FindIndex(line => line.Trim() == job + ":" && line.StartsWith("  ", StringComparison.Ordinal));

            if (start < 0)
            {
                return [];
            }

            var block = new List<string>();

            for (var index = start + 1; index < jobs.Count; index++)
            {
                if (Regex.IsMatch(jobs[index], @"^  \S"))
                {
                    break;
                }

                block.Add(jobs[index]);
            }

            return block;
        }
    }
}
