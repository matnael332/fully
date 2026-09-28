namespace WorkContinuity.Core.Events;

/// <summary>
/// The closed registry of canonical event types and the schema versions supported for each.
/// </summary>
/// <remarks>
/// <para>
/// An event whose type is unknown is quarantined as bad schema; an event of a known type whose schema
/// version is not supported is quarantined as an unknown schema version (architecture §3). The registry
/// is code, so supporting a new type or version is a reviewed change.
/// </para>
/// <para>
/// Gate 2 needs exactly one type. <see cref="ObjectChangedType"/> reports before/after changes to the
/// fields of one work object, which is what the field-level Snapshot is projected from.
/// </para>
/// </remarks>
public static class CanonicalEventSchemas
{
    /// <summary>The type of an event that changes fields of one work object.</summary>
    public const string ObjectChangedType = "workcontinuity.object.changed";

    /// <summary>Version 1 of the <see cref="ObjectChangedType"/> schema.</summary>
    public const string ObjectChangedVersion1 = "1";

    private static readonly Dictionary<string, string[]> SupportedVersions = new(StringComparer.Ordinal)
    {
        [ObjectChangedType] = [ObjectChangedVersion1],
    };

    /// <summary>Gets every known canonical event type.</summary>
    public static IReadOnlyCollection<string> KnownTypes { get; } = SupportedVersions.Keys.ToArray();

    /// <summary>Whether the type is a known canonical event type.</summary>
    public static bool IsKnownType(string? type) =>
        type is not null && SupportedVersions.ContainsKey(type);

    /// <summary>Whether the schema version is supported for the type.</summary>
    public static bool IsSupported(string? type, string? schemaVersion) =>
        type is not null
        && schemaVersion is not null
        && SupportedVersions.TryGetValue(type, out var versions)
        && Array.IndexOf(versions, schemaVersion) >= 0;

    /// <summary>Gets the schema versions supported for a known type.</summary>
    /// <exception cref="ArgumentException">The type is not a known canonical event type.</exception>
    public static IReadOnlyCollection<string> VersionsOf(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return SupportedVersions.TryGetValue(type, out var versions)
            ? Array.AsReadOnly(versions)
            : throw new ArgumentException("The type is not a known canonical event type.", nameof(type));
    }
}
