using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Connectors;

/// <summary>
/// The stable, provider-neutral outcome of a failed connector call.
/// </summary>
/// <remarks>
/// Each connector translates its provider's statuses, error codes and exceptions onto these kinds, so callers decide
/// on a small closed set instead of on provider detail. Only <see cref="RateLimited"/>, <see cref="ProviderTransient"/>
/// and <see cref="NetworkTransient"/> are transient; every other kind is permanent for the attempt that produced it.
/// </remarks>
public enum ConnectorFailureKind
{
    /// <summary>Unspecified. Never a real failure.</summary>
    Unspecified = 0,

    /// <summary>The request itself was invalid before anything was sent.</summary>
    InvalidRequest = 1,

    /// <summary>The operation is not one this connector may perform.</summary>
    OperationNotPermitted = 2,

    /// <summary>The connection is disabled; nothing was sent.</summary>
    ConnectionDisabled = 3,

    /// <summary>No usable credential exists for the connection; nothing was sent.</summary>
    CredentialUnavailable = 4,

    /// <summary>The credential was revoked, expired or rejected by the provider.</summary>
    AuthenticationFailed = 5,

    /// <summary>The provider refused the operation for this identity or its granted scopes.</summary>
    AuthorizationFailed = 6,

    /// <summary>The provider rejected the request as invalid. Permanent.</summary>
    ProviderValidation = 7,

    /// <summary>The provider reports the addressed resource as absent.</summary>
    ResourceNotFound = 8,

    /// <summary>The provider is throttling requests. Transient.</summary>
    RateLimited = 9,

    /// <summary>The provider failed temporarily. Transient.</summary>
    ProviderTransient = 10,

    /// <summary>The provider could not be reached in time. Transient.</summary>
    NetworkTransient = 11,

    /// <summary>The provider's response was not understood. Permanent: an unknown shape is never guessed at.</summary>
    UnsupportedResponse = 12,
}

/// <summary>
/// A connector call's failure: its kind, a safe error and, when the provider said, how long to wait.
/// </summary>
/// <remarks>
/// The error is always one of the fixed <see cref="ConnectorErrors"/>, so no provider message, payload or credential can
/// travel in a failure.
/// </remarks>
public sealed record ConnectorFailure
{
    /// <summary>Creates a failure.</summary>
    /// <param name="kind">The failure's kind.</param>
    /// <param name="error">The safe error describing it.</param>
    /// <param name="retryAfter">How long the provider asked the caller to wait, if it said.</param>
    /// <exception cref="ArgumentException">The kind is unspecified or the wait is negative.</exception>
    public ConnectorFailure(ConnectorFailureKind kind, Error error, TimeSpan? retryAfter = null)
    {
        if (kind == ConnectorFailureKind.Unspecified || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("A connector failure requires a defined kind.", nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(error);

        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentException("A provider's requested wait cannot be negative.", nameof(retryAfter));
        }

        Kind = kind;
        Error = error;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets the failure's kind.</summary>
    public ConnectorFailureKind Kind { get; }

    /// <summary>Gets the safe error describing the failure.</summary>
    public Error Error { get; }

    /// <summary>Gets how long the provider asked the caller to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Gets a value indicating whether a later attempt could succeed without any change.</summary>
    public bool IsTransient => Kind is ConnectorFailureKind.RateLimited
        or ConnectorFailureKind.ProviderTransient
        or ConnectorFailureKind.NetworkTransient;

    /// <summary>Identifies the failure by kind and code.</summary>
    public override string ToString() => $"ConnectorFailure({Kind}, {Error.Code})";
}

/// <summary>
/// The stable connector error codes. Every message is fixed text that never echoes the provider.
/// </summary>
public static class ConnectorErrors
{
    /// <summary>The request was invalid before anything was sent.</summary>
    public static Error InvalidRequest { get; } =
        Error.Validation("connector.request.invalid", "The connector request is invalid.");

    /// <summary>The operation is not one this connector may perform.</summary>
    public static Error OperationNotPermitted { get; } =
        Error.Forbidden("connector.operation.not_permitted", "The operation is not permitted for this connector.");

    /// <summary>The connection is disabled.</summary>
    public static Error ConnectionDisabled { get; } =
        Error.Forbidden("connector.connection.disabled", "The connection is disabled.");

    /// <summary>No usable credential exists for the connection.</summary>
    public static Error CredentialUnavailable { get; } =
        Error.Infrastructure("connector.credential.unavailable", "No usable credential is available for the connection.");

    /// <summary>The credential offered does not belong to the requesting tenant and connection.</summary>
    public static Error CredentialMismatch { get; } =
        Error.Forbidden("connector.credential.mismatch", "The credential does not belong to the requesting connection.");

    /// <summary>The connection's credential was revoked.</summary>
    public static Error CredentialRevoked { get; } =
        Error.Unauthorized("connector.credential.revoked", "The connection's credential has been revoked.");

    /// <summary>The connection's credential has expired.</summary>
    public static Error CredentialExpired { get; } =
        Error.Unauthorized("connector.credential.expired", "The connection's credential has expired.");

    /// <summary>The connection's credential can no longer be trusted or renewed; its user must authorize it again.</summary>
    public static Error ReauthorizationRequired { get; } =
        Error.Unauthorized("connector.credential.reauthorization_required", "The connection must be authorized again.");

    /// <summary>The provider rejected the credential.</summary>
    public static Error AuthenticationFailed { get; } =
        Error.Unauthorized("connector.provider.authentication_failed", "The provider rejected the credential.");

    /// <summary>The provider refused the operation for this identity or its scopes.</summary>
    public static Error AuthorizationFailed { get; } =
        Error.Forbidden("connector.provider.authorization_failed", "The provider refused the operation for this identity.");

    /// <summary>The provider rejected the request as invalid.</summary>
    public static Error ProviderValidation { get; } =
        Error.Validation("connector.provider.request_rejected", "The provider rejected the request as invalid.");

    /// <summary>The provider rejected the pinned API version.</summary>
    public static Error ApiVersionRejected { get; } =
        Error.Validation("connector.provider.api_version_rejected", "The provider rejected the pinned API version.");

    /// <summary>The provider reports the resource as absent.</summary>
    public static Error ResourceNotFound { get; } =
        Error.NotFound("connector.provider.resource_not_found", "The provider reports the resource as not found.");

    /// <summary>The provider is throttling requests.</summary>
    public static Error RateLimited { get; } =
        Error.Infrastructure("connector.provider.rate_limited", "The provider is throttling requests.");

    /// <summary>The provider failed temporarily.</summary>
    public static Error ProviderUnavailable { get; } =
        Error.Infrastructure("connector.provider.unavailable", "The provider is temporarily unavailable.");

    /// <summary>The provider could not be reached.</summary>
    public static Error NetworkFailure { get; } =
        Error.Infrastructure("connector.network.failure", "The provider could not be reached.");

    /// <summary>The provider did not answer in time.</summary>
    public static Error Timeout { get; } =
        Error.Infrastructure("connector.network.timeout", "The provider did not answer in time.");

    /// <summary>The provider's response was not understood.</summary>
    public static Error UnsupportedResponse { get; } =
        Error.Infrastructure("connector.provider.unsupported_response", "The provider's response was not understood.");

    /// <summary>Every connector error, for tests and dashboards.</summary>
    public static IReadOnlyList<Error> All { get; } =
    [
        InvalidRequest, OperationNotPermitted, ConnectionDisabled, CredentialUnavailable, CredentialMismatch,
        CredentialRevoked, CredentialExpired, ReauthorizationRequired, AuthenticationFailed, AuthorizationFailed,
        ProviderValidation, ApiVersionRejected, ResourceNotFound, RateLimited, ProviderUnavailable, NetworkFailure, Timeout,
        UnsupportedResponse,
    ];
}
