namespace WorkContinuity.Core.Connectors;

/// <summary>
/// The provider-neutral form of an OAuth scope list: scope names separated by single spaces.
/// </summary>
/// <remarks>
/// A scope list names permissions, never a secret, so it may be stored in ordinary fields (Gate 3 decision D3B-2).
/// Each name follows the RFC 6749 scope-token syntax: visible ASCII without a double quote or a backslash. Which names a
/// connector may request or accept is the connector's own policy; this type only keeps the form well defined.
/// </remarks>
public static class ConnectorScopes
{
    /// <summary>The longest stored scope list.</summary>
    public const int MaxLength = 512;

    /// <summary>The longest single scope name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Whether the text is a well-formed scope list: distinct names separated by single spaces.</summary>
    public static bool IsWellFormed(string? scopes)
    {
        if (string.IsNullOrEmpty(scopes) || scopes.Length > MaxLength)
        {
            return false;
        }

        var names = scopes.Split(' ');
        return names.All(IsName) && names.Distinct(StringComparer.Ordinal).Count() == names.Length;
    }

    /// <summary>Splits a well-formed scope list into its names.</summary>
    /// <exception cref="ArgumentException">The list is not well formed.</exception>
    public static IReadOnlyList<string> Parse(string scopes) =>
        IsWellFormed(scopes)
            ? scopes.Split(' ')
            : throw new ArgumentException("The scope list is not well formed.", nameof(scopes));

    /// <summary>The canonical list of the given names: distinct and in ordinal order.</summary>
    /// <exception cref="ArgumentException">A name is malformed, or the list would be empty or too long.</exception>
    public static string Normalize(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var list = string.Join(' ', names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return IsWellFormed(list)
            ? list
            : throw new ArgumentException("The scope names do not form a well-formed list.", nameof(names));
    }

    private static bool IsName(string name) =>
        name.Length is > 0 and <= MaxNameLength
        && name.All(character => character is >= '!' and <= '~' and not '"' and not '\\');
}
