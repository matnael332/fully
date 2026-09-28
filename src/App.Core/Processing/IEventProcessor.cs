using System.Security.Cryptography;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Transport;

namespace WorkContinuity.Core.Processing;

/// <summary>
/// The processing pipeline between the transport and projection (architecture §3: normalization, schema
/// validation, deduplication, identity, authority, ordering and conflict check, canonical event).
/// </summary>
/// <remarks>
/// It runs in the tenant scope the transport consumer restored from the validated envelope. A success is a
/// settled, durable outcome, including a quarantine or a duplicate. A failure is either permanent
/// (<see cref="ProcessingErrors.ReceiptNotVisible"/>, <see cref="ProcessingErrors.TenantMismatch"/>: the message
/// cannot be processed as delivered) or transient (<see cref="ProcessingErrors.Unavailable"/>: nothing was
/// committed and the message is retried).
/// </remarks>
public interface IEventProcessor
{
    /// <summary>Processes the receipt a validated transport envelope names.</summary>
    Task<Result<EventProcessingResult>> ProcessAsync(TransportEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>Deterministic identities of processing.</summary>
public static class EventIdentity
{
    private static readonly byte[] Namespace = "workcontinuity.canonical-event.v1"u8.ToArray();

    /// <summary>
    /// The canonical event id of the event normalised from a receipt. It is derived, not generated, so every
    /// reprocessing of the receipt names the same event.
    /// </summary>
    /// <remarks>
    /// A SHA-256 name-based identifier over a fixed namespace and the receipt id, laid out as an RFC 9562
    /// version 8 UUID.
    /// </remarks>
    /// <exception cref="ArgumentException">The receipt id is empty.</exception>
    public static CanonicalEventId ForReceipt(Guid receiptId)
    {
        if (receiptId == Guid.Empty)
        {
            throw new ArgumentException("A receipt id is required.", nameof(receiptId));
        }

        Span<byte> input = stackalloc byte[Namespace.Length + 16];
        Namespace.CopyTo(input);
        receiptId.TryWriteBytes(input[Namespace.Length..], bigEndian: true, out _);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return CanonicalEventId.From(new Guid(bytes, bigEndian: true));
    }
}
