using System.Buffers.Text;
using System.Text;
using WorkContinuity.Connectors.Monday;

namespace WorkContinuity.Harness.Monday.Secrets;

/// <summary>
/// How the Key Vault adapter represents the connector's opaque values as vault text (owner decision S-3).
/// </summary>
/// <remarks>
/// <para>
/// Every value the adapter writes is base64url text without padding, marked with one exact content type, so it comes
/// back byte for byte whatever its bytes are. A value of more than <see cref="MaxValueBytes"/> bytes is never encoded:
/// that bound is a supported compatibility subset, chosen to fit the documented 25 KB vault limit once encoded, and it
/// does not cover every value the connector can produce.
/// </para>
/// <para>
/// On read, a value carrying the Fully content type is decoded strictly and is unavailable when it does not decode; it
/// never falls back to plain text. A value with no content type is plain text, and only under the two names the product
/// owner places by hand: the client secret and the Signing Secret. Under any other name it is unavailable, and so is a
/// value with any other content type, the empty one included.
/// </para>
/// </remarks>
public static class KeyVaultSecretCodec
{
    /// <summary>The one content type of every value the adapter writes. Compared exactly and ordinally.</summary>
    public const string ContentType = "application/vnd.wce.opaque-bytes+base64url;v=1";

    /// <summary>The largest value, in bytes, that the adapter writes or returns.</summary>
    public const int MaxValueBytes = 18_000;

    /// <summary>The length of the base64url text of <see cref="MaxValueBytes"/> bytes.</summary>
    public const int MaxEncodedLength = 24_000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The text to store for a value, or null when the value is larger than the supported subset.</summary>
    /// <param name="value">The connector's opaque value.</param>
    public static string? Encode(ReadOnlySpan<byte> value) =>
        value.Length > MaxValueBytes ? null : Base64Url.EncodeToString(value);

    /// <summary>Reads a stored value back into the connector's bytes.</summary>
    /// <param name="name">The name the value was read under.</param>
    /// <param name="text">The stored text.</param>
    /// <param name="contentTypePresent">Whether the vault reported a content type at all.</param>
    /// <param name="contentType">The reported content type, when present.</param>
    /// <returns>The value, or unavailable; never missing, because the vault did answer with a value.</returns>
    public static MondaySecretLookup Decode(MondaySecretName name, string text, bool contentTypePresent, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(text);

        if (contentTypePresent)
        {
            return string.Equals(contentType, ContentType, StringComparison.Ordinal)
                ? DecodeMarked(text)
                : MondaySecretLookup.Unavailable();
        }

        return IsOwnerPlaced(name) ? DecodePlain(text) : MondaySecretLookup.Unavailable();
    }

    /// <summary>Whether the name is one of the two values the product owner places as plain text.</summary>
    /// <param name="name">The secret's name.</param>
    public static bool IsOwnerPlaced(MondaySecretName name) =>
        name.Equals(MondaySecretName.ClientSecret) || name.Equals(MondaySecretName.SigningSecret);

    private static MondaySecretLookup DecodeMarked(string text)
    {
        if (text.Length > MaxEncodedLength || text.Length % 4 == 1)
        {
            return MondaySecretLookup.Unavailable();
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return MondaySecretLookup.Unavailable();
            }
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return MondaySecretLookup.Unavailable();
        }

        // Canonical only: text whose unused trailing bits are set decodes, but is not what the adapter writes.
        return bytes.Length <= MaxValueBytes && string.Equals(Base64Url.EncodeToString(bytes), text, StringComparison.Ordinal)
            ? MondaySecretLookup.Found(bytes)
            : MondaySecretLookup.Unavailable();
    }

    private static MondaySecretLookup DecodePlain(string text)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return MondaySecretLookup.Unavailable();
        }

        return bytes.Length <= MaxValueBytes ? MondaySecretLookup.Found(bytes) : MondaySecretLookup.Unavailable();
    }
}
