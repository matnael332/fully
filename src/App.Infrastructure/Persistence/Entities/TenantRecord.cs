using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>The lifecycle state of a tenant.</summary>
public enum TenantStatus
{
    /// <summary>Unspecified. Never persisted.</summary>
    None = 0,

    /// <summary>The tenant is active.</summary>
    Active = 1,

    /// <summary>The tenant is suspended and must not be served.</summary>
    Suspended = 2,
}

/// <summary>
/// The canonical tenant record.
/// </summary>
/// <remarks>
/// Deliberately minimal: identity, lifecycle state and creation time. Billing, company profile and
/// contract data are not modelled because no Gate needs them yet.
/// </remarks>
public sealed class TenantRecord : ITenantOwned
{
    private TenantRecord()
    {
        // Materialization only.
    }

    private TenantRecord(TenantId tenantId, TenantStatus status, DateTimeOffset createdAtUtc)
    {
        TenantId = tenantId;
        Status = status;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Gets the tenant identifier. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the lifecycle state.</summary>
    public TenantStatus Status { get; private set; }

    /// <summary>Gets the UTC creation instant, supplied by the caller's clock.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Creates a tenant record.</summary>
    /// <exception cref="ArgumentException">The tenant id or status is invalid.</exception>
    public static TenantRecord Create(TenantId tenantId, TenantStatus status, DateTimeOffset createdAtUtc)
    {
        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("A tenant record requires a tenant id.", nameof(tenantId));
        }

        if (status == TenantStatus.None)
        {
            throw new ArgumentException("A tenant record requires a status.", nameof(status));
        }

        return new TenantRecord(tenantId, status, createdAtUtc.ToUniversalTime());
    }
}
