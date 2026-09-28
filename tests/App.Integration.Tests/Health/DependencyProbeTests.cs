using Npgsql;
using WorkContinuity.Infrastructure.Health;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Health;

/// <summary>
/// The readiness probe against the real local dependencies: PostgreSQL as the runtime role, and the
/// Service Bus emulator through both the send-link and the peek check.
/// </summary>
/// <remarks>
/// The PostgreSQL checks share the database fixture and fail, never skip, when the database is down.
/// The Service Bus checks need the emulator, which runs locally under <c>./eng/dev.ps1 up</c> but not
/// in the CI job that hosts only PostgreSQL; they skip with an explicit reason when no emulator is
/// expected, and run whenever <c>.env</c> accepts the emulator licence or a test connection string
/// is supplied.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class DependencyProbeTests(DatabaseFixture fixture)
{
    private const string DefaultEmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static string EmulatorConnectionString =>
        LocalEnvironment.Read().GetValueOrDefault("SERVICEBUS_TEST_CONNECTION_STRING", DefaultEmulatorConnectionString);

    [Fact]
    public async Task Postgres_readiness_succeeds_as_the_runtime_role()
    {
        await using var probe = new DependencyProbe(new DependencyProbeOptions
        {
            PostgresConnectionString = fixture.ApplicationConnectionString,
        });

        var report = await probe.CheckAsync(Token);
        var postgres = report.Dependencies.Single(dependency => dependency.Name == DependencyProbe.PostgresDependency);

        Assert.True(postgres.Healthy, postgres.Detail);
        Assert.Null(postgres.Detail);
    }

    [Fact]
    public async Task Postgres_readiness_reports_a_wrong_role_password_as_an_authentication_state_only()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            Password = "definitely-not-the-runtime-role-password",
        };

        await using var probe = new DependencyProbe(new DependencyProbeOptions
        {
            PostgresConnectionString = builder.ConnectionString,
        });

        var report = await probe.CheckAsync(Token);
        var postgres = report.Dependencies.Single(dependency => dependency.Name == DependencyProbe.PostgresDependency);

        // 28P01 is invalid_password: the probe authenticated as the role, which a TCP check never could.
        Assert.False(postgres.Healthy);
        Assert.Equal("PostgresException:28P01", postgres.Detail);
    }

    [Fact]
    public async Task Service_bus_send_link_readiness_succeeds_against_the_emulator()
    {
        RequireEmulator();

        await using var probe = new DependencyProbe(new DependencyProbeOptions
        {
            ServiceBusConnectionString = EmulatorConnectionString,
            ServiceBusMode = ServiceBusReadinessMode.Send,
            Timeout = TimeSpan.FromSeconds(10),
        });

        var report = await probe.CheckAsync(Token);
        var serviceBus = report.Dependencies.Single(dependency => dependency.Name == DependencyProbe.ServiceBusDependency);

        Assert.True(serviceBus.Healthy, serviceBus.Detail);
    }

    [Fact]
    public async Task Service_bus_peek_readiness_succeeds_against_the_emulator()
    {
        RequireEmulator();

        await using var probe = new DependencyProbe(new DependencyProbeOptions
        {
            ServiceBusConnectionString = EmulatorConnectionString,
            ServiceBusMode = ServiceBusReadinessMode.Receive,
            Timeout = TimeSpan.FromSeconds(10),
        });

        var report = await probe.CheckAsync(Token);
        var serviceBus = report.Dependencies.Single(dependency => dependency.Name == DependencyProbe.ServiceBusDependency);

        Assert.True(serviceBus.Healthy, serviceBus.Detail);
    }

    [Fact]
    public async Task A_missing_queue_is_reported_as_a_reason_code_not_a_message()
    {
        RequireEmulator();

        await using var probe = new DependencyProbe(new DependencyProbeOptions
        {
            ServiceBusConnectionString = EmulatorConnectionString,
            ServiceBusQueueName = "queue-that-does-not-exist",
            ServiceBusMode = ServiceBusReadinessMode.Send,
            Timeout = TimeSpan.FromSeconds(10),
        });

        var report = await probe.CheckAsync(Token);
        var serviceBus = report.Dependencies.Single(dependency => dependency.Name == DependencyProbe.ServiceBusDependency);

        Assert.False(serviceBus.Healthy);
        Assert.NotNull(serviceBus.Detail);
        Assert.StartsWith("ServiceBusException:", serviceBus.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("queue-that-does-not-exist", serviceBus.Detail, StringComparison.Ordinal);
    }

    private static void RequireEmulator()
    {
        if (!LocalEnvironment.EmulatorExpected)
        {
            Assert.Skip(
                "No Service Bus emulator is expected here (.env has no ACCEPT_EULA=Y and "
                + "SERVICEBUS_TEST_CONNECTION_STRING is unset). Run './eng/dev.ps1 up' locally to execute this test.");
        }
    }
}
