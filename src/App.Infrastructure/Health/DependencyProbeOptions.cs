using Microsoft.Extensions.Configuration;

namespace WorkContinuity.Infrastructure.Health;

/// <summary>
/// Which Service Bus claim the process holds, and therefore which side-effect-free operation proves
/// the link.
/// </summary>
public enum ServiceBusReadinessMode
{
    /// <summary>
    /// Open a send link to the queue. Needs the Data Sender claim. No message is created or sent.
    /// </summary>
    Send,

    /// <summary>
    /// Peek one message on the queue. Needs the Data Receiver claim. Peeking neither locks nor
    /// removes a message.
    /// </summary>
    Receive,
}

/// <summary>
/// Settings for <see cref="DependencyProbe"/>, supplied entirely by configuration or environment.
/// </summary>
/// <remarks>
/// In Azure the Service Bus namespace comes from <c>ServiceBus__FullyQualifiedNamespace</c> and the
/// process authenticates with its user-assigned managed identity (<c>AZURE_CLIENT_ID</c>). Locally
/// the emulator is addressed through <c>ServiceBus__ConnectionString</c>, whose only "key" is the
/// emulator's fixed, published development value. The PostgreSQL connection string is the same
/// runtime-role string request handling uses, so readiness proves the real access path.
/// </remarks>
public sealed class DependencyProbeOptions
{
    /// <summary>The configuration section holding the Service Bus settings.</summary>
    public const string ServiceBusSection = "ServiceBus";

    /// <summary>
    /// The queue used for readiness. <c>ai-jobs</c> is the one canonical queue without sessions, so
    /// a plain receiver can peek it and a plain sender can open a link to it.
    /// </summary>
    public const string DefaultQueueName = "ai-jobs";

    /// <summary>The environment variable Azure sets for the workload's user-assigned identity.</summary>
    public const string ManagedIdentityClientIdVariable = "AZURE_CLIENT_ID";

    /// <summary>Gets or sets the application (runtime-role) PostgreSQL connection string.</summary>
    public string PostgresConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the Service Bus namespace host, for managed-identity authentication.</summary>
    public string ServiceBusFullyQualifiedNamespace { get; set; } = string.Empty;

    /// <summary>Gets or sets the Service Bus connection string, used only for the local emulator.</summary>
    public string ServiceBusConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the queue the readiness check targets.</summary>
    public string ServiceBusQueueName { get; set; } = DefaultQueueName;

    /// <summary>Gets or sets which claim the process holds.</summary>
    public ServiceBusReadinessMode ServiceBusMode { get; set; } = ServiceBusReadinessMode.Send;

    /// <summary>Gets or sets the client id of the user-assigned identity, when running in Azure.</summary>
    public string ManagedIdentityClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the per-dependency time budget.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets a value indicating whether a PostgreSQL connection string is present.</summary>
    public bool PostgresConfigured => !string.IsNullOrWhiteSpace(PostgresConnectionString);

    /// <summary>Gets a value indicating whether a Service Bus target is present.</summary>
    public bool ServiceBusConfigured =>
        !string.IsNullOrWhiteSpace(ServiceBusFullyQualifiedNamespace)
        || !string.IsNullOrWhiteSpace(ServiceBusConnectionString);

    /// <summary>Reads the options from configuration; nothing is compiled in.</summary>
    public static DependencyProbeOptions FromConfiguration(
        IConfiguration configuration,
        ServiceBusReadinessMode mode)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceBus = configuration.GetSection(ServiceBusSection);

        return new DependencyProbeOptions
        {
            PostgresConnectionString = configuration.GetConnectionString("Postgres") ?? string.Empty,
            ServiceBusFullyQualifiedNamespace = serviceBus["FullyQualifiedNamespace"] ?? string.Empty,
            ServiceBusConnectionString = serviceBus["ConnectionString"] ?? string.Empty,
            ServiceBusQueueName = serviceBus["ReadinessQueue"] ?? DefaultQueueName,
            ServiceBusMode = mode,
            ManagedIdentityClientId = configuration[ManagedIdentityClientIdVariable] ?? string.Empty,
        };
    }
}
