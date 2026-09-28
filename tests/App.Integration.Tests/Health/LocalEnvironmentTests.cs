using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Health;

/// <summary>
/// The rule that decides whether the Service Bus emulator tests run or skip.
/// </summary>
/// <remarks>
/// Only the repository's <c>.env</c> licence line (<c>ACCEPT_EULA=Y</c>) proves that the local compose
/// stack hosts the emulator. A process-level <c>ACCEPT_EULA</c> is a generic Microsoft licence flag:
/// the GitHub Ubuntu runner image exports <c>ACCEPT_EULA=Y</c> globally, and honouring it there made
/// CI run the emulator tests against a port nothing listens on. An explicit test connection string,
/// from either source, always means an emulator is reachable.
/// </remarks>
public sealed class LocalEnvironmentTests
{
    private static readonly IReadOnlyDictionary<string, string> NoDotEnv = new Dictionary<string, string>(StringComparer.Ordinal);

    private static Func<string, string?> Environment(params (string Key, string Value)[] variables) =>
        key => variables.FirstOrDefault(variable => variable.Key == key).Value;

    [Fact]
    public void A_process_level_ACCEPT_EULA_does_not_imply_an_emulator()
    {
        Assert.False(LocalEnvironment.IsEmulatorExpected(NoDotEnv, Environment(("ACCEPT_EULA", "Y"))));
    }

    [Fact]
    public void The_dot_env_licence_line_implies_the_local_emulator()
    {
        var dotEnv = new Dictionary<string, string>(StringComparer.Ordinal) { ["ACCEPT_EULA"] = "Y" };

        Assert.True(LocalEnvironment.IsEmulatorExpected(dotEnv, Environment()));
    }

    [Fact]
    public void A_declined_dot_env_licence_means_no_emulator()
    {
        var dotEnv = new Dictionary<string, string>(StringComparer.Ordinal) { ["ACCEPT_EULA"] = "N" };

        Assert.False(LocalEnvironment.IsEmulatorExpected(dotEnv, Environment(("ACCEPT_EULA", "Y"))));
    }

    [Fact]
    public void An_explicit_test_connection_string_implies_an_emulator_from_either_source()
    {
        var dotEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SERVICEBUS_TEST_CONNECTION_STRING"] = "Endpoint=sb://emulator;UseDevelopmentEmulator=true;",
        };

        Assert.True(LocalEnvironment.IsEmulatorExpected(dotEnv, Environment()));
        Assert.True(LocalEnvironment.IsEmulatorExpected(
            NoDotEnv,
            Environment(("SERVICEBUS_TEST_CONNECTION_STRING", "Endpoint=sb://emulator;UseDevelopmentEmulator=true;"))));
    }

    [Fact]
    public void Nothing_configured_means_no_emulator()
    {
        Assert.False(LocalEnvironment.IsEmulatorExpected(NoDotEnv, Environment()));
    }
}
