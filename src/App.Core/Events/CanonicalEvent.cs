using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Events;

/// <summary>
/// A validated canonical event: a CloudEvents-compatible envelope with product extensions (PRD §11,
/// architecture §3), split into retained <see cref="Metadata"/> and expiring <see cref="Content"/>.
/// </summary>
/// <remarks>
/// <para>
/// The only way to obtain an instance is <see cref="Create"/>, so holding a <see cref="CanonicalEvent"/>
/// is proof that it passed validation. Validation is deterministic and total: malformed provider data
/// produces a failed <see cref="Result{TValue}"/> carrying a stable error code, never an exception, and
/// <see cref="CanonicalEventErrors.QuarantineReasonFor"/> maps that code to its canonical quarantine
/// reason.
/// </para>
/// <para>
/// Validation checks structure only. Deduplication, identity resolution, authority, ordering and
/// projection are later steps of the processing lifecycle and are not decided here.
/// </para>
/// </remarks>
public sealed class CanonicalEvent : ITenantOwned
{
    private CanonicalEvent(CanonicalEventMetadata metadata, CanonicalEventContent content)
    {
        Metadata = metadata;
        Content = content;
    }

    /// <summary>Gets the retained identity, provenance, ordering and deduplication metadata.</summary>
    public CanonicalEventMetadata Metadata { get; }

    /// <summary>Gets the expiring work content.</summary>
    public CanonicalEventContent Content { get; }

    /// <inheritdoc />
    public TenantId TenantId => Metadata.TenantId;

    /// <summary>Validates the parts and, if they are well formed, creates the event.</summary>
    /// <returns>The event, or the first validation error found.</returns>
    /// <exception cref="ArgumentNullException">A part is null.</exception>
    public static Result<CanonicalEvent> Create(CanonicalEventMetadata metadata, CanonicalEventContent content)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        var error = ValidateMetadata(metadata) ?? ValidateContent(content);
        return error is null
            ? Result<CanonicalEvent>.Success(new CanonicalEvent(metadata, content))
            : Result<CanonicalEvent>.Failure(error);
    }

    private static Error? ValidateMetadata(CanonicalEventMetadata metadata)
    {
        if (metadata.EventId.IsEmpty)
        {
            return CanonicalEventErrors.MissingEventId;
        }

        if (metadata.TenantId.IsEmpty)
        {
            return CanonicalEventErrors.MissingTenant;
        }

        if (!CanonicalEventSchemas.IsKnownType(metadata.Type))
        {
            return CanonicalEventErrors.UnknownType;
        }

        if (!CanonicalEventSchemas.IsSupported(metadata.Type, metadata.SchemaVersion))
        {
            return CanonicalEventErrors.UnknownSchemaVersion;
        }

        if (!metadata.Source.IsWellFormed)
        {
            return CanonicalEventErrors.InvalidSource;
        }

        if (!metadata.WorkObject.IsWellFormed)
        {
            return CanonicalEventErrors.InvalidObject;
        }

        if (metadata.SourceEventId is not null && !EventText.IsIdentifier(metadata.SourceEventId))
        {
            return CanonicalEventErrors.InvalidSourceEventId;
        }

        if (metadata.SourceRevision is < 0)
        {
            return CanonicalEventErrors.InvalidSourceRevision;
        }

        var timesAreUsable = IsUsableInstant(metadata.ReceivedAtUtc)
            && IsUsableOptionalInstant(metadata.OccurredAtUtc)
            && IsUsableOptionalInstant(metadata.SourceUpdatedAtUtc)
            && IsUsableOptionalInstant(metadata.ProcessedAtUtc);
        if (!timesAreUsable)
        {
            return CanonicalEventErrors.InvalidTime;
        }

        if (metadata.CorrelationId.IsEmpty)
        {
            return CanonicalEventErrors.MissingCorrelation;
        }

        if (metadata.CausationId == Guid.Empty)
        {
            return CanonicalEventErrors.InvalidCausation;
        }

        return null;
    }

    private static Error? ValidateContent(CanonicalEventContent content)
    {
        if (content.Changes.Count == 0)
        {
            return CanonicalEventErrors.MissingChanges;
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in content.Changes)
        {
            if (!change.IsWellFormed)
            {
                return CanonicalEventErrors.InvalidChange;
            }

            if (!change.HasValidValues)
            {
                return CanonicalEventErrors.InvalidValue;
            }

            if (!fields.Add(change.Field))
            {
                return CanonicalEventErrors.DuplicateField;
            }
        }

        if (content.Actor is not null && !content.Actor.IsWellFormed)
        {
            return CanonicalEventErrors.InvalidActor;
        }

        if (content.SourceReference is not null && !EventText.IsReference(content.SourceReference))
        {
            return CanonicalEventErrors.InvalidSourceReference;
        }

        return null;
    }

    // The minimum and maximum representable instants are what an unset or overflowed timestamp turns
    // into; a real source or receipt time is never either.
    private static bool IsUsableInstant(DateTimeOffset value) =>
        value != DateTimeOffset.MinValue && value != DateTimeOffset.MaxValue;

    private static bool IsUsableOptionalInstant(DateTimeOffset? value) =>
        value is null || IsUsableInstant(value.Value);
}
