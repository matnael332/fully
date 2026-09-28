using WorkContinuity.Core.Results;
using WorkContinuity.Core.Transport;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Transport;

/// <summary>How the in-memory broker treats one send, to inject the failures the relay must survive.</summary>
internal enum SendFault
{
    /// <summary>Accept the message.</summary>
    None,

    /// <summary>Refuse the message, as a broker outage would.</summary>
    Refuse,

    /// <summary>Crash before the message reaches the broker: the crash switch trips and the send is cancelled.</summary>
    CrashBeforeSend,

    /// <summary>Accept the message, then crash: the crash switch trips before the relay can commit its mark.</summary>
    CrashAfterSend,
}

/// <summary>
/// The deterministic in-memory broker Gate 2 owner decision 8 approves for CI. It models what the transport
/// semantics depend on: one queue with sessions delivered in order, a duplicate-detection window on the
/// message id, peek-lock delivery with a delivery count, abandon, the broker's own maximum-delivery rule,
/// dead-lettering with a reason, and re-drive with the contract's rules. Time is virtual (<see cref="Now"/>).
/// </summary>
internal sealed class InMemoryTransport : IEventTransportPublisher, IDeadLetterQueue
{
    private readonly object _gate = new();
    private readonly List<StoredMessage> _queue = [];
    private readonly List<DeadLetter> _deadLetters = [];
    private readonly Dictionary<string, DateTimeOffset> _messageIds = new(StringComparer.Ordinal);
    private readonly List<TransportWireMessage> _sends = [];
    private readonly List<TransportWireMessage> _accepted = [];
    private readonly List<TimeSpan> _backoffs = [];
    private long _sequence;
    private int _duplicatesSuppressed;

    /// <summary>Gets or sets the virtual time the duplicate-detection window is measured in.</summary>
    public DateTimeOffset Now { get; set; } = IngressHost.Now;

    /// <summary>Gets or sets the duplicate-detection window. The local emulator's is five minutes.</summary>
    public TimeSpan DuplicateDetectionWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the broker's own delivery limit. The local emulator's is five.</summary>
    public int MaxDeliveryCount { get; set; } = 5;

    /// <summary>Gets or sets the fault to inject for each send.</summary>
    public Func<TransportEnvelope, SendFault> Fault { get; set; } = _ => SendFault.None;

    /// <summary>Gets or sets the switch a simulated crash trips; pass its token to the relay.</summary>
    public CancellationTokenSource CrashSwitch { get; set; } = new();

    /// <summary>Gets or sets work to run after the broker accepted a message and before the send returns.</summary>
    public Func<TransportEnvelope, Task>? AfterSend { get; set; }

    /// <summary>Gets every send that reached the broker, including duplicates it discarded.</summary>
    public IReadOnlyList<TransportWireMessage> Sends
    {
        get
        {
            lock (_gate)
            {
                return [.. _sends];
            }
        }
    }

    /// <summary>Gets every message the broker enqueued.</summary>
    public IReadOnlyList<TransportWireMessage> Accepted
    {
        get
        {
            lock (_gate)
            {
                return [.. _accepted];
            }
        }
    }

    /// <summary>Gets how many sends the duplicate-detection window discarded.</summary>
    public int DuplicatesSuppressed
    {
        get
        {
            lock (_gate)
            {
                return _duplicatesSuppressed;
            }
        }
    }

    /// <summary>Gets the backoff of every abandon, in order.</summary>
    public IReadOnlyList<TimeSpan> Backoffs
    {
        get
        {
            lock (_gate)
            {
                return [.. _backoffs];
            }
        }
    }

    /// <summary>Gets how many messages wait in the queue.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <inheritdoc />
    public async Task<Result> PublishAsync(TransportEnvelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (Fault(envelope))
        {
            case SendFault.Refuse:
                return Result.Failure(TransportErrors.PublishFailed);

            case SendFault.CrashBeforeSend:
                await CrashSwitch.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("A crash before the send must cancel the relay; pass the crash switch's token.");

            case SendFault.CrashAfterSend:
                Enqueue(envelope.ToWire());
                await CrashSwitch.CancelAsync();
                return Result.Success();

            default:
                Enqueue(envelope.ToWire());
                if (AfterSend is { } afterSend)
                {
                    await afterSend(envelope);
                }

                return Result.Success();
        }
    }

    /// <summary>Puts an arbitrary, possibly malformed, message on the queue, as a forger with send rights could.</summary>
    public void SendRaw(
        string? messageId,
        string? sessionId,
        string? correlationId,
        string? contentType,
        IReadOnlyDictionary<string, object> properties,
        byte[] body)
    {
        lock (_gate)
        {
            _queue.Add(new StoredMessage(
                messageId, sessionId, correlationId, contentType, new Dictionary<string, object>(properties, StringComparer.Ordinal), body, ++_sequence, Now));
        }
    }

    /// <summary>
    /// Delivers the head of the oldest available session to the consumer and applies its settlement exactly as
    /// the Service Bus adapter does. A consumer that throws counts as a lost lock: the delivery is counted and
    /// the message stays for redelivery. Returns null when nothing is available.
    /// </summary>
    public async Task<TransportDelivery?> DeliverNextAsync(IEventTransportConsumer consumer, CancellationToken cancellationToken = default)
    {
        StoredMessage? next;
        lock (_gate)
        {
            next = _queue
                .GroupBy(message => message.SessionId ?? string.Empty, StringComparer.Ordinal)
                .Select(session => session.OrderBy(message => message.Sequence).First())
                .Where(head => !head.Locked)
                .OrderBy(head => head.Sequence)
                .FirstOrDefault();
            if (next is null)
            {
                return null;
            }

            next.Locked = true;
            next.DeliveryCount++;
        }

        var received = next.AsReceived();
        TransportSettlement settlement;
        try
        {
            settlement = await consumer.ConsumeAsync(received, cancellationToken);
        }
        catch
        {
            lock (_gate)
            {
                next.Locked = false;
                ApplyDeliveryLimit(next);
            }

            throw;
        }

        lock (_gate)
        {
            next.Locked = false;
            switch (settlement.Kind)
            {
                case TransportSettlementKind.Complete:
                    _queue.Remove(next);
                    break;

                case TransportSettlementKind.Abandon:
                    _backoffs.Add(settlement.RetryAfter);
                    ApplyDeliveryLimit(next);
                    break;

                case TransportSettlementKind.DeadLetter:
                    _queue.Remove(next);
                    _deadLetters.Add(new DeadLetter(next, settlement.DeadLetterReason!.Code, settlement.DeadLetterReason.Message));
                    break;

                default:
                    throw new InvalidOperationException("The consumer returned no settlement.");
            }
        }

        return new TransportDelivery(received, settlement);
    }

    /// <summary>Delivers until nothing is available or the limit is reached.</summary>
    public async Task<IReadOnlyList<TransportDelivery>> DrainAsync(IEventTransportConsumer consumer, int maxDeliveries = 500)
    {
        var deliveries = new List<TransportDelivery>();
        while (deliveries.Count < maxDeliveries && await DeliverNextAsync(consumer) is { } delivery)
        {
            deliveries.Add(delivery);
        }

        return deliveries;
    }

    /// <summary>Gets the free-text descriptions the dead-letter queue holds, for asserting what they never contain.</summary>
    public IReadOnlyList<string> DeadLetterDescriptions()
    {
        lock (_gate)
        {
            return [.. _deadLetters.Select(dead => dead.Description)];
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetteredMessage>> PeekAsync(int maxMessages, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<DeadLetteredMessage> peeked = [.. _deadLetters
                .Take(maxMessages)
                .Select(dead => DeadLetteredMessage.From(dead.Message.AsReceived(), dead.Reason, dead.Message.EnqueuedAt))];
            return Task.FromResult(peeked);
        }
    }

    /// <inheritdoc />
    public Task<RedriveOutcome> RedriveAsync(Guid outboxMessageId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var dead in _deadLetters)
            {
                var read = TransportEnvelope.Read(dead.Message.AsReceived());
                if (read.IsFailure || read.Value.OutboxMessageId != outboxMessageId)
                {
                    continue;
                }

                var next = read.Value.ForRedrive();
                if (next.IsFailure)
                {
                    return Task.FromResult(new RedriveOutcome(RedriveStatus.LimitReached, MessageId: null));
                }

                EnqueueLocked(next.Value.ToWire());
                _deadLetters.Remove(dead);
                return Task.FromResult(new RedriveOutcome(RedriveStatus.Redriven, next.Value.MessageId));
            }

            return Task.FromResult(new RedriveOutcome(RedriveStatus.NotFound, MessageId: null));
        }
    }

    private void Enqueue(TransportWireMessage wire)
    {
        lock (_gate)
        {
            EnqueueLocked(wire);
        }
    }

    private void EnqueueLocked(TransportWireMessage wire)
    {
        _sends.Add(wire);
        if (_messageIds.TryGetValue(wire.MessageId, out var seen) && Now - seen < DuplicateDetectionWindow)
        {
            // As Service Bus does: the send succeeds and the duplicate is silently discarded.
            _duplicatesSuppressed++;
            return;
        }

        _messageIds[wire.MessageId] = Now;
        _accepted.Add(wire);
        _queue.Add(new StoredMessage(
            wire.MessageId,
            wire.SessionId,
            wire.CorrelationId,
            wire.ContentType,
            new Dictionary<string, object>(wire.ApplicationProperties, StringComparer.Ordinal),
            wire.Body.ToArray(),
            ++_sequence,
            Now));
    }

    private void ApplyDeliveryLimit(StoredMessage message)
    {
        if (message.DeliveryCount >= MaxDeliveryCount)
        {
            _queue.Remove(message);
            _deadLetters.Add(new DeadLetter(message, "MaxDeliveryCountExceeded", "Message could not be consumed after the maximum delivery attempts."));
        }
    }

    private sealed class StoredMessage(
        string? messageId,
        string? sessionId,
        string? correlationId,
        string? contentType,
        Dictionary<string, object> properties,
        byte[] body,
        long sequence,
        DateTimeOffset enqueuedAt)
    {
        public string? SessionId { get; } = sessionId;

        public long Sequence { get; } = sequence;

        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;

        public bool Locked { get; set; }

        public int DeliveryCount { get; set; }

        public ReceivedTransportMessage AsReceived() => new(
            messageId, SessionId, correlationId, contentType, properties, body, DeliveryCount, Sequence);
    }

    private sealed record DeadLetter(StoredMessage Message, string Reason, string Description);
}

/// <summary>One delivery and how the consumer settled it.</summary>
internal sealed record TransportDelivery(ReceivedTransportMessage Message, TransportSettlement Settlement);
