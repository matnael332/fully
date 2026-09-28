namespace WorkContinuity.Core.Events;

/// <summary>
/// The canonical identifier of one canonical event. It is the CloudEvents <c>id</c> attribute.
/// </summary>
/// <remarks>
/// This is the product's own id, assigned when an event is produced. It is distinct from the
/// provider's event id (<see cref="CanonicalEventMetadata.SourceEventId"/>), which deduplication uses
/// within a tenant and connection when a source supplies one.
/// </remarks>
public readonly record struct CanonicalEventId
{
    private readonly Guid _value;

    private CanonicalEventId(Guid value) => _value = value;

    /// <summary>Gets the underlying value.</summary>
    public Guid Value => _value;

    /// <summary>Gets a value indicating whether this is the unset default.</summary>
    public bool IsEmpty => _value == Guid.Empty;

    /// <summary>Creates a new, time-ordered identifier.</summary>
    public static CanonicalEventId New() => new(Guid.CreateVersion7());

    /// <summary>Creates an identifier from an existing value.</summary>
    /// <exception cref="ArgumentException">The value is the empty GUID.</exception>
    public static CanonicalEventId From(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("A canonical event id must not be an empty GUID.", nameof(value))
        : new CanonicalEventId(value);

    /// <inheritdoc />
    public override string ToString() => _value.ToString("D");
}
