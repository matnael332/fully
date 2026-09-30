using WorkContinuity.Core.Events;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Connectors;

/// <summary>The lifecycle state of a connection's delegated authorization.</summary>
public enum ConnectorAuthorizationState
{
    /// <summary>Unspecified. Never stored.</summary>
    Unspecified = 0,

    /// <summary>The authorization is in force; its credential may be used.</summary>
    Active = 1,

    /// <summary>The credential can no longer be trusted or renewed; only the authorizing user can restore it.</summary>
    ReauthorizationRequired = 2,

    /// <summary>The connection was disconnected. Final: nothing restores it.</summary>
    Revoked = 3,
}

/// <summary>Why an authorization was started.</summary>
public enum ConnectorAuthorizationPurpose
{
    /// <summary>Unspecified. Never stored.</summary>
    Unspecified = 0,

    /// <summary>Authorizing a new connection.</summary>
    Connect = 1,

    /// <summary>Restoring a connection that requires reauthorization, by the same user and provider identity.</summary>
    Reauthorize = 2,
}

/// <summary>
/// One started authorization: server-owned, bound to one tenant, one canonical user and one intended connection, and
/// usable once.
/// </summary>
/// <remarks>
/// <para>
/// It holds hashes, never the values they hash: the state's secret part and the redirect address are compared by their
/// SHA-256, so neither can be read back from storage. The tenant is never part of the record; the store takes it from
/// the trusted server-side context. The creation and expiry instants are the database's own clock.
/// </para>
/// <para>
/// The record carries no credential and no verifier; the connector keeps those in its secret storage (decision D3B-2).
/// </para>
/// </remarks>
public sealed record ConnectorAuthorizationTransaction
{
    /// <summary>The length of each hash, in bytes: a SHA-256.</summary>
    public const int HashLength = 32;

    /// <summary>The longest time a started authorization stays usable: the provider's code validity.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Creates a transaction.</summary>
    /// <exception cref="ArgumentException">A component is missing or malformed.</exception>
    public ConnectorAuthorizationTransaction(
        Guid transactionId,
        UserId canonicalUser,
        Guid connectionId,
        string sourceSystem,
        ConnectorAuthorizationPurpose purpose,
        ReadOnlyMemory<byte> stateHash,
        ReadOnlyMemory<byte> redirectHash,
        string requestedScopes)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("An authorization transaction requires an id.", nameof(transactionId));
        }

        if (canonicalUser.IsEmpty)
        {
            throw new ArgumentException("An authorization transaction requires a canonical user.", nameof(canonicalUser));
        }

        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("An authorization transaction requires a connection.", nameof(connectionId));
        }

        if (!EventText.IsToken(sourceSystem))
        {
            throw new ArgumentException("An authorization transaction requires a source system token.", nameof(sourceSystem));
        }

        if (purpose == ConnectorAuthorizationPurpose.Unspecified || !Enum.IsDefined(purpose))
        {
            throw new ArgumentException("An authorization transaction requires a defined purpose.", nameof(purpose));
        }

        if (stateHash.Length != HashLength || redirectHash.Length != HashLength)
        {
            throw new ArgumentException("An authorization transaction requires SHA-256 hashes.", nameof(stateHash));
        }

        if (!ConnectorScopes.IsWellFormed(requestedScopes))
        {
            throw new ArgumentException("An authorization transaction requires a well-formed scope list.", nameof(requestedScopes));
        }

        TransactionId = transactionId;
        CanonicalUser = canonicalUser;
        ConnectionId = connectionId;
        SourceSystem = sourceSystem;
        Purpose = purpose;
        StateHash = stateHash.ToArray();
        RedirectHash = redirectHash.ToArray();
        RequestedScopes = requestedScopes;
    }

    /// <summary>Gets the transaction id, which the provider echoes back inside the state.</summary>
    public Guid TransactionId { get; }

    /// <summary>Gets the canonical user who started the authorization.</summary>
    public UserId CanonicalUser { get; }

    /// <summary>Gets the connection the authorization is for: a new one, or the one being restored.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets the source system token, for example the connector's name.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets why the authorization was started.</summary>
    public ConnectorAuthorizationPurpose Purpose { get; }

    /// <summary>Gets the SHA-256 of the state's secret part.</summary>
    public ReadOnlyMemory<byte> StateHash { get; }

    /// <summary>Gets the SHA-256 of the redirect address the authorization was started with.</summary>
    public ReadOnlyMemory<byte> RedirectHash { get; }

    /// <summary>Gets the scope names requested.</summary>
    public string RequestedScopes { get; }

    /// <summary>Identifies the transaction without its hashes.</summary>
    public override string ToString() =>
        $"ConnectorAuthorizationTransaction({TransactionId:D}, {Purpose}, connection {ConnectionId:D})";
}

/// <summary>What consuming a transaction found, in the order the checks run.</summary>
public enum ConnectorTransactionOutcome
{
    /// <summary>Unspecified. Never a real outcome.</summary>
    Unspecified = 0,

    /// <summary>The transaction was valid and is now consumed; nothing can use it again.</summary>
    Consumed = 1,

    /// <summary>No such transaction exists in the trusted tenant.</summary>
    NotFound = 2,

    /// <summary>The transaction has expired by the database clock.</summary>
    Expired = 3,

    /// <summary>The transaction belongs to another canonical user. It is left unconsumed.</summary>
    BindingMismatch = 4,

    /// <summary>The state's secret part does not match. The transaction is left unconsumed.</summary>
    StateMismatch = 5,

    /// <summary>The transaction was already consumed: a replay, or a concurrent second callback.</summary>
    AlreadyConsumed = 6,
}

/// <summary>The result of consuming a transaction: the outcome, and the transaction when it was consumed.</summary>
public sealed record ConnectorTransactionConsumption
{
    private ConnectorTransactionConsumption(ConnectorTransactionOutcome outcome, ConnectorAuthorizationTransaction? transaction)
    {
        Outcome = outcome;
        Transaction = transaction;
    }

    /// <summary>Gets what consuming found.</summary>
    public ConnectorTransactionOutcome Outcome { get; }

    /// <summary>Gets the consumed transaction; null unless <see cref="Outcome"/> is <see cref="ConnectorTransactionOutcome.Consumed"/>.</summary>
    public ConnectorAuthorizationTransaction? Transaction { get; }

    /// <summary>The transaction was consumed.</summary>
    /// <exception cref="ArgumentNullException">The transaction is null.</exception>
    public static ConnectorTransactionConsumption Consumed(ConnectorAuthorizationTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new ConnectorTransactionConsumption(ConnectorTransactionOutcome.Consumed, transaction);
    }

    /// <summary>The transaction was refused.</summary>
    /// <exception cref="ArgumentException">The outcome is not a refusal.</exception>
    public static ConnectorTransactionConsumption Refused(ConnectorTransactionOutcome outcome) =>
        outcome is ConnectorTransactionOutcome.Unspecified or ConnectorTransactionOutcome.Consumed || !Enum.IsDefined(outcome)
            ? throw new ArgumentException("A refusal requires a defined refusing outcome.", nameof(outcome))
            : new ConnectorTransactionConsumption(outcome, transaction: null);
}

/// <summary>
/// What activating or restoring an authorization stores: the verified delegated identity and the metadata of the
/// credential the connector has already put in its secret storage.
/// </summary>
/// <remarks>
/// The material version names where the connector stored the credential; it is an opaque identifier, never the
/// credential. The provider subject and account are the provider's stable identifiers, never an email address.
/// </remarks>
public sealed record ConnectorAuthorizationGrant
{
    /// <summary>Creates a grant.</summary>
    /// <exception cref="ArgumentException">A component is missing or malformed.</exception>
    public ConnectorAuthorizationGrant(
        Guid connectionId,
        string sourceSystem,
        UserId canonicalUser,
        string providerSubject,
        string providerAccount,
        Guid materialVersion,
        DateTimeOffset accessExpiresAt,
        string grantedScopes)
    {
        if (connectionId == Guid.Empty || materialVersion == Guid.Empty)
        {
            throw new ArgumentException("A grant requires a connection and a material version.", nameof(connectionId));
        }

        if (!EventText.IsToken(sourceSystem))
        {
            throw new ArgumentException("A grant requires a source system token.", nameof(sourceSystem));
        }

        if (canonicalUser.IsEmpty)
        {
            throw new ArgumentException("A grant requires the canonical user who authorized it.", nameof(canonicalUser));
        }

        if (!ConnectorAuthorization.IsProviderIdentifier(providerSubject)
            || !ConnectorAuthorization.IsProviderIdentifier(providerAccount))
        {
            throw new ArgumentException(
                "A grant requires the provider's stable subject and account identifiers, never an email address.",
                nameof(providerSubject));
        }

        if (!ConnectorScopes.IsWellFormed(grantedScopes))
        {
            throw new ArgumentException("A grant requires a well-formed scope list.", nameof(grantedScopes));
        }

        ConnectionId = connectionId;
        SourceSystem = sourceSystem;
        CanonicalUser = canonicalUser;
        ProviderSubject = providerSubject;
        ProviderAccount = providerAccount;
        MaterialVersion = materialVersion;
        AccessExpiresAt = accessExpiresAt.ToUniversalTime();
        GrantedScopes = grantedScopes;
    }

    /// <summary>Gets the connection.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets the source system token.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the canonical user who authorized the connection.</summary>
    public UserId CanonicalUser { get; }

    /// <summary>Gets the provider's stable identifier for the delegating user.</summary>
    public string ProviderSubject { get; }

    /// <summary>Gets the provider's identifier for the delegating user's account.</summary>
    public string ProviderAccount { get; }

    /// <summary>Gets the opaque version naming the stored credential.</summary>
    public Guid MaterialVersion { get; }

    /// <summary>Gets when the stored access credential expires.</summary>
    public DateTimeOffset AccessExpiresAt { get; }

    /// <summary>Gets the scope names the provider granted.</summary>
    public string GrantedScopes { get; }

    /// <summary>Identifies the grant by its connection.</summary>
    public override string ToString() => $"ConnectorAuthorizationGrant(connection {ConnectionId:D})";
}

/// <summary>
/// A connection's delegated authorization as stored, read in the trusted tenant: its state, the delegation chain, and
/// the metadata of its current credential. It never holds the credential.
/// </summary>
/// <remarks>
/// <para>
/// The chain is the one decision D3B-1 requires: tenant, canonical user, connection, delegated provider identity. Both
/// ends of it are rechecked at every access: the connection must still be enabled and the canonical user still active.
/// </para>
/// <para>
/// The refresh claim is the single-flight marker of a credential renewal in progress, and its age is measured by the
/// database clock, so every instance judges a stale claim the same way.
/// </para>
/// </remarks>
public sealed record ConnectorAuthorization
{
    /// <summary>Creates an authorization as read from storage.</summary>
    /// <exception cref="ArgumentException">A component is missing or malformed.</exception>
    public ConnectorAuthorization(
        TenantId tenant,
        Guid connectionId,
        string sourceSystem,
        bool connectionEnabled,
        UserId canonicalUser,
        bool canonicalUserActive,
        ConnectorAuthorizationState state,
        string providerSubject,
        string providerAccount,
        long generation,
        Guid materialVersion,
        DateTimeOffset accessExpiresAt,
        string grantedScopes,
        Guid? refreshClaim,
        TimeSpan? refreshClaimAge)
    {
        if (tenant.IsEmpty || connectionId == Guid.Empty || canonicalUser.IsEmpty || materialVersion == Guid.Empty)
        {
            throw new ArgumentException(
                "An authorization requires a tenant, a connection, a canonical user and a material version.",
                nameof(tenant));
        }

        if (!EventText.IsToken(sourceSystem))
        {
            throw new ArgumentException("An authorization requires a source system token.", nameof(sourceSystem));
        }

        if (state == ConnectorAuthorizationState.Unspecified || !Enum.IsDefined(state))
        {
            throw new ArgumentException("An authorization requires a defined state.", nameof(state));
        }

        if (!IsProviderIdentifier(providerSubject) || !IsProviderIdentifier(providerAccount))
        {
            throw new ArgumentException(
                "An authorization requires the provider's stable identifiers, never an email address.",
                nameof(providerSubject));
        }

        if (generation < 1)
        {
            throw new ArgumentException("An authorization's generation starts at 1.", nameof(generation));
        }

        if (!ConnectorScopes.IsWellFormed(grantedScopes))
        {
            throw new ArgumentException("An authorization requires a well-formed scope list.", nameof(grantedScopes));
        }

        if (refreshClaim == Guid.Empty || refreshClaim.HasValue != refreshClaimAge.HasValue)
        {
            throw new ArgumentException("A refresh claim is an id together with its age.", nameof(refreshClaim));
        }

        Tenant = tenant;
        ConnectionId = connectionId;
        SourceSystem = sourceSystem;
        ConnectionEnabled = connectionEnabled;
        CanonicalUser = canonicalUser;
        CanonicalUserActive = canonicalUserActive;
        State = state;
        ProviderSubject = providerSubject;
        ProviderAccount = providerAccount;
        Generation = generation;
        MaterialVersion = materialVersion;
        AccessExpiresAt = accessExpiresAt.ToUniversalTime();
        GrantedScopes = grantedScopes;
        RefreshClaim = refreshClaim;
        RefreshClaimAge = refreshClaimAge < TimeSpan.Zero ? TimeSpan.Zero : refreshClaimAge;
    }

    /// <summary>Gets the tenant the connection belongs to.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the connection.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets the source system token.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets a value indicating whether the connection itself is enabled.</summary>
    public bool ConnectionEnabled { get; }

    /// <summary>Gets the canonical user who authorized the connection.</summary>
    public UserId CanonicalUser { get; }

    /// <summary>Gets a value indicating whether that user is still an active member of the tenant.</summary>
    public bool CanonicalUserActive { get; }

    /// <summary>Gets the authorization's state.</summary>
    public ConnectorAuthorizationState State { get; }

    /// <summary>Gets the provider's stable identifier for the delegating user.</summary>
    public string ProviderSubject { get; }

    /// <summary>Gets the provider's identifier for the delegating user's account.</summary>
    public string ProviderAccount { get; }

    /// <summary>Gets the credential generation; each renewal advances it by one.</summary>
    public long Generation { get; }

    /// <summary>Gets the opaque version naming the current stored credential.</summary>
    public Guid MaterialVersion { get; }

    /// <summary>Gets when the current access credential expires.</summary>
    public DateTimeOffset AccessExpiresAt { get; }

    /// <summary>Gets the scope names the provider granted.</summary>
    public string GrantedScopes { get; }

    /// <summary>Gets the id of the renewal in progress, when there is one; it also names that renewal's stored credential.</summary>
    public Guid? RefreshClaim { get; }

    /// <summary>Gets how long the renewal in progress has held its claim, by the database clock.</summary>
    public TimeSpan? RefreshClaimAge { get; }

    /// <summary>Gets the identity the connection acts under: the delegating user's provider identity.</summary>
    public ConnectorIdentity DelegatedIdentity =>
        new(Tenant, ConnectionId, ConnectorIdentityKind.DelegatedUser, ProviderSubject);

    /// <summary>Identifies the authorization without its identifiers.</summary>
    public override string ToString() =>
        $"ConnectorAuthorization(connection {ConnectionId:D}, {State}, generation {Generation})";

    /// <summary>Whether the text is a provider's stable identifier: opaque text that is not an email address.</summary>
    internal static bool IsProviderIdentifier(string? value) =>
        EventText.IsIdentifier(value) && !value!.Contains('@', StringComparison.Ordinal);
}

/// <summary>What disconnecting found.</summary>
public enum ConnectorDisconnectOutcome
{
    /// <summary>Unspecified. Never a real outcome.</summary>
    Unspecified = 0,

    /// <summary>The authorization is now revoked and the connection disabled.</summary>
    Disconnected = 1,

    /// <summary>The connection was already disconnected; nothing changed.</summary>
    AlreadyDisconnected = 2,

    /// <summary>No authorization exists for the connection in the trusted tenant.</summary>
    NotFound = 3,

    /// <summary>The requesting user may not disconnect this connection; nothing changed.</summary>
    NotPermitted = 4,
}

/// <summary>
/// The result of disconnecting: the outcome and, when the connection was just disconnected, the versions of the
/// stored credentials the connector must now revoke and destroy.
/// </summary>
public sealed record ConnectorDisconnection
{
    private ConnectorDisconnection(ConnectorDisconnectOutcome outcome, Guid? materialVersion, Guid? pendingMaterialVersion)
    {
        Outcome = outcome;
        MaterialVersion = materialVersion;
        PendingMaterialVersion = pendingMaterialVersion;
    }

    /// <summary>Gets what disconnecting found.</summary>
    public ConnectorDisconnectOutcome Outcome { get; }

    /// <summary>Gets the version of the current stored credential, when the connection was just disconnected.</summary>
    public Guid? MaterialVersion { get; }

    /// <summary>Gets the version a renewal in progress may have stored, when one held a claim.</summary>
    public Guid? PendingMaterialVersion { get; }

    /// <summary>The connection was just disconnected.</summary>
    /// <exception cref="ArgumentException">The version is empty.</exception>
    public static ConnectorDisconnection Disconnected(Guid materialVersion, Guid? pendingMaterialVersion) =>
        materialVersion == Guid.Empty || pendingMaterialVersion == Guid.Empty
            ? throw new ArgumentException("A disconnection names the stored versions.", nameof(materialVersion))
            : new ConnectorDisconnection(ConnectorDisconnectOutcome.Disconnected, materialVersion, pendingMaterialVersion);

    /// <summary>Nothing was disconnected.</summary>
    /// <exception cref="ArgumentException">The outcome is not a refusal.</exception>
    public static ConnectorDisconnection Unchanged(ConnectorDisconnectOutcome outcome) =>
        outcome is ConnectorDisconnectOutcome.Unspecified or ConnectorDisconnectOutcome.Disconnected || !Enum.IsDefined(outcome)
            ? throw new ArgumentException("An unchanged result requires a defined non-disconnecting outcome.", nameof(outcome))
            : new ConnectorDisconnection(outcome, materialVersion: null, pendingMaterialVersion: null);
}
