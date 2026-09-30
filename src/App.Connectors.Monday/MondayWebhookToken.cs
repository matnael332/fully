using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkContinuity.Connectors.Monday;

/// <summary>The one claim of a verified webhook token that intake uses: the monday account.</summary>
/// <param name="AccountId">monday's numeric account id, as decimal digits.</param>
internal sealed record MondayWebhookClaims(string AccountId)
{
    /// <summary>Names the type only; the account id is not rendered.</summary>
    public override string ToString() => "MondayWebhookClaims(account redacted)";
}

/// <summary>
/// Verifies the JWT monday puts in a webhook request's <c>Authorization</c> header, signed with the app's Signing Secret.
/// </summary>
/// <remarks>
/// <para>
/// monday documents that board-webhook tokens are signed with the app's Signing Secret, but not their JOSE algorithm.
/// HS256 is therefore a 3C compatibility assumption, which must be confirmed against a real monday board webhook in 3G:
/// the key is a symmetric secret, and monday's related examples sign with it as a standard JWT. The algorithm allowlist
/// is exactly <see cref="AllowedAlgorithm"/>; <c>none</c>, every asymmetric algorithm and every other HMAC are refused.
/// </para>
/// <para>
/// Verification uses the base class library only. The token is three canonical base64url segments without padding.
/// Before the signature is checked, the header is read only to require the allowlisted <c>alg</c>, refuse <c>crit</c>,
/// and allow <c>typ</c> only as JWT. The signature is recomputed over the first two segments and compared in constant
/// time, and only after it matches is any claim read.
/// </para>
/// <para>
/// <c>exp</c> is required and must not have passed; <c>nbf</c> and <c>iat</c>, when present, must not lie in the future;
/// each with <see cref="ClockTolerance"/>. <c>aud</c>, a string or an array of strings, must contain exactly the expected
/// audience, and <c>accountId</c>, a number or a string of decimal digits, must be present. No other claim is read:
/// <c>userId</c> is provider actor information, not an identity, and <c>shortLivedToken</c> is credential material that
/// is never extracted.
/// </para>
/// </remarks>
internal static class MondayWebhookToken
{
    /// <summary>The exact algorithm allowlist: this one value (a 3C compatibility assumption, confirmed in 3G).</summary>
    internal const string AllowedAlgorithm = "HS256";

    /// <summary>How far the token's times may disagree with the clock.</summary>
    internal static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(30);

    private const int SignatureLength = 32;
    private const int MaxAudiences = 16;
    private const int MaxAccountIdLength = 20;

    // 9999-12-31T23:59:59Z, the last second a DateTimeOffset can hold.
    private const double MaxNumericDate = 253_402_300_799;

    private static readonly JsonDocumentOptions Parsing = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    /// <summary>Verifies a token for one audience at one instant.</summary>
    /// <param name="token">The token, without any scheme.</param>
    /// <param name="key">The Signing Secret.</param>
    /// <param name="audience">The only audience accepted.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The token's claims, or null when it is not valid.</returns>
    internal static MondayWebhookClaims? Verify(string token, ReadOnlySpan<byte> key, string audience, DateTimeOffset now)
    {
        if (key.IsEmpty || string.IsNullOrEmpty(token) || token.Length > MondayWebhooks.MaxAuthorizationLength)
        {
            return null;
        }

        var first = token.IndexOf('.', StringComparison.Ordinal);
        var second = first < 0 ? -1 : token.IndexOf('.', first + 1);
        if (first <= 0 || second <= first + 1 || second == token.Length - 1 || token.IndexOf('.', second + 1) >= 0)
        {
            return null;
        }

        if (Decode(token.AsSpan(0, first)) is not { } header
            || Decode(token.AsSpan(first + 1, second - first - 1)) is not { } payload
            || Decode(token.AsSpan(second + 1)) is not { Length: SignatureLength } signature
            || !IsAcceptedHeader(header))
        {
            return null;
        }

        // Every character is base64url or a dot, so the signing input is exactly the token's ASCII.
        Span<byte> expected = stackalloc byte[SignatureLength];
        HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(token[..second]), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            return null;
        }

        return ReadClaims(payload, audience, now);
    }

    private static byte[]? Decode(ReadOnlySpan<char> segment)
    {
        foreach (var character in segment)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return null;
            }
        }

        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(segment);
        }
        catch (FormatException)
        {
            return null;
        }

        // Only the canonical encoding is accepted, so a token has exactly one spelling.
        return Base64Url.EncodeToString(decoded).AsSpan().SequenceEqual(segment) ? decoded : null;
    }

    private static bool IsAcceptedHeader(byte[] header)
    {
        try
        {
            using var document = JsonDocument.Parse(header, Parsing);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("alg", out var algorithm)
                && algorithm.ValueKind == JsonValueKind.String
                && algorithm.ValueEquals(AllowedAlgorithm)
                && !root.TryGetProperty("crit", out _)
                && (!root.TryGetProperty("typ", out var type)
                    || (type.ValueKind == JsonValueKind.String
                        && string.Equals(type.GetString(), "JWT", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static MondayWebhookClaims? ReadClaims(byte[] payload, string audience, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, Parsing);
            var claims = document.RootElement;
            if (claims.ValueKind != JsonValueKind.Object
                || !TryReadTime(claims, "exp", out var expires)
                || expires is null
                || !TryReadTime(claims, "nbf", out var notBefore)
                || !TryReadTime(claims, "iat", out var issuedAt))
            {
                return null;
            }

            var instant = now.ToUnixTimeMilliseconds() / 1000d;
            var tolerance = ClockTolerance.TotalSeconds;
            if (instant >= expires + tolerance
                || instant + tolerance < notBefore
                || issuedAt > instant + tolerance)
            {
                return null;
            }

            return HasAudience(claims, audience) && AccountId(claims) is { } account
                ? new MondayWebhookClaims(account)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // A NumericDate claim: absent, or a number of seconds between the epoch and the year 9999.
    private static bool TryReadTime(JsonElement claims, string name, out double? seconds)
    {
        seconds = null;
        if (!claims.TryGetProperty(name, out var value))
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var parsed)
            || !double.IsFinite(parsed)
            || parsed is < 0 or > MaxNumericDate)
        {
            return false;
        }

        seconds = parsed;
        return true;
    }

    private static bool HasAudience(JsonElement claims, string audience)
    {
        if (!claims.TryGetProperty("aud", out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.ValueEquals(audience);
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > MaxAudiences)
        {
            return false;
        }

        var found = false;
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            found |= entry.ValueEquals(audience);
        }

        return found;
    }

    private static string? AccountId(JsonElement claims)
    {
        if (!claims.TryGetProperty("accountId", out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };

        return text is { Length: > 0 and <= MaxAccountIdLength } && text.All(char.IsAsciiDigit) ? text : null;
    }
}
