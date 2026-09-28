namespace WorkContinuity.Integration.Tests.Database;

/// <summary>
/// Reads local database settings from the process environment, falling back to the repository's
/// untracked <c>.env</c>.
/// </summary>
/// <remarks>
/// <c>.env</c> is already the documented home for local-only credentials and is excluded from source
/// control, so reading it keeps the test suite runnable straight after <c>./eng/dev.ps1 up</c> with
/// no extra manual step. Process environment variables win, which is how CI or a developer overrides
/// a single value without editing the file.
/// </remarks>
internal static class LocalEnvironment
{
    private static readonly string[] Keys =
    [
        "POSTGRES_TEST_HOST",
        "POSTGRES_PORT",
        "POSTGRES_DB",
        "POSTGRES_USER",
        "POSTGRES_PASSWORD",
        "POSTGRES_APP_USER",
        "POSTGRES_APP_PASSWORD",
        "ACCEPT_EULA",
        "SERVICEBUS_TEST_CONNECTION_STRING",
    ];

    /// <summary>
    /// Whether a Service Bus emulator is expected: the local stack accepted its licence, or a test
    /// connection string was supplied explicitly.
    /// </summary>
    internal static bool EmulatorExpected
    {
        get
        {
            var settings = Read();

            return settings.ContainsKey("SERVICEBUS_TEST_CONNECTION_STRING")
                || string.Equals(settings.GetValueOrDefault("ACCEPT_EULA"), "Y", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static IReadOnlyDictionary<string, string> Read()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in ReadDotEnvLines())
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();

            if (Keys.Contains(key, StringComparer.Ordinal))
            {
                settings[key] = value;
            }
        }

        // An explicit environment variable overrides the file.
        foreach (var key in Keys)
        {
            var value = Environment.GetEnvironmentVariable(key);

            if (!string.IsNullOrWhiteSpace(value))
            {
                settings[key] = value;
            }
        }

        return settings;
    }

    private static IEnumerable<string> ReadDotEnvLines()
    {
        var envFile = Path.Combine(RepositoryRoot().FullName, ".env");

        return File.Exists(envFile) ? File.ReadAllLines(envFile) : [];
    }

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("WorkContinuityEngine.sln").Any())
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
