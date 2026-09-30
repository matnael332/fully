using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// The OAuth <c>state</c> of one started authorization: the transaction id and a 256-bit secret.
/// </summary>
/// <remarks>
/// <para>
/// The form is the transaction id as 32 lowercase hex digits, a dot, and 32 CSPRNG bytes as 43 base64url characters. The
/// id tells the callback which transaction to check; the secret proves the response belongs to the browser that started
/// it (anti-CSRF). Only the secret's SHA-256 is stored, so a database reader cannot forge a state, and the comparison
/// runs in constant time.
/// </para>
/// <para>
/// Parsing is strict: anything but the exact form names no transaction, and nothing about it reaches the database.
/// </para>
/// </remarks>
internal static class MondayAuthorizationState
{
    private const int IdLength = 32;
    private const int SecretLength = 43;

    /// <summary>A new state for the transaction, and the SHA-256 of its secret part.</summary>
    internal static (string State, byte[] SecretHash) Create(Guid transactionId)
    {
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (transactionId.ToString("N") + "." + secret, HashOf(secret));
    }

    /// <summary>Reads a state returned by the provider. False unless it has exactly the form this type creates.</summary>
    internal static bool TryParse(string? state, out Guid transactionId, out byte[] secretHash)
    {
        transactionId = Guid.Empty;
        secretHash = [];

        if (state is not { Length: IdLength + 1 + SecretLength } || state[IdLength] != '.')
        {
            return false;
        }

        var id = state.AsSpan(0, IdLength);
        var secret = state.AsSpan(IdLength + 1);

        if (!IsLowercaseHex(id)
            || !Guid.TryParseExact(id, "N", out transactionId)
            || transactionId == Guid.Empty
            || !IsBase64Url(secret))
        {
            transactionId = Guid.Empty;
            return false;
        }

        secretHash = HashOf(secret.ToString());
        return true;
    }

    private static byte[] HashOf(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    private static bool IsBase64Url(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowercaseHex(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character) && character is < 'a' or > 'f')
            {
                return false;
            }
        }

        return !text.IsEmpty;
    }
}
