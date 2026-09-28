using System.Buffers;
using System.Globalization;
using System.Text;

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
/// text, but they must be bounded, well-formed (no lone surrogate), carry no leading or trailing
/// whitespace, and contain no control, format or line-separator character, whichever plane it comes
/// from. They end up in keys, in the database and in safe metadata logs, where malformed text cannot be
/// stored and an invisible or line-breaking character could forge, split or hide an entry.
/// </para>
/// </remarks>
public static class EventText
{
    /// <summary>The maximum length of a token.</summary>
    public const int MaxTokenLength = 64;

    /// <summary>
    /// The maximum length of a provider identifier. Matches the durable receipt columns, so anything
    /// the inbox can store an event can carry.
    /// </summary>
    public const int MaxIdentifierLength = 200;

    /// <summary>The maximum length of an opaque provider reference such as a record link.</summary>
    public const int MaxReferenceLength = 2048;

    /// <summary>Whether the value is a token: 1-64 lowercase ASCII letters, digits, '-', '_' or '.'.</summary>
    public static bool IsToken(string? value)
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
    public static bool IsIdentifier(string? value) => IsOpaqueText(value, MaxIdentifierLength);

    /// <summary>Whether the value is an acceptable opaque provider reference.</summary>
    public static bool IsReference(string? value) => IsOpaqueText(value, MaxReferenceLength);

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

        // Whole code points, not UTF-16 units: a lone surrogate is malformed text that no UTF-8 store
        // accepts, and an invisible character outside the Basic Multilingual Plane (a tag character,
        // for example) arrives as a surrogate pair whose halves would otherwise look harmless.
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }
}
