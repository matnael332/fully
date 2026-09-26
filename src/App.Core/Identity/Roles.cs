using System.Collections.Immutable;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Identity;

/// <summary>
/// Coarse platform role. Roles are authorization input, evaluated together with resource-level
/// permission; holding a role is never by itself sufficient to access a resource.
/// </summary>
public enum PlatformRole
{
    /// <summary>No role.</summary>
    None = 0,

    /// <summary>An employee acting on their own work context.</summary>
    Employee = 1,

    /// <summary>A manager. Visibility is aggregate by default, not individual surveillance.</summary>
    Manager = 2,

    /// <summary>
    /// A tenant administrator managing users, policies, connectors and devices. This does not
    /// confer unrestricted access to employee content.
    /// </summary>
    TenantAdmin = 3,

    /// <summary>A security/audit reader scoped to audit and security evidence.</summary>
    SecurityAuditor = 4,

    /// <summary>
    /// Internal support. Metadata-only by default; raw access requires scoped, time-bound,
    /// audited elevation.
    /// </summary>
    InternalSupport = 5,

    /// <summary>A non-human service or workload identity.</summary>
    ServiceIdentity = 6,
}

/// <summary>
/// A user's membership of a tenant, together with the roles that membership grants.
/// </summary>
/// <remarks>
/// Membership is the authorization side of the model and is deliberately separate from
/// <see cref="Principal"/>, which only carries the authentication outcome. Being authenticated
/// says nothing about which tenant a user may act in, or in what capacity.
/// </remarks>
public sealed record TenantMembership
{
    /// <summary>Creates a tenant membership.</summary>
    /// <exception cref="ArgumentException">The tenant, user or role set is invalid.</exception>
    public TenantMembership(
        TenantId tenantId,
        UserId userId,
        IEnumerable<PlatformRole> roles,
        bool isActive = true)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("A membership requires a tenant.", nameof(tenantId));
        }

        if (userId.IsEmpty)
        {
            throw new ArgumentException("A membership requires a user.", nameof(userId));
        }

        var distinctRoles = roles.Where(role => role != PlatformRole.None).ToImmutableSortedSet();

        if (distinctRoles.Count == 0)
        {
            throw new ArgumentException("A membership requires at least one role.", nameof(roles));
        }

        TenantId = tenantId;
        UserId = userId;
        Roles = distinctRoles;
        IsActive = isActive;
    }

    /// <summary>Gets the tenant this membership belongs to.</summary>
    public TenantId TenantId { get; }

    /// <summary>Gets the user this membership belongs to.</summary>
    public UserId UserId { get; }

    /// <summary>Gets the immutable set of roles granted by this membership.</summary>
    public ImmutableSortedSet<PlatformRole> Roles { get; }

    /// <summary>
    /// Gets a value indicating whether the membership is currently active. Revocation is enforced
    /// at access time, not only at ingestion time.
    /// </summary>
    public bool IsActive { get; }

    /// <summary>Determines whether this membership grants the specified role.</summary>
    public bool HasRole(PlatformRole role) => Roles.Contains(role);
}
