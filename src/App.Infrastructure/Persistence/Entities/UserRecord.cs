using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// The canonical identity record: a person's membership of one tenant, keyed on the canonical
/// <see cref="Core.Identity.UserId"/> and mapped to one external provider identity.
/// </summary>
/// <remarks>
/// <para>
/// A single table carries both the canonical user and the tenant membership. The MVP has no user
/// who belongs to two employers, so a separate membership table would model a relationship no Gate
/// needs; splitting it later is a migration, not a redesign.
/// </para>
/// <para>
/// There is no email column. Email is mutable, reassignable and provider-controlled, so it is never
/// an identity key. The lookup key is
/// (<see cref="TenantId"/>, <see cref="Provider"/>, <see cref="ProviderSubject"/>), enforced by a
/// unique index so one provider identity cannot map to two canonical users in a tenant.
/// </para>
/// <para>
/// Roles are not persisted here. Authorization, including role and resource permission, belongs to
/// the security Gate; storing roles now would be speculative.
/// </para>
/// </remarks>
public sealed class UserRecord : ITenantOwned
{
    /// <summary>The maximum stored length of a provider subject.</summary>
    public const int MaxProviderSubjectLength = 200;

    private UserRecord()
    {
        // Materialization only.
    }

    private UserRecord(
        UserId userId,
        TenantId tenantId,
        IdentityProviderId provider,
        string providerSubject,
        bool isActive,
        DateTimeOffset createdAtUtc)
    {
        UserId = userId;
        TenantId = tenantId;
        Provider = provider;
        ProviderSubject = providerSubject;
        IsActive = isActive;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Gets the canonical user identifier.</summary>
    public UserId UserId { get; private set; }

    /// <summary>Gets the owning tenant. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the external identity provider.</summary>
    public IdentityProviderId Provider { get; private set; }

    /// <summary>Gets the provider's stable subject/object identifier.</summary>
    public string ProviderSubject { get; private set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the membership is active. Revocation is enforced at access
    /// time, not only at ingestion time.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>Gets the UTC creation instant.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Creates a user record from an already-validated external identity.</summary>
    /// <exception cref="ArgumentException">A required component is missing or invalid.</exception>
    public static UserRecord Create(
        UserId userId,
        TenantId tenantId,
        ExternalIdentity externalIdentity,
        DateTimeOffset createdAtUtc,
        bool isActive = true)
    {
        ArgumentNullException.ThrowIfNull(externalIdentity);

        if (userId.IsEmpty)
        {
            throw new ArgumentException("A user record requires a canonical user id.", nameof(userId));
        }

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("A user record requires a tenant.", nameof(tenantId));
        }

        return new UserRecord(
            userId,
            tenantId,
            externalIdentity.Provider,
            externalIdentity.Subject,
            isActive,
            createdAtUtc.ToUniversalTime());
    }

    /// <summary>Projects the stored provider identity back onto the canonical value object.</summary>
    public ExternalIdentity ToExternalIdentity() => new(Provider, ProviderSubject);
}
