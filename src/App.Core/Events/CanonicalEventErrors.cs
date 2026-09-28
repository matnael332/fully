using WorkContinuity.Core.Results;

namespace WorkContinuity.Core.Events;

/// <summary>
/// The stable validation errors for canonical events and the quarantine reason each one maps to.
/// </summary>
/// <remarks>
/// Every message is a fixed string. Validation never echoes the offending value, so an error can be
/// logged or returned without carrying provider content, secrets or injected text.
/// </remarks>
public static class CanonicalEventErrors
{
    /// <summary>The event has no id.</summary>
    public static Error MissingEventId { get; } =
        Error.Validation("event.id.missing", "The event has no id.");

    /// <summary>The event belongs to no tenant.</summary>
    public static Error MissingTenant { get; } =
        Error.Validation("event.tenant.missing", "The event belongs to no tenant.");

    /// <summary>The event type is not a known canonical type.</summary>
    public static Error UnknownType { get; } =
        Error.Validation("event.type.unknown", "The event type is not a known canonical type.");

    /// <summary>The schema version is not supported for the event type.</summary>
    public static Error UnknownSchemaVersion { get; } =
        Error.Validation("event.schema_version.unknown", "The event schema version is not supported for its type.");

    /// <summary>The event source is malformed.</summary>
    public static Error InvalidSource { get; } =
        Error.Validation("event.source.invalid", "The event source is malformed.");

    /// <summary>The work object reference is malformed.</summary>
    public static Error InvalidObject { get; } =
        Error.Validation("event.object.invalid", "The event object reference is malformed.");

    /// <summary>The provider event id is malformed.</summary>
    public static Error InvalidSourceEventId { get; } =
        Error.Validation("event.source_event_id.invalid", "The source event id is malformed.");

    /// <summary>The provider revision is negative.</summary>
    public static Error InvalidSourceRevision { get; } =
        Error.Validation("event.source_revision.invalid", "The source revision is negative.");

    /// <summary>A time is missing where required, or is a sentinel value.</summary>
    public static Error InvalidTime { get; } =
        Error.Validation("event.time.invalid", "An event time is missing or is a sentinel value.");

    /// <summary>The event has no correlation id.</summary>
    public static Error MissingCorrelation { get; } =
        Error.Validation("event.correlation.missing", "The event has no correlation id.");

    /// <summary>The causation id is present but empty.</summary>
    public static Error InvalidCausation { get; } =
        Error.Validation("event.causation.invalid", "The causation id is empty.");

    /// <summary>The event carries no change.</summary>
    public static Error MissingChanges { get; } =
        Error.Validation("event.changes.missing", "The event carries no change.");

    /// <summary>A field change is malformed or has no known data type.</summary>
    public static Error InvalidChange { get; } =
        Error.Validation("event.change.invalid", "A field change is malformed or has no known data type.");

    /// <summary>A field value is malformed or longer than a Snapshot value may be.</summary>
    public static Error InvalidValue { get; } =
        Error.Validation("event.value.invalid", "A field value is malformed or too long.");

    /// <summary>One event changes the same field more than once.</summary>
    public static Error DuplicateField { get; } =
        Error.Validation("event.change.duplicate_field", "A field is changed more than once in one event.");

    /// <summary>The actor reference is malformed.</summary>
    public static Error InvalidActor { get; } =
        Error.Validation("event.actor.invalid", "The actor reference is malformed.");

    /// <summary>The provider reference is malformed.</summary>
    public static Error InvalidSourceReference { get; } =
        Error.Validation("event.source_reference.invalid", "The source reference is malformed.");

    /// <summary>Gets every canonical event validation error.</summary>
    public static IReadOnlyList<Error> All { get; } =
    [
        MissingEventId,
        MissingTenant,
        UnknownType,
        UnknownSchemaVersion,
        InvalidSource,
        InvalidObject,
        InvalidSourceEventId,
        InvalidSourceRevision,
        InvalidTime,
        MissingCorrelation,
        InvalidCausation,
        MissingChanges,
        InvalidChange,
        InvalidValue,
        DuplicateField,
        InvalidActor,
        InvalidSourceReference,
    ];

    /// <summary>Maps a canonical event validation error to its quarantine reason.</summary>
    /// <exception cref="ArgumentException">The error is not a canonical event validation error.</exception>
    public static QuarantineReason QuarantineReasonFor(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error == MissingTenant)
        {
            return QuarantineReason.UnknownTenant;
        }

        if (error == UnknownSchemaVersion)
        {
            return QuarantineReason.UnknownSchemaVersion;
        }

        if (All.Contains(error))
        {
            return QuarantineReason.BadSchema;
        }

        throw new ArgumentException("The error is not a canonical event validation error.", nameof(error));
    }
}
