using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Ingress;

/// <summary>
/// One inbound delivery handed to the in-process ingress by a source adapter.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no tenant member. The tenant is resolved server-side from the connection,
/// never from the caller or the payload (invariants 1 and 45), so a delivery cannot name its own
/// tenant.
/// </para>
/// <para>
/// The payload and the proof are untrusted data (invariant 25). The proof is transport evidence handed
/// to the source's verifier; it is never persisted. Neither the payload nor the proof appears in the
/// type's string form, so logging a submission cannot leak them.
/// </para>
/// </remarks>
public sealed class IngressSubmission
{
    /// <summary>Initializes a new instance of the <see cref="IngressSubmission"/> class.</summary>
    /// <param name="connectionId">The registered connection the delivery arrived through.</param>
    /// <param name="sourceSystem">The source system the delivery claims to come from.</param>
    /// <param name="sourceEventId">The provider's event id, when the source supplies one.</param>
    /// <param name="schemaVersion">The source's payload schema version.</param>
    /// <param name="payload">The raw payload as received.</param>
    /// <param name="correlationId">The caller's correlation id; empty means the ingress generates one.</param>
    /// <param name="proof">Transport evidence for the source's verifier, if any.</param>
    /// <exception cref="ArgumentNullException">A required text component is null.</exception>
    public IngressSubmission(
        Guid connectionId,
        string sourceSystem,
        string? sourceEventId,
        string schemaVersion,
        string payload,
        CorrelationId correlationId,
        string? proof)
    {
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(schemaVersion);
        ArgumentNullException.ThrowIfNull(payload);

        ConnectionId = connectionId;
        SourceSystem = sourceSystem;
        SourceEventId = sourceEventId;
        SchemaVersion = schemaVersion;
        Payload = payload;
        CorrelationId = correlationId;
        Proof = proof;
    }

    /// <summary>Gets the registered connection the delivery arrived through.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets the source system the delivery claims to come from.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the provider's event id, when the source supplies one.</summary>
    public string? SourceEventId { get; }

    /// <summary>Gets the source's payload schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the raw payload as received. Untrusted data.</summary>
    public string Payload { get; }

    /// <summary>Gets the caller's correlation id, or an empty one.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>Gets the transport evidence for the source's verifier. Never persisted or logged.</summary>
    public string? Proof { get; }

    /// <summary>
    /// Checks everything that can be checked before any tenant is known. Nothing is persisted for a
    /// delivery that fails here.
    /// </summary>
    /// <returns>The first problem found, or null when the shape is acceptable.</returns>
    public Error? Validate()
    {
        if (ConnectionId == Guid.Empty)
        {
            return IngressErrors.InvalidConnectionId;
        }

        if (!EventText.IsToken(SourceSystem))
        {
            return IngressErrors.InvalidSourceSystem;
        }

        if (SourceEventId is not null && !EventText.IsIdentifier(SourceEventId))
        {
            return IngressErrors.InvalidSourceEventId;
        }

        if (!EventText.IsToken(SchemaVersion))
        {
            return IngressErrors.InvalidSchemaVersion;
        }

        if (string.IsNullOrWhiteSpace(Payload))
        {
            return IngressErrors.EmptyPayload;
        }

        return null;
    }
}
