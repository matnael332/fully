using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Health;

namespace WorkContinuity.Infrastructure.Transport;

/// <summary>
/// Where the transport's Service Bus client connects, read from the same keys readiness uses: the
/// namespace and the workload's user-assigned identity in Azure, the emulator's connection string locally.
/// </summary>
internal sealed record ServiceBusTransportSettings(
    string ConnectionString,
    string FullyQualifiedNamespace,
    string ManagedIdentityClientId)
{
    /// <summary>Reads the settings; nothing is compiled in.</summary>
    /// <exception cref="InvalidOperationException">Neither a namespace nor a connection string is configured.</exception>
    public static ServiceBusTransportSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceBus = configuration.GetSection(DependencyProbeOptions.ServiceBusSection);
        var settings = new ServiceBusTransportSettings(
            serviceBus["ConnectionString"] ?? string.Empty,
            serviceBus["FullyQualifiedNamespace"] ?? string.Empty,
            configuration[DependencyProbeOptions.ManagedIdentityClientIdVariable] ?? string.Empty);

        if (string.IsNullOrWhiteSpace(settings.ConnectionString)
            && string.IsNullOrWhiteSpace(settings.FullyQualifiedNamespace))
        {
            throw new InvalidOperationException(
                "Service Bus is not configured. Set ServiceBus__FullyQualifiedNamespace, or ServiceBus__ConnectionString for the local emulator.");
        }

        return settings;
    }

    /// <summary>Creates the one client the transport shares. Sends retry briefly, then report failure.</summary>
    public ServiceBusClient CreateClient()
    {
        var clientOptions = new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions
            {
                Mode = ServiceBusRetryMode.Exponential,
                MaxRetries = 3,
                Delay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(10),
                TryTimeout = TimeSpan.FromSeconds(10),
            },
        };

        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            return new ServiceBusClient(ConnectionString, clientOptions);
        }

        // The user-assigned identity is named explicitly, so the process never falls through a chain of other
        // credential sources in Azure. Outside Azure, with no client id, the developer's own credential is used.
        TokenCredential credential = string.IsNullOrWhiteSpace(ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(ManagedIdentityClientId));

        return new ServiceBusClient(FullyQualifiedNamespace, credential, clientOptions);
    }

    /// <summary>Never prints the connection string.</summary>
    public override string ToString() => nameof(ServiceBusTransportSettings);
}

/// <summary>Maps the transport's own message shapes to and from the Service Bus SDK's, and nothing else.</summary>
internal static class ServiceBusTransportMapping
{
    /// <summary>The Service Bus message for a wire message: every value copied, nothing added.</summary>
    public static ServiceBusMessage ToServiceBusMessage(TransportWireMessage wire)
    {
        ArgumentNullException.ThrowIfNull(wire);

        var message = new ServiceBusMessage(wire.Body)
        {
            MessageId = wire.MessageId,
            SessionId = wire.SessionId,
            CorrelationId = wire.CorrelationId,
            ContentType = wire.ContentType,
        };

        foreach (var property in wire.ApplicationProperties)
        {
            message.ApplicationProperties.Add(property.Key, property.Value);
        }

        return message;
    }

    /// <summary>A received Service Bus message as the untrusted transport shape the consumer validates.</summary>
    public static ReceivedTransportMessage ToReceived(ServiceBusReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new ReceivedTransportMessage(
            message.MessageId,
            message.SessionId,
            message.CorrelationId,
            message.ContentType,
            message.ApplicationProperties,
            message.Body.ToMemory(),
            message.DeliveryCount,
            message.SequenceNumber);
    }
}

/// <summary>Publishes validated envelopes to their Service Bus queue.</summary>
/// <remarks>
/// A send succeeds only when Service Bus accepted the message durably. A refusal is reported as
/// <see cref="TransportErrors.PublishFailed"/> and logged by reason code or exception type, never by SDK
/// message. Cancellation is not a refusal: it propagates, and the outcome of an interrupted send is then
/// unknown, which republishing the same message id absorbs.
/// </remarks>
internal sealed class ServiceBusEventPublisher(
    ServiceBusClient client,
    ILogger<ServiceBusEventPublisher> logger) : IEventTransportPublisher, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<Result> PublishAsync(TransportEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var wire = envelope.ToWire();
        try
        {
            var sender = _senders.GetOrAdd(wire.QueueName, client.CreateSender);
            await sender.SendMessageAsync(ServiceBusTransportMapping.ToServiceBusMessage(wire), cancellationToken);
            return Result.Success();
        }
        catch (ServiceBusException exception)
        {
            logger.LogWarning(
                "Service Bus did not accept message {MessageId}: {ServiceBusReason}.",
                wire.MessageId,
                exception.Reason);
            return Result.Failure(TransportErrors.PublishFailed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Sending message {MessageId} failed with {ExceptionType}.",
                wire.MessageId,
                exception.GetType().Name);
            return Result.Failure(TransportErrors.PublishFailed);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync();
        }
    }
}
