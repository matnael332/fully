namespace WorkContinuity.Core.Events;

/// <summary>
/// The expiring half of a canonical event: the work content itself.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is raw work-event content and expires by the 30-day limit (PRD §12 and Gate 2 owner
/// decision 5): the before/after changes, which are the event's domain payload, the actor, and the
/// opaque provider reference. Retained provenance lives only in <see cref="CanonicalEventMetadata"/>.
/// </para>
/// <para>
/// The change list is copied on construction, so the caller cannot alter an event after creating it.
/// Construction only guards against null; <see cref="CanonicalEvent.Create"/> decides validity.
/// </para>
/// </remarks>
public sealed class CanonicalEventContent
{
    /// <summary>Initializes a new instance of the <see cref="CanonicalEventContent"/> class.</summary>
    /// <exception cref="ArgumentNullException">The change list is null.</exception>
    /// <exception cref="ArgumentException">The change list contains a null entry.</exception>
    public CanonicalEventContent(IEnumerable<FieldChange> changes, EventActor? actor, string? sourceReference)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var copy = changes.ToArray();
        if (Array.Exists(copy, change => change is null))
        {
            throw new ArgumentException("A change list must not contain null entries.", nameof(changes));
        }

        Changes = Array.AsReadOnly(copy);
        Actor = actor;
        SourceReference = sourceReference;
    }

    /// <summary>Gets the field changes: the event's before/after change and domain payload.</summary>
    public IReadOnlyList<FieldChange> Changes { get; }

    /// <summary>Gets the actor, or null when the source reported none.</summary>
    public EventActor? Actor { get; }

    /// <summary>
    /// Gets an opaque provider reference kept for traceability, for example a record link. It is
    /// never used for authority or ordering.
    /// </summary>
    public string? SourceReference { get; }
}
