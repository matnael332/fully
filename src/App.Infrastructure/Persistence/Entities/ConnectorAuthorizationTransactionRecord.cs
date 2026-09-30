using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// One started authorization (Gate 3B): server-owned, bound to its tenant, canonical user and intended connection, and
/// consumed at most once.
/// </summary>
/// <remarks>
/// <para>
/// It keeps hashes, never the values they hash: the SHA-256 of the state's secret part and of the redirect address.
/// The PKCE verifier is secret material and lives only in the connector's secret storage (decision D3B-2). The
/// creation, expiry and consumption instants are stamped by the database clock, and a check constraint caps the
/// lifetime at the provider's 10-minute code validity.
/// </para>
/// <para>
/// Rows are written only by <c>ConnectorAuthorizationStore</c>, so this type has no factory.
/// </para>
/// </remarks>
public sealed class ConnectorAuthorizationTransactionRecord : ITenantOwned
{
    private ConnectorAuthorizationTransactionRecord()
    {
        // Materialization only.
    }

    /// <summary>Gets the transaction id.</summary>
    public Guid TransactionId { get; private set; }

    /// <summary>Gets the owning tenant. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the canonical user who started the authorization.</summary>
    public UserId CanonicalUserId { get; private set; }

    /// <summary>Gets the connection the authorization is for.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>Gets the source system token.</summary>
    public string SourceSystem { get; private set; } = string.Empty;

    /// <summary>Gets why the authorization was started.</summary>
    public ConnectorAuthorizationPurpose Purpose { get; private set; }

    /// <summary>Gets the SHA-256 of the state's secret part.</summary>
    public byte[] StateHash { get; private set; } = [];

    /// <summary>Gets the SHA-256 of the redirect address.</summary>
    public byte[] RedirectHash { get; private set; } = [];

    /// <summary>Gets the scope names requested.</summary>
    public string RequestedScopes { get; private set; } = string.Empty;

    /// <summary>Gets when the transaction was opened.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets when the transaction stops being usable.</summary>
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>Gets when the transaction was consumed or closed; null while it is open.</summary>
    public DateTimeOffset? ConsumedAtUtc { get; private set; }
}
