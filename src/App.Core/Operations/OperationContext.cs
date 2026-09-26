using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Core.Operations;

/// <summary>
/// The validated ambient facts for one logical operation.
/// </summary>
/// <remarks>
/// Deliberately limited to four primitives so it stays a context object rather than a service
/// locator. It exposes no repositories, no clients and no behaviour.
/// </remarks>
public interface IOperationContext
{
    /// <summary>Gets the tenant security context, which may be unresolved.</summary>
    ITenantContext Tenant { get; }

    /// <summary>Gets the authenticated principal, which may be anonymous.</summary>
    Principal Principal { get; }

    /// <summary>Gets the correlation id for this operation.</summary>
    CorrelationId CorrelationId { get; }

    /// <summary>Gets the UTC instant the operation started, taken from <see cref="IClock"/>.</summary>
    DateTimeOffset StartedAtUtc { get; }
}

/// <summary>
/// Immutable <see cref="IOperationContext"/> implementation.
/// </summary>
public sealed class OperationContext : IOperationContext
{
    private OperationContext(
        ITenantContext tenant,
        Principal principal,
        CorrelationId correlationId,
        DateTimeOffset startedAtUtc)
    {
        Tenant = tenant;
        Principal = principal;
        CorrelationId = correlationId;
        StartedAtUtc = startedAtUtc;
    }

    /// <inheritdoc />
    public ITenantContext Tenant { get; }

    /// <inheritdoc />
    public Principal Principal { get; }

    /// <inheritdoc />
    public CorrelationId CorrelationId { get; }

    /// <inheritdoc />
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Starts an operation context from already-validated primitives.</summary>
    /// <exception cref="ArgumentException">The correlation context carries no correlation id.</exception>
    public static OperationContext Start(
        ITenantContext tenant,
        Principal principal,
        ICorrelationContext correlation,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(clock);

        if (correlation.CorrelationId.IsEmpty)
        {
            throw new ArgumentException(
                "An operation context requires a correlation id.",
                nameof(correlation));
        }

        return new OperationContext(tenant, principal, correlation.CorrelationId, clock.UtcNow);
    }
}
