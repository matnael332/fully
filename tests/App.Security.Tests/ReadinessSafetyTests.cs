using System.Diagnostics;
using System.Text.Json;
using WorkContinuity.Infrastructure.Health;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// The readiness body is returned to callers, so it must never carry a credential, a host or a
/// message, and the probe must never throw or hang: an unreachable dependency is reported, bounded
/// by the configured timeout, as a type name or reason code only.
/// </summary>
public sealed class ReadinessSafetyTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Readiness_never_throws_and_never_echoes_connection_details()
    {
        // Deliberately fake values on a port nothing listens on; the scan of committed files skips tests.
        const string password = "NOT-A-REAL-SECRET-readiness-test";
        const string key = "NOT-A-REAL-KEY-readiness-test";

        var options = new DependencyProbeOptions
        {
            PostgresConnectionString = $"Host=127.0.0.1;Port=1;Database=readiness;Username=readinessuser;Password={password}",
            ServiceBusConnectionString =
                $"Endpoint=sb://127.0.0.1:1;SharedAccessKeyName=readinesskey;SharedAccessKey={key};UseDevelopmentEmulator=true;",
            ServiceBusMode = ServiceBusReadinessMode.Send,
            Timeout = TimeSpan.FromSeconds(3),
        };

        await using var probe = new DependencyProbe(options);
        var stopwatch = Stopwatch.StartNew();

        var report = await probe.CheckAsync(Token);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"readiness took {stopwatch.Elapsed}");
        Assert.False(report.Ready);
        Assert.Equal(
            [DependencyProbe.PostgresDependency, DependencyProbe.ServiceBusDependency],
            report.Dependencies.Select(dependency => dependency.Name).ToArray());
        Assert.All(report.Dependencies, dependency => Assert.False(dependency.Healthy));

        var body = JsonSerializer.Serialize(report);

        foreach (var leak in new[] { password, key, "127.0.0.1", "Password", "SharedAccessKey", "readinessuser", "readinesskey" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Unconfigured_dependencies_are_reported_as_not_configured_and_never_as_healthy()
    {
        await using var probe = new DependencyProbe(new DependencyProbeOptions());

        var report = await probe.CheckAsync(Token);

        Assert.False(report.Ready);
        Assert.All(report.Dependencies, dependency =>
        {
            Assert.False(dependency.Healthy);
            Assert.Equal(DependencyProbe.NotConfigured, dependency.Detail);
        });
    }

    [Fact]
    public void The_default_readiness_queue_is_the_sessionless_canonical_queue()
    {
        // Peeking needs a plain receiver, which a session-enabled queue would refuse.
        Assert.Equal("ai-jobs", DependencyProbeOptions.DefaultQueueName);
    }
}
