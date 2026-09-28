using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Transport;

namespace WorkContinuity.Infrastructure.Transport;

/// <summary>
/// Receives from the <c>event-processing</c> queue and applies the transport consumer's settlement to each
/// message.
/// </summary>
/// <remarks>
/// <para>
/// A session processor in peek-lock mode that takes one message at a time in each session, so a tenant's
/// messages are handled in order, and never completes a message on its own. Every message goes through
/// <see cref="IEventTransportConsumer"/>; the returned settlement is applied exactly: complete, wait and
/// abandon, or dead-letter with the stable reason and the fixed description.
/// </para>
/// <para>
/// The host refuses to start without a downstream <see cref="ITransportMessageHandler"/>: a consumer with
/// nothing to hand messages to must not run, so messages wait in the queue instead. If a settlement cannot
/// be applied, or the process stops, the lock lapses and Service Bus redelivers the message.
/// </para>
/// </remarks>
public sealed class ServiceBusEventConsumerHost : IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly IEventTransportConsumer _consumer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EventTransportOptions _options;
    private readonly ILogger<ServiceBusEventConsumerHost> _logger;
    private ServiceBusSessionProcessor? _processor;

    internal ServiceBusEventConsumerHost(
        ServiceBusClient client,
        IEventTransportConsumer consumer,
        IServiceScopeFactory scopeFactory,
        EventTransportOptions options,
        ILogger<ServiceBusEventConsumerHost> logger)
    {
        _client = client;
        _consumer = consumer;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>Starts receiving.</summary>
    /// <exception cref="InvalidOperationException">No downstream handler is registered, or the host already runs.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processor is not null)
        {
            throw new InvalidOperationException("The consumer host is already running.");
        }

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            if (scope.ServiceProvider.GetService<ITransportMessageHandler>() is null)
            {
                throw new InvalidOperationException(
                    "No downstream transport handler is registered; the consumer will not take messages it cannot hand on.");
            }
        }

        var processorOptions = new ServiceBusSessionProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentSessions = _options.MaxConcurrentSessions,
            MaxConcurrentCallsPerSession = 1,
            PrefetchCount = 0,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            SessionIdleTimeout = _options.SessionIdleTimeout,
            MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
        };
        foreach (var sessionId in _options.ConsumerSessionIds)
        {
            processorOptions.SessionIds.Add(sessionId);
        }

        var processor = _client.CreateSessionProcessor(TransportContract.EventProcessingQueue, processorOptions);
        processor.ProcessMessageAsync += OnMessageAsync;
        processor.ProcessErrorAsync += OnErrorAsync;
        _processor = processor;

        await processor.StartProcessingAsync(cancellationToken);
    }

    /// <summary>Stops receiving. Unsettled messages are redelivered.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_processor is { } processor)
        {
            await processor.StopProcessingAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_processor is { } processor)
        {
            _processor = null;
            await processor.DisposeAsync();
        }
    }

    private async Task OnMessageAsync(ProcessSessionMessageEventArgs args)
    {
        TransportSettlement settlement;
        try
        {
            settlement = await _consumer.ConsumeAsync(
                ServiceBusTransportMapping.ToReceived(args.Message), args.CancellationToken);
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested)
        {
            // Stopping: the message stays locked until the session closes, then it is redelivered.
            return;
        }

        switch (settlement.Kind)
        {
            case TransportSettlementKind.Complete:
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                break;

            case TransportSettlementKind.Abandon:
                try
                {
                    // The session stays with this receiver while it waits, so the tenant's later messages
                    // keep their order behind this one.
                    await Task.Delay(settlement.RetryAfter, args.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
                break;

            case TransportSettlementKind.DeadLetter:
                await args.DeadLetterMessageAsync(
                    args.Message,
                    settlement.DeadLetterReason!.Code,
                    settlement.DeadLetterReason.Message,
                    args.CancellationToken);
                break;

            default:
                throw new InvalidOperationException("The consumer returned no settlement.");
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        var reason = args.Exception is ServiceBusException serviceBus ? serviceBus.Reason.ToString() : null;
        if (reason is nameof(ServiceBusFailureReason.ServiceTimeout) or nameof(ServiceBusFailureReason.SessionCannotBeLocked))
        {
            // Normal while sessions are idle or taken by another receiver.
            _logger.LogDebug(
                "Service Bus consumer idle signal from {ErrorSource}: {ServiceBusReason}.",
                args.ErrorSource,
                reason);
        }
        else
        {
            _logger.LogWarning(
                "Service Bus consumer error from {ErrorSource}: {ServiceBusReason} ({ExceptionType}).",
                args.ErrorSource,
                reason ?? "none",
                args.Exception.GetType().Name);
        }

        return Task.CompletedTask;
    }
}
