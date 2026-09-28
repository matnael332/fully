using WorkContinuity.Core.Identity;

namespace WorkContinuity.Core.Events;

/// <summary>
/// A work object as identified by its home system: the system and connection that own it, its type and
/// the provider's id for it.
/// </summary>
/// <remarks>
/// The home system and connection are the object's owner, and for task status and deadlines the owner
/// is the Source of Truth (PRD §5). Keeping them on the reference, separately from the event's own
/// source, is what lets a report about an object from any other source be recognised as
/// non-authoritative.
/// </remarks>
public sealed record ObjectReference
{
    /// <summary>Initializes a new instance of the <see cref="ObjectReference"/> class.</summary>
    /// <exception cref="ArgumentNullException">A component is null.</exception>
    public ObjectReference(string sourceSystem, string connectionReference, string objectType, string sourceObjectId)
    {
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(connectionReference);
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentNullException.ThrowIfNull(sourceObjectId);

        SourceSystem = sourceSystem;
        ConnectionReference = connectionReference;
        ObjectType = objectType;
        SourceObjectId = sourceObjectId;
    }

    /// <summary>Gets the token of the system that owns the object.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the connection through which the owning system is reached.</summary>
    public string ConnectionReference { get; }

    /// <summary>Gets the object type token, for example "task".</summary>
    public string ObjectType { get; }

    /// <summary>Gets the provider's identifier for the object.</summary>
    public string SourceObjectId { get; }

    /// <summary>Gets a value indicating whether every part is well formed.</summary>
    internal bool IsWellFormed =>
        EventText.IsToken(SourceSystem)
        && EventText.IsIdentifier(ConnectionReference)
        && EventText.IsToken(ObjectType)
        && EventText.IsIdentifier(SourceObjectId);
}

/// <summary>
/// The actor behind an event: their id as the source reported it, and the canonical user only when the
/// mapping was resolved deterministically.
/// </summary>
/// <remarks>
/// <para>
/// An actor is either resolved or explicitly unresolved. There is no way to derive a canonical user
/// from the source id here: identity mapping is a deterministic lookup, never a guess and never an AI
/// decision (invariant 19). An unresolved actor does not by itself make an event invalid or
/// quarantined; the fact is preserved and the actor stays marked unresolved.
/// </para>
/// <para>
/// The actor is work content, not provenance, so it lives in <see cref="CanonicalEventContent"/> and
/// expires with it.
/// </para>
/// </remarks>
public sealed record EventActor
{
    private EventActor(string sourceActorId, UserId? canonicalUserId)
    {
        SourceActorId = sourceActorId;
        CanonicalUserId = canonicalUserId;
    }

    /// <summary>Gets the actor's id as the source reported it.</summary>
    public string SourceActorId { get; }

    /// <summary>Gets the canonical user, or null while the actor is unresolved.</summary>
    public UserId? CanonicalUserId { get; }

    /// <summary>Gets a value indicating whether the actor was resolved to a canonical user.</summary>
    public bool IsResolved => CanonicalUserId is not null;

    /// <summary>Gets a value indicating whether the source id is well formed.</summary>
    internal bool IsWellFormed => EventText.IsIdentifier(SourceActorId);

    /// <summary>Creates an actor whose canonical user is not known.</summary>
    /// <exception cref="ArgumentNullException">The source actor id is null.</exception>
    public static EventActor Unresolved(string sourceActorId)
    {
        ArgumentNullException.ThrowIfNull(sourceActorId);
        return new EventActor(sourceActorId, canonicalUserId: null);
    }

    /// <summary>Creates an actor deterministically resolved to a canonical user.</summary>
    /// <exception cref="ArgumentNullException">The source actor id is null.</exception>
    /// <exception cref="ArgumentException">The canonical user id is empty.</exception>
    public static EventActor Resolved(string sourceActorId, UserId canonicalUserId)
    {
        ArgumentNullException.ThrowIfNull(sourceActorId);
        if (canonicalUserId.IsEmpty)
        {
            throw new ArgumentException(
                "A resolved actor requires a canonical user id.",
                nameof(canonicalUserId));
        }

        return new EventActor(sourceActorId, canonicalUserId);
    }
}
