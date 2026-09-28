using System.Globalization;

namespace WorkContinuity.Core.Events;

/// <summary>
/// Text rules shared by the canonical event contract.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of text reach an event. <em>Tokens</em> are the product's own vocabulary — source
/// systems, object types, field names, event types and schema versions — and are restricted to
/// lowercase ASCII so they are stable, comparable keys.
/// </para>
/// <para>
/// <em>Identifiers</em> and <em>references</em> come from providers, so they are accepted as opaque
/// text, but they must be bounded, carry no leading or trailing whitespace, and contain no control,
/// format or line-separator character. They end up in keys and in safe metadata logs, where an
/// invisible or line-breaking character could forge, split or hide an entry.
/// </para>
/// </remarks>
internal static class EventText
{
    /// <summary>The maximum length of a token.</summary>
    internal const int MaxTokenLength = 64;

    /// <summary>
    /// The maximum length of a provider identifier. Matches the durable receipt columns, so anything
    /// the inbox can store an event can carry.
    /// </summary>
    internal const int MaxIdentifierLength = 200;

    /// <summary>The maximum length of an opaque provider reference such as a record link.</summary>
    internal const int MaxReferenceLength = 2048;

    /// <summary>Whether the value is a token: 1-64 lowercase ASCII letters, digits, '-', '_' or '.'.</summary>
    internal static bool IsToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTokenLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var allowed = char.IsAsciiLetterLower(character)
                || char.IsAsciiDigit(character)
                || character is '-' or '_' or '.';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the value is an acceptable provider identifier.</summary>
    internal static bool IsIdentifier(string? value) => IsOpaqueText(value, MaxIdentifierLength);

    /// <summary>Whether the value is an acceptable opaque provider reference.</summary>
    internal static bool IsReference(string? value) => IsOpaqueText(value, MaxReferenceLength);

    private static bool IsOpaqueText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        foreach (var character in value)
        {
            var category = char.GetUnicodeCategory(character);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }

        return true;
    }
}
