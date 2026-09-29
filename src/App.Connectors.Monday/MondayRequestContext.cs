using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The server-side context of one monday call: the tenant, the connection it goes through, and the correlation that
/// ties it to the work that caused it.
/// </summary>
/// <remarks>
/// The tenant and the connection come from trusted server-side state (the connection record), never from provider data
/// (invariants 1 and 45). The credential acquired for the call must belong to exactly this tenant and connection.
/// </remarks>
public sealed record MondayRequestContext
{
    /// <summary>Creates a context.</summary>
    /// <exception cref="ArgumentException">A component is missing.</exception>
    public MondayRequestContext(TenantId tenant, Guid connectionId, CorrelationId correlationId)
    {
        if (tenant.IsEmpty)
        {
            throw new ArgumentException("A monday call requires a tenant.", nameof(tenant));
        }

        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("A monday call requires a connection.", nameof(connectionId));
        }

        if (correlationId.IsEmpty)
        {
            throw new ArgumentException("A monday call requires a correlation id.", nameof(correlationId));
        }

        Tenant = tenant;
        ConnectionId = connectionId;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the tenant the call serves.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the connection the call goes through.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets the correlation of the work that caused the call.</summary>
    public CorrelationId CorrelationId { get; }
}
