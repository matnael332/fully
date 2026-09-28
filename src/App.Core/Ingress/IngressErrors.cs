using WorkContinuity.Core.Events;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Ingress;

/// <summary>
/// The stable ingress error codes, and the canonical quarantine reason behind each rejection.
/// </summary>
/// <remarks>
/// A rejection is permanent: the delivery is refused before any payload is persisted (Gate 2 owner
/// decision 6), so there is no tenant-less payload store. <see cref="Unavailable"/> is the only
/// non-rejection: the delivery was not stored and must not be acknowledged, so the source retries it.
/// Every message is fixed text and never echoes the delivery.
/// </remarks>
public static class IngressErrors
{
    /// <summary>The connection id is missing.</summary>
    public static Error InvalidConnectionId { get; } =
        Error.Validation("ingress.connection_id.invalid", "The connection id is missing.");

    /// <summary>The claimed source system is malformed.</summary>
    public static Error InvalidSourceSystem { get; } =
        Error.Validation("ingress.source_system.invalid", "The source system is malformed.");

    /// <summary>The provider event id is malformed.</summary>
    public static Error InvalidSourceEventId { get; } =
        Error.Validation("ingress.source_event_id.invalid", "The source event id is malformed.");

    /// <summary>The source schema version is malformed.</summary>
    public static Error InvalidSchemaVersion { get; } =
        Error.Validation("ingress.schema_version.invalid", "The source schema version is malformed.");

    /// <summary>The payload is empty.</summary>
    public static Error EmptyPayload { get; } =
        Error.Validation("ingress.payload.empty", "The payload is empty.");

    /// <summary>The payload is not a JSON document that can be stored.</summary>
    public static Error PayloadNotStorable { get; } =
        Error.Validation("ingress.payload.not_storable", "The payload is not a storable JSON document.");

    /// <summary>No active connection has this id.</summary>
    public static Error UnknownConnection { get; } =
        Error.NotFound("ingress.connection.unknown", "The connection is unknown or inactive.");

    /// <summary>The connection's tenant is not active.</summary>
    public static Error TenantInactive { get; } =
        Error.Forbidden("ingress.tenant.inactive", "The connection's tenant is not active.");

    /// <summary>The claimed source system is not the connection's source system.</summary>
    public static Error SourceMismatch { get; } =
        Error.Unauthorized("ingress.source.mismatch", "The source does not match the connection.");

    /// <summary>No verifier accepted the delivery.</summary>
    public static Error SourceNotVerified { get; } =
        Error.Unauthorized("ingress.source.not_verified", "The source could not be verified.");

    /// <summary>The delivery was not stored and must not be acknowledged.</summary>
    public static Error Unavailable { get; } =
        Error.Infrastructure("ingress.unavailable", "The delivery was not stored and must not be acknowledged; retry it.");

    /// <summary>Gets every rejection error.</summary>
    public static IReadOnlyList<Error> Rejections { get; } =
    [
        InvalidConnectionId,
        InvalidSourceSystem,
        InvalidSourceEventId,
        InvalidSchemaVersion,
        EmptyPayload,
        PayloadNotStorable,
        UnknownConnection,
        TenantInactive,
        SourceMismatch,
        SourceNotVerified,
    ];

    /// <summary>Maps a rejection to its canonical quarantine reason.</summary>
    /// <exception cref="ArgumentException">The error is not an ingress rejection.</exception>
    public static QuarantineReason RejectionReasonFor(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error == UnknownConnection || error == TenantInactive)
        {
            return QuarantineReason.UnknownTenant;
        }

        if (error == SourceMismatch || error == SourceNotVerified)
        {
            return QuarantineReason.InvalidAuthentication;
        }

        if (Rejections.Contains(error))
        {
            return QuarantineReason.BadSchema;
        }

        throw new ArgumentException("The error is not an ingress rejection.", nameof(error));
    }
}
