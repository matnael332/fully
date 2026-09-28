using WorkContinuity.Core.Events;

namespace WorkContinuity.Core.SourceOfTruth;

/// <summary>Whether a source is authoritative for a field.</summary>
public enum AuthorityDecision
{
    /// <summary>Unspecified. Never returned.</summary>
    None = 0,

    /// <summary>The source is authoritative for the field's data type.</summary>
    Authoritative = 1,

    /// <summary>The source is not authoritative for the field's data type.</summary>
    NotAuthoritative = 2,
}

/// <summary>
/// Decides, deterministically, whether an event's source is authoritative for each field it changes.
/// </summary>
/// <remarks>
/// <para>
/// The decision depends only on the field's data type, the event's source and the object's home, looked
/// up in <see cref="AuthorityTable"/>. It never depends on the actor, on receipt time or on any model
/// output: the actor may be unresolved without changing authority, and receipt order never decides
/// anything (invariants 12 and 21).
/// </para>
/// <para>
/// The resolver fails closed. A data type without a rule, a source kind that is not defined, or a
/// malformed source or object is never authoritative. What happens to a non-authoritative change —
/// quarantine as an authority conflict — is decided by the processing pipeline, not here.
/// </para>
/// </remarks>
public static class SourceOfTruthResolver
{
    /// <summary>Decides whether the source is authoritative for a field of the given data type.</summary>
    /// <exception cref="ArgumentNullException">The source or the work object is null.</exception>
    public static AuthorityDecision Resolve(FactDataType dataType, EventSource source, ObjectReference workObject)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(workObject);

        if (!source.IsWellFormed || !workObject.IsWellFormed)
        {
            return AuthorityDecision.NotAuthoritative;
        }

        var authoritative = AuthorityTable.BasisFor(dataType) switch
        {
            AuthorityBasis.SourceTaskSystem => IsHomeConnector(source, workObject),
            AuthorityBasis.DefiningSystem => IsHomeConnector(source, workObject),
            AuthorityBasis.LocalAgent => source.Kind == EventSourceKind.LocalAgent,
            AuthorityBasis.Employee => source.Kind == EventSourceKind.Employee,
            AuthorityBasis.OrganizationIdentitySystem => source.Kind == EventSourceKind.IdentityProvider,
            AuthorityBasis.AuthoritativeDestination => source.Kind == EventSourceKind.ActionExecutor,
            _ => false,
        };

        return authoritative ? AuthorityDecision.Authoritative : AuthorityDecision.NotAuthoritative;
    }

    /// <summary>
    /// Decides authority for every change of a validated event, in the order the changes appear.
    /// </summary>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    public static IReadOnlyList<AuthorityDecision> ResolveChanges(CanonicalEvent canonicalEvent)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);

        var metadata = canonicalEvent.Metadata;
        var changes = canonicalEvent.Content.Changes;
        var decisions = new AuthorityDecision[changes.Count];
        for (var index = 0; index < changes.Count; index++)
        {
            decisions[index] = Resolve(changes[index].DataType, metadata.Source, metadata.WorkObject);
        }

        return Array.AsReadOnly(decisions);
    }

    // The owning system is authoritative only through the object's own connection: another connection
    // of the same system, or any other kind of source, is reporting on an object it does not own.
    private static bool IsHomeConnector(EventSource source, ObjectReference workObject) =>
        source.Kind == EventSourceKind.SaasConnector
        && string.Equals(source.SourceSystem, workObject.SourceSystem, StringComparison.Ordinal)
        && string.Equals(source.ConnectionReference, workObject.ConnectionReference, StringComparison.Ordinal);
}
