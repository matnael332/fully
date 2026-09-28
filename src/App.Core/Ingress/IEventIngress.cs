namespace WorkContinuity.Core.Ingress;

/// <summary>
/// The in-process ingress: the only way a source delivery becomes a durable receipt (Gate 2 owner
/// decision 2).
/// </summary>
/// <remarks>
/// A caller acknowledges its source only when <see cref="IngressOutcome.IsAcknowledged"/> is true,
/// which means the receipt and its outbox marker are committed. A rejected delivery is refused
/// permanently; a failed delivery was not stored and must be retried by the source. No delivery ends
/// without one of these outcomes, so none is silently lost.
/// An instance handles one delivery at a time: resolve it from a scope per delivery, as a request does.
/// </remarks>
public interface IEventIngress
{
    /// <summary>Verifies, attributes and durably stores one delivery.</summary>
    /// <param name="submission">The delivery.</param>
    /// <param name="cancellationToken">Cancels the operation before it commits.</param>
    /// <returns>The settled outcome.</returns>
    Task<IngressOutcome> SubmitAsync(IngressSubmission submission, CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies that a delivery really comes from its source system, before any tenant is resolved.
/// </summary>
/// <remarks>
/// One verifier serves one source system. A source system without a registered verifier is never
/// accepted, so verification fails closed. Source-specific verifiers (for example monday's webhook
/// signature) belong to their connector Gate; Gate 2 registers none in production.
/// A verifier must prove the delivery authentic for the connection it names, not merely for the source
/// system: the connection decides the tenant, so a proof that any other connection of the same source
/// could also present would let one tenant's deliveries be attributed to another.
/// </remarks>
public interface ISourceVerifier
{
    /// <summary>Gets the source system token this verifier serves.</summary>
    string SourceSystem { get; }

    /// <summary>Decides whether the delivery is authentic.</summary>
    /// <param name="submission">The delivery, including its transport proof.</param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <returns>True only when the delivery is authentic.</returns>
    ValueTask<bool> VerifyAsync(IngressSubmission submission, CancellationToken cancellationToken);
}

/// <summary>The outbox message types the ingress writes.</summary>
public static class IngressMessageTypes
{
    /// <summary>
    /// A durable receipt awaits processing. The marker carries the receipt id only: no payload content,
    /// so it holds nothing that must expire with raw work data.
    /// </summary>
    public const string ReceiptReceived = "workcontinuity.receipt.received";
}
