using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Observability;

namespace WorkContinuity.Infrastructure.Transport;

/// <summary>
/// Reads the <c>event-processing</c> dead-letter queue and re-drives one message on explicit request.
/// </summary>
/// <remarks>
/// <para>
/// Reading peeks: nothing is locked or removed, and each message is reported as safe metadata only (ids,
/// tenant, type, reason code, counts, times), never its body or its free-text description.
/// </para>
/// <para>
/// A re-drive names one message by its outbox identity. The queue is scanned up to the configured limit;
/// every other message scanned is released untouched. The target's envelope is validated again, and a message
/// already re-driven the maximum number of times stays where it is. Otherwise a copy with the same tenant,
/// session, correlation, causation and identity and the next re-drive count is sent, under its own
/// deterministic message id so that duplicate detection cannot swallow it, and only then is the original
/// removed. If the removal fails, the original stays and a later re-drive sends the same message id again,
/// which the broker absorbs. Nothing calls this automatically.
/// </para>
/// </remarks>
internal sealed class ServiceBusDeadLetterQueue(
    ServiceBusClient client,
    EventTransportOptions options,
    EventBackboneMetrics metrics,
    ILogger<ServiceBusDeadLetterQueue> logger) : IDeadLetterQueue
{
    private const int ReceiveBatchSize = 50;

    private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetteredMessage>> PeekAsync(
        int maxMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);

        await using var receiver = client.CreateReceiver(
            TransportContract.EventProcessingQueue,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        var messages = new List<DeadLetteredMessage>();
        long? fromSequenceNumber = null;
        while (messages.Count < maxMessages)
        {
            var page = await receiver.PeekMessagesAsync(maxMessages - messages.Count, fromSequenceNumber, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            messages.AddRange(page.Select(message => DeadLetteredMessage.From(
                ServiceBusTransportMapping.ToReceived(message),
                message.DeadLetterReason,
                message.EnqueuedTime)));
            fromSequenceNumber = page[^1].SequenceNumber + 1;
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task<RedriveOutcome> RedriveAsync(Guid outboxMessageId, CancellationToken cancellationToken = default)
    {
        var outcome = await RedriveMessageAsync(outboxMessageId, cancellationToken);
        metrics.Redriven(outcome.Status);
        return outcome;
    }

    private async Task<RedriveOutcome> RedriveMessageAsync(Guid outboxMessageId, CancellationToken cancellationToken)
    {
        await using var receiver = client.CreateReceiver(
            TransportContract.EventProcessingQueue,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });

        var released = new List<ServiceBusReceivedMessage>();
        try
        {
            ServiceBusReceivedMessage? target = null;
            TransportEnvelope? envelope = null;
            var scanned = 0;
            while (target is null && scanned < options.MaxDeadLetterScan)
            {
                var batch = await receiver.ReceiveMessagesAsync(
                    Math.Min(ReceiveBatchSize, options.MaxDeadLetterScan - scanned),
                    ReceiveWait,
                    cancellationToken);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var message in batch)
                {
                    scanned++;
                    var read = TransportEnvelope.Read(ServiceBusTransportMapping.ToReceived(message));
                    if (target is null && read.IsSuccess && read.Value.OutboxMessageId == outboxMessageId)
                    {
                        target = message;
                        envelope = read.Value;
                    }
                    else
                    {
                        released.Add(message);
                    }
                }
            }

            if (target is null || envelope is null)
            {
                logger.LogWarning(
                    "Re-drive found no dead-lettered message {OutboxMessageId} among {Scanned} scanned.",
                    outboxMessageId,
                    scanned);
                return new RedriveOutcome(RedriveStatus.NotFound, MessageId: null);
            }

            var next = envelope.ForRedrive();
            if (next.IsFailure)
            {
                released.Add(target);
                logger.LogWarning(
                    "Re-drive refused message {OutboxMessageId} for tenant {TenantId}: {ReasonCode}; it stays dead-lettered (correlation {CorrelationId}).",
                    outboxMessageId,
                    envelope.TenantId,
                    next.Error.Code,
                    envelope.CorrelationId);
                return new RedriveOutcome(RedriveStatus.LimitReached, MessageId: null);
            }

            await using (var sender = client.CreateSender(next.Value.QueueName))
            {
                await sender.SendMessageAsync(
                    ServiceBusTransportMapping.ToServiceBusMessage(next.Value.ToWire()), cancellationToken);
            }

            await receiver.CompleteMessageAsync(target, cancellationToken);
            logger.LogInformation(
                "Re-drove dead-lettered message {OutboxMessageId} for tenant {TenantId} as {MessageId} (re-drive {RedriveCount}, correlation {CorrelationId}).",
                outboxMessageId,
                envelope.TenantId,
                next.Value.MessageId,
                next.Value.RedriveCount,
                envelope.CorrelationId);
            return new RedriveOutcome(RedriveStatus.Redriven, next.Value.MessageId);
        }
        finally
        {
            foreach (var message in released)
            {
                await ReleaseAsync(receiver, message);
            }
        }
    }

    private async Task ReleaseAsync(ServiceBusReceiver receiver, ServiceBusReceivedMessage message)
    {
        try
        {
            await receiver.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
        catch (ServiceBusException exception)
        {
            // The lock lapses on its own and the message stays dead-lettered either way.
            logger.LogDebug(
                "A scanned dead-lettered message could not be released early: {ServiceBusReason}.",
                exception.Reason);
        }
    }
}
