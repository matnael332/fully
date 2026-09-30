namespace WorkContinuity.Core.Ingress;

/// <summary>
/// Answers, before any tenant is known, whether a connection id names an active connection of one source system.
/// </summary>
/// <remarks>
/// It serves a source's address-verification handshake, such as a webhook challenge, which stores nothing and so never
/// goes through <see cref="IEventIngress"/>. It uses the same approved connection resolution as ingress and reveals
/// nothing beyond the status: not the tenant, not whether a connection of another source exists, and no other data of
/// the connection (Gate 2 owner decision 1; Gate 3C).
/// </remarks>
public interface IIngressConnectionCheck
{
    /// <summary>Checks one connection id against one source system.</summary>
    /// <param name="connectionId">The connection id the request names.</param>
    /// <param name="sourceSystem">The source system the request claims.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The connection's status for that source.</returns>
    Task<IngressConnectionStatus> CheckAsync(Guid connectionId, string sourceSystem, CancellationToken cancellationToken);
}

/// <summary>What checking a connection id found.</summary>
public enum IngressConnectionStatus
{
    /// <summary>Unspecified. Never returned.</summary>
    Unspecified = 0,

    /// <summary>The id names an active connection of the source system.</summary>
    Active = 1,

    /// <summary>
    /// No active connection of the source system has this id: none exists, it is not active, or it belongs to another
    /// source system. The three are deliberately indistinguishable.
    /// </summary>
    Unknown = 2,

    /// <summary>The check could not be completed; the caller should answer so that the source retries.</summary>
    Unavailable = 3,
}
