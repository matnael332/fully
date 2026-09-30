using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Connectors;

/// <summary>
/// The stable errors of a connection's authorization lifecycle: starting, completing and disconnecting.
/// </summary>
/// <remarks>
/// Every message is fixed text. None echoes the provider, a state, a code, a token, a verifier or a secret, so an
/// error can be returned, logged or shown without carrying anything an attacker could reuse.
/// </remarks>
public static class ConnectorAuthorizationErrors
{
    /// <summary>The request was malformed.</summary>
    public static Error InvalidRequest { get; } =
        Error.Validation("connector.authorization.invalid_request", "The authorization request is invalid.");

    /// <summary>No trusted tenant is established for the operation.</summary>
    public static Error TenantUnresolved { get; } =
        Error.Unauthorized("connector.authorization.tenant_unresolved", "No trusted tenant is established for the authorization.");

    /// <summary>The user is not an authenticated, active member of the tenant.</summary>
    public static Error UserNotEligible { get; } =
        Error.Forbidden("connector.authorization.user_not_eligible", "The user cannot authorize a connection.");

    /// <summary>The connection cannot be reauthorized: it is not awaiting reauthorization by this user.</summary>
    public static Error NotReauthorizable { get; } =
        Error.Conflict("connector.authorization.not_reauthorizable", "The connection cannot be reauthorized.");

    /// <summary>The state names no transaction in the tenant, or is malformed.</summary>
    public static Error TransactionNotFound { get; } =
        Error.NotFound("connector.authorization.transaction_not_found", "The authorization transaction was not found.");

    /// <summary>The transaction expired.</summary>
    public static Error TransactionExpired { get; } =
        Error.Conflict("connector.authorization.transaction_expired", "The authorization transaction has expired.");

    /// <summary>The transaction belongs to another user.</summary>
    public static Error BindingMismatch { get; } =
        Error.Forbidden("connector.authorization.binding_mismatch", "The authorization belongs to another user.");

    /// <summary>The state does not match the transaction.</summary>
    public static Error StateMismatch { get; } =
        Error.Forbidden("connector.authorization.state_mismatch", "The authorization state does not match.");

    /// <summary>The transaction was already used.</summary>
    public static Error Replayed { get; } =
        Error.Conflict("connector.authorization.replayed", "The authorization response was already used.");

    /// <summary>The provider did not report an approval.</summary>
    public static Error Denied { get; } =
        Error.Forbidden("connector.authorization.denied", "The authorization was not approved.");

    /// <summary>The response names an issuer other than the provider's.</summary>
    public static Error IssuerMismatch { get; } =
        Error.Forbidden("connector.authorization.issuer_mismatch", "The authorization response names an unexpected issuer.");

    /// <summary>The response carries no usable code.</summary>
    public static Error CodeMissing { get; } =
        Error.Validation("connector.authorization.code_missing", "The authorization response carries no usable code.");

    /// <summary>The configured redirect address no longer matches the one the transaction was started with.</summary>
    public static Error RedirectMismatch { get; } =
        Error.Conflict("connector.authorization.redirect_mismatch", "The redirect address no longer matches the authorization.");

    /// <summary>The connector's own client configuration or client secret is unavailable.</summary>
    public static Error ClientUnavailable { get; } =
        Error.Infrastructure("connector.authorization.client_unavailable", "The connector's client configuration is unavailable.");

    /// <summary>The provider rejected the code exchange.</summary>
    public static Error ExchangeRejected { get; } =
        Error.Unauthorized("connector.authorization.exchange_rejected", "The provider rejected the authorization code.");

    /// <summary>The exchange failed or its outcome is unknown; it is never retried.</summary>
    public static Error ExchangeFailed { get; } =
        Error.Infrastructure("connector.authorization.exchange_failed", "The authorization code could not be exchanged.");

    /// <summary>The provider's grant is incomplete, or its expiry is unusable.</summary>
    public static Error GrantUnusable { get; } =
        Error.Infrastructure("connector.authorization.grant_unusable", "The provider's grant is incomplete or unusable.");

    /// <summary>The granted scopes are not permitted, or lack a required one.</summary>
    public static Error ScopeRefused { get; } =
        Error.Forbidden("connector.authorization.scope_refused", "The granted scopes are not permitted.");

    /// <summary>The delegated identity could not be verified.</summary>
    public static Error IdentityUnverified { get; } =
        Error.Infrastructure("connector.authorization.identity_unverified", "The delegated identity could not be verified.");

    /// <summary>The delegated identity differs from the connection's recorded identity.</summary>
    public static Error IdentityMismatch { get; } =
        Error.Forbidden("connector.authorization.identity_mismatch", "The delegated identity differs from the connection's.");

    /// <summary>The authorization could not be stored.</summary>
    public static Error StorageFailed { get; } =
        Error.Infrastructure("connector.authorization.storage_failed", "The authorization could not be stored.");

    /// <summary>The connection has no authorization in the tenant.</summary>
    public static Error ConnectionNotFound { get; } =
        Error.NotFound("connector.authorization.connection_not_found", "The connection was not found.");

    /// <summary>The user may not change the connection.</summary>
    public static Error NotPermitted { get; } =
        Error.Forbidden("connector.authorization.not_permitted", "The user may not change this connection.");

    /// <summary>Every authorization error, for tests and dashboards.</summary>
    public static IReadOnlyList<Error> All { get; } =
    [
        InvalidRequest, TenantUnresolved, UserNotEligible, NotReauthorizable, TransactionNotFound, TransactionExpired,
        BindingMismatch, StateMismatch, Replayed, Denied, IssuerMismatch, CodeMissing, RedirectMismatch, ClientUnavailable,
        ExchangeRejected, ExchangeFailed, GrantUnusable, ScopeRefused, IdentityUnverified, IdentityMismatch, StorageFailed,
        ConnectionNotFound, NotPermitted,
    ];
}
