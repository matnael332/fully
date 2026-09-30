using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// A connection's delegated authorization (Gate 3B): the delegation chain, the lifecycle state and the metadata of the
/// current credential. One row per authorized connection.
/// </summary>
/// <remarks>
/// <para>
/// The chain is tenant, canonical user, connection and the provider's identity of the delegating user (decision
/// D3B-1). Foreign keys that include the tenant tie the row to its own tenant's connection and user, even though
/// PostgreSQL checks foreign keys without row-level security.
/// </para>
/// <para>
/// No column can hold a credential (decision D3B-2, invariant 53): the credential lives in the connector's secret
/// storage under <see cref="MaterialVersion"/>, an opaque identifier. Rows are written only by
/// <c>ConnectorAuthorizationStore</c>, through compare-and-swap statements stamped with the database clock, so this
/// type has no factory.
/// </para>
/// </remarks>
public sealed class ConnectorAuthorizationRecord : ITenantOwned
{
    /// <summary>The longest stored provider identifier.</summary>
    public const int MaxProviderIdentifierLength = 200;

    private ConnectorAuthorizationRecord()
    {
        // Materialization only.
    }

    /// <summary>Gets the connection. One authorization per connection.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>Gets the owning tenant. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the canonical user who authorized the connection.</summary>
    public UserId CanonicalUserId { get; private set; }

    /// <summary>Gets the lifecycle state.</summary>
    public ConnectorAuthorizationState State { get; private set; }

    /// <summary>Gets the provider's stable identifier for the delegating user.</summary>
    public string ProviderSubject { get; private set; } = string.Empty;

    /// <summary>Gets the provider's identifier for the delegating user's account.</summary>
    public string ProviderAccount { get; private set; } = string.Empty;

    /// <summary>Gets the credential generation; each renewal advances it by one.</summary>
    public long Generation { get; private set; }

    /// <summary>Gets the opaque version naming the current stored credential.</summary>
    public Guid MaterialVersion { get; private set; }

    /// <summary>Gets when the current access credential expires.</summary>
    public DateTimeOffset AccessExpiresAtUtc { get; private set; }

    /// <summary>Gets the scope names the provider granted.</summary>
    public string GrantedScopes { get; private set; } = string.Empty;

    /// <summary>Gets the renewal in progress, which also names that renewal's stored credential.</summary>
    public Guid? RefreshClaimId { get; private set; }

    /// <summary>Gets when the renewal in progress took its claim, by the database clock.</summary>
    public DateTimeOffset? RefreshClaimedAtUtc { get; private set; }

    /// <summary>Gets when the connection was last authorized, by the database clock.</summary>
    public DateTimeOffset AuthorizedAtUtc { get; private set; }

    /// <summary>Gets when the credential was last renewed.</summary>
    public DateTimeOffset? RefreshedAtUtc { get; private set; }

    /// <summary>Gets when the authorization came to require reauthorization.</summary>
    public DateTimeOffset? ReauthorizationRequiredAtUtc { get; private set; }

    /// <summary>Gets when the connection was disconnected.</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>Gets whether the provider confirmed the revocation at disconnect; null until it was attempted.</summary>
    public bool? ProviderRevocationConfirmed { get; private set; }
}
