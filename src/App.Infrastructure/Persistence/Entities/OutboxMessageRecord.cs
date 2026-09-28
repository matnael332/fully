using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// A message queued for publication, written in the same database transaction as the state change
/// that produced it.
/// </summary>
/// <remarks>
/// <para>
/// The outbox exists so that a state change and its notification cannot diverge: either both are
/// committed or neither is. The database remains operational truth; the broker is durable transport.
/// </para>
/// <para>
/// Gate 1D provides the storage guarantee. Gate 2C's outbox relay publishes these rows: it claims a
/// batch with a row lock held for the whole transaction, sends each message, and calls
/// <see cref="MarkPublished"/> only after the broker accepted it, committing the marks with the lock.
/// The lock is the claim, so no lease, attempt or retry column is modelled: a crash or rollback
/// releases the lock and leaves every unmarked row pending.
/// </para>
/// </remarks>
public sealed class OutboxMessageRecord : ITenantOwned
{
    /// <summary>The maximum stored length of a message type.</summary>
    public const int MaxMessageTypeLength = 200;

    private OutboxMessageRecord()
    {
        // Materialization only.
    }

    private OutboxMessageRecord(
        Guid outboxMessageId,
        TenantId tenantId,
        string messageType,
        string payload,
        DateTimeOffset occurredAtUtc,
        CorrelationId correlationId)
    {
        OutboxMessageId = outboxMessageId;
        TenantId = tenantId;
        MessageType = messageType;
        Payload = payload;
        OccurredAtUtc = occurredAtUtc;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the message identifier.</summary>
    public Guid OutboxMessageId { get; private set; }

    /// <summary>Gets the owning tenant. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the message type discriminator.</summary>
    public string MessageType { get; private set; } = string.Empty;

    /// <summary>Gets the serialized envelope.</summary>
    public string Payload { get; private set; } = string.Empty;

    /// <summary>Gets the UTC instant the originating change occurred.</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Gets the correlation id tying this message to its wider operation.</summary>
    public CorrelationId CorrelationId { get; private set; }

    /// <summary>Gets the UTC instant the message was published, or null while pending.</summary>
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    /// <summary>Queues a message for later publication.</summary>
    /// <exception cref="ArgumentException">A required component is missing or invalid.</exception>
    public static OutboxMessageRecord Queue(
        Guid outboxMessageId,
        TenantId tenantId,
        string messageType,
        string payload,
        DateTimeOffset occurredAtUtc,
        CorrelationId correlationId)
    {
        if (outboxMessageId == Guid.Empty)
        {
            throw new ArgumentException("An outbox message requires an id.", nameof(outboxMessageId));
        }

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("An outbox message requires a tenant.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(messageType))
        {
            throw new ArgumentException("An outbox message requires a type.", nameof(messageType));
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("An outbox message requires a payload.", nameof(payload));
        }

        if (correlationId.IsEmpty)
        {
            throw new ArgumentException(
                "An outbox message requires a correlation id.",
                nameof(correlationId));
        }

        return new OutboxMessageRecord(
            outboxMessageId,
            tenantId,
            messageType,
            payload,
            occurredAtUtc.ToUniversalTime(),
            correlationId);
    }

    /// <summary>Records that the broker accepted the message. Call only after a successful send.</summary>
    /// <exception cref="InvalidOperationException">The message is already marked published.</exception>
    public void MarkPublished(DateTimeOffset publishedAtUtc)
    {
        if (PublishedAtUtc is not null)
        {
            throw new InvalidOperationException("The outbox message is already marked published.");
        }

        PublishedAtUtc = publishedAtUtc.ToUniversalTime();
    }
}
