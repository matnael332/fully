using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>The processing state of a durable inbound receipt.</summary>
public enum RawReceiptState
{
    /// <summary>Unspecified. Never persisted.</summary>
    None = 0,

    /// <summary>Durably received and acknowledged to the source. Not yet processed.</summary>
    Received = 1,

    /// <summary>Processed into a canonical event.</summary>
    Processed = 2,

    /// <summary>
    /// Rejected before it could affect any snapshot: bad schema, unknown tenant, unknown schema
    /// version, impossible ordering, authority conflict or invalid authentication.
    /// </summary>
    Quarantined = 3,
}

/// <summary>
/// A durable record of one inbound payload, written and committed before the source is
/// acknowledged.
/// </summary>
/// <remarks>
/// <para>
/// This is the inbox/raw-receipt foundation only. Normalisation, deduplication decisions, ordering,
/// authority resolution and snapshot projection are the event backbone's work in a later Gate.
/// </para>
/// <para>
/// Retention: this table holds raw work payloads, which are subject to a maximum rolling 30-day
/// lifetime. <see cref="ReceivedAtUtc"/> is the age basis a scheduled purge will use, and it is
/// indexed so that purge can run efficiently. That lifecycle is deliberately separate from security
/// audit retention; the two must never be conflated or served from the same table.
/// </para>
/// <para>
/// <see cref="PayloadFingerprint"/> exists to <em>flag duplicate candidates</em> when a source
/// provides no event id. It is never the authoritative duplicate key: two genuinely distinct events
/// can be byte-identical. The authoritative key is
/// (<see cref="TenantId"/>, <see cref="ConnectionReference"/>, <see cref="SourceEventId"/>).
/// </para>
/// </remarks>
public sealed class RawReceiptRecord : ITenantOwned
{
    /// <summary>The maximum stored length of the short descriptive columns.</summary>
    public const int MaxReferenceLength = 200;

    private RawReceiptRecord()
    {
        // Materialization only.
    }

    private RawReceiptRecord(
        Guid receiptId,
        TenantId tenantId,
        string connectionReference,
        string sourceSystem,
        string? sourceEventId,
        string schemaVersion,
        DateTimeOffset receivedAtUtc,
        string payload,
        string payloadFingerprint,
        RawReceiptState processingState)
    {
        ReceiptId = receiptId;
        TenantId = tenantId;
        ConnectionReference = connectionReference;
        SourceSystem = sourceSystem;
        SourceEventId = sourceEventId;
        SchemaVersion = schemaVersion;
        ReceivedAtUtc = receivedAtUtc;
        Payload = payload;
        PayloadFingerprint = payloadFingerprint;
        ProcessingState = processingState;
    }

    /// <summary>Gets the receipt identifier.</summary>
    public Guid ReceiptId { get; private set; }

    /// <summary>Gets the owning tenant. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the connector/connection this payload arrived through.</summary>
    public string ConnectionReference { get; private set; } = string.Empty;

    /// <summary>Gets the source system, for example "monday".</summary>
    public string SourceSystem { get; private set; } = string.Empty;

    /// <summary>Gets the provider's event id when the source supplies one.</summary>
    public string? SourceEventId { get; private set; }

    /// <summary>Gets the source schema version, so an unknown version can be quarantined.</summary>
    public string SchemaVersion { get; private set; } = string.Empty;

    /// <summary>Gets the UTC instant the payload was durably received. The retention age basis.</summary>
    public DateTimeOffset ReceivedAtUtc { get; private set; }

    /// <summary>Gets the raw payload as received.</summary>
    public string Payload { get; private set; } = string.Empty;

    /// <summary>Gets a fingerprint of the payload, used only to flag duplicate candidates.</summary>
    public string PayloadFingerprint { get; private set; } = string.Empty;

    /// <summary>Gets the processing state.</summary>
    public RawReceiptState ProcessingState { get; private set; }

    /// <summary>Creates a receipt in the <see cref="RawReceiptState.Received"/> state.</summary>
    /// <exception cref="ArgumentException">A required component is missing or invalid.</exception>
    public static RawReceiptRecord Receive(
        Guid receiptId,
        TenantId tenantId,
        string connectionReference,
        string sourceSystem,
        string? sourceEventId,
        string schemaVersion,
        DateTimeOffset receivedAtUtc,
        string payload,
        string payloadFingerprint)
    {
        if (receiptId == Guid.Empty)
        {
            throw new ArgumentException("A receipt requires an id.", nameof(receiptId));
        }

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("A receipt requires a tenant.", nameof(tenantId));
        }

        Require(connectionReference, nameof(connectionReference));
        Require(sourceSystem, nameof(sourceSystem));
        Require(schemaVersion, nameof(schemaVersion));
        Require(payload, nameof(payload));
        Require(payloadFingerprint, nameof(payloadFingerprint));

        if (sourceEventId is not null && string.IsNullOrWhiteSpace(sourceEventId))
        {
            throw new ArgumentException(
                "A source event id must be either absent or meaningful, never blank.",
                nameof(sourceEventId));
        }

        return new RawReceiptRecord(
            receiptId,
            tenantId,
            connectionReference,
            sourceSystem,
            sourceEventId,
            schemaVersion,
            receivedAtUtc.ToUniversalTime(),
            payload,
            payloadFingerprint,
            RawReceiptState.Received);
    }

    /// <summary>Records that processing settled the receipt with an outcome other than quarantine.</summary>
    /// <exception cref="InvalidOperationException">The receipt was already processed.</exception>
    public void MarkProcessed() => Settle(RawReceiptState.Processed);

    /// <summary>Records that processing quarantined the receipt.</summary>
    /// <exception cref="InvalidOperationException">The receipt was already processed.</exception>
    public void MarkQuarantined() => Settle(RawReceiptState.Quarantined);

    private void Settle(RawReceiptState state)
    {
        if (ProcessingState != RawReceiptState.Received)
        {
            throw new InvalidOperationException("A receipt is settled by processing exactly once.");
        }

        ProcessingState = state;
    }

    private static void Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"'{parameterName}' is required.", parameterName);
        }
    }
}
