using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Npgsql;

namespace WorkContinuity.Infrastructure.Health;

/// <summary>One dependency's readiness. <see cref="Detail"/> is a type name or reason code only.</summary>
public sealed record DependencyStatus(string Name, bool Healthy, string? Detail = null);

/// <summary>The readiness verdict and the per-dependency statuses behind it.</summary>
public sealed record ReadinessReport(bool Ready, IReadOnlyList<DependencyStatus> Dependencies);

/// <summary>
/// Proves the real dependencies with side-effect-free operations under the process's own credentials.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL: opens a connection with the application connection string, that is as the non-owner
/// runtime role, and runs <c>SELECT 1</c>. This covers the network path, TLS, the role's existence and
/// its password: exactly what request handling needs, and what a TCP connect could never prove.
/// </para>
/// <para>
/// Service Bus: in <see cref="ServiceBusReadinessMode.Send"/> mode it opens a send link to the queue,
/// which the SDK does by fetching the link's limits from the service; that requires the Data Sender
/// claim and creates or sends no message. In <see cref="ServiceBusReadinessMode.Receive"/> mode it
/// peeks one message, which requires the Data Receiver claim and neither locks nor removes anything.
/// Locally the same code talks to the emulator through its connection string; in Azure it
/// authenticates with the workload's user-assigned managed identity. Clients and links are created
/// once and reused, as the SDK intends.
/// </para>
/// <para>
/// The probe never throws. Detail values are exception type names and provider reason codes only:
/// never messages, hosts, users or credentials, so the readiness body can be returned to a caller.
/// </para>
/// </remarks>
public sealed class DependencyProbe : IAsyncDisposable
{
    /// <summary>The PostgreSQL dependency name.</summary>
    public const string PostgresDependency = "postgres";

    /// <summary>The Service Bus dependency name.</summary>
    public const string ServiceBusDependency = "service-bus";

    /// <summary>Detail reported when a dependency has no configuration at all.</summary>
    public const string NotConfigured = "NotConfigured";

    private readonly DependencyProbeOptions _options;
    private readonly Lazy<ServiceBusClient> _client;
    private readonly Lazy<ServiceBusSender> _sender;
    private readonly Lazy<ServiceBusReceiver> _receiver;

    /// <summary>Creates a probe. Nothing connects until the first check.</summary>
    public DependencyProbe(DependencyProbeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _client = new Lazy<ServiceBusClient>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
        _sender = new Lazy<ServiceBusSender>(
            () => _client.Value.CreateSender(_options.ServiceBusQueueName),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _receiver = new Lazy<ServiceBusReceiver>(
            () => _client.Value.CreateReceiver(
                _options.ServiceBusQueueName,
                new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock }),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Checks every dependency. Never throws.</summary>
    public async Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var postgres = await CheckPostgresAsync(cancellationToken).ConfigureAwait(false);
        var serviceBus = await CheckServiceBusAsync(cancellationToken).ConfigureAwait(false);
        DependencyStatus[] dependencies = [postgres, serviceBus];

        return new ReadinessReport(dependencies.All(dependency => dependency.Healthy), dependencies);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_receiver.IsValueCreated)
        {
            await _receiver.Value.DisposeAsync().ConfigureAwait(false);
        }

        if (_sender.IsValueCreated)
        {
            await _sender.Value.DisposeAsync().ConfigureAwait(false);
        }

        if (_client.IsValueCreated)
        {
            await _client.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<DependencyStatus> CheckPostgresAsync(CancellationToken cancellationToken)
    {
        if (!_options.PostgresConfigured)
        {
            return new DependencyStatus(PostgresDependency, false, NotConfigured);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);

            var seconds = Math.Max(1, (int)Math.Ceiling(_options.Timeout.TotalSeconds));
            var builder = new NpgsqlConnectionStringBuilder(_options.PostgresConnectionString)
            {
                Timeout = seconds,
                CommandTimeout = seconds,
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(timeout.Token).ConfigureAwait(false);

            await using var command = new NpgsqlCommand("SELECT 1", connection);
            _ = await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);

            return new DependencyStatus(PostgresDependency, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new DependencyStatus(PostgresDependency, false, Describe(exception));
        }
    }

    private async Task<DependencyStatus> CheckServiceBusAsync(CancellationToken cancellationToken)
    {
        if (!_options.ServiceBusConfigured)
        {
            return new DependencyStatus(ServiceBusDependency, false, NotConfigured);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);

            if (_options.ServiceBusMode == ServiceBusReadinessMode.Receive)
            {
                _ = await _receiver.Value
                    .PeekMessagesAsync(1, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                // Creating a batch makes the SDK open the send link to learn its size limits. Nothing
                // is added to the batch and nothing is sent; disposing it releases the batch only.
                using var batch = await _sender.Value
                    .CreateMessageBatchAsync(timeout.Token)
                    .ConfigureAwait(false);
            }

            return new DependencyStatus(ServiceBusDependency, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new DependencyStatus(ServiceBusDependency, false, Describe(exception));
        }
    }

    private ServiceBusClient CreateClient()
    {
        // One attempt per check: readiness reports the state now; the probe interval is the retry.
        var clientOptions = new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions
            {
                MaxRetries = 0,
                TryTimeout = _options.Timeout,
            },
        };

        if (!string.IsNullOrWhiteSpace(_options.ServiceBusConnectionString))
        {
            return new ServiceBusClient(_options.ServiceBusConnectionString, clientOptions);
        }

        // The user-assigned identity is named explicitly, so the process never falls through a chain
        // of other credential sources in Azure. Outside Azure, with no client id, the developer's own
        // signed-in credential is used.
        TokenCredential credential = string.IsNullOrWhiteSpace(_options.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(_options.ManagedIdentityClientId));

        return new ServiceBusClient(_options.ServiceBusFullyQualifiedNamespace, credential, clientOptions);
    }

    private static string Describe(Exception exception) => exception switch
    {
        ServiceBusException serviceBus => $"ServiceBusException:{serviceBus.Reason}",
        PostgresException postgres => $"PostgresException:{postgres.SqlState}",
        NpgsqlException { InnerException: { } inner } => $"NpgsqlException:{inner.GetType().Name}",
        _ => exception.GetType().Name,
    };
}
