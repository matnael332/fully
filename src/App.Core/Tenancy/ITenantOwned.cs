namespace WorkContinuity.Core.Tenancy;

/// <summary>
/// Marks a persisted record as belonging to exactly one tenant.
/// </summary>
/// <remarks>
/// <para>
/// This is the single tenant-ownership convention for persistence. Every tenant-owned record
/// carries its <see cref="TenantId"/> explicitly rather than inferring ownership from a join path,
/// so storage-level isolation can be expressed uniformly against one column.
/// </para>
/// <para>
/// The interface exposes a getter only: a record's tenant is fixed when it is created and is never
/// reassigned. The interface is persistence-agnostic and deliberately declares no storage concern,
/// so Core stays free of any database dependency.
/// </para>
/// </remarks>
public interface ITenantOwned
{
    /// <summary>Gets the tenant that owns this record.</summary>
    TenantId TenantId { get; }
}
