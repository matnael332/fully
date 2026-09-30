using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// PKCE (RFC 7636) as monday's New OAuth Flow requires it: a random verifier and its S256 challenge.
/// </summary>
/// <remarks>
/// The verifier is 32 bytes from the operating system's CSPRNG, base64url-encoded into 43 unreserved characters, the
/// length RFC 7636 recommends and the lower bound of monday's 43–128. It is secret material (decision D3B-2): it lives
/// only in the connector's secret storage between the start of an authorization and its code exchange. The challenge,
/// its unpadded base64url SHA-256, is public and travels in the authorization request. monday accepts only
/// <c>S256</c>; a plain challenge is never produced.
/// </remarks>
public static class MondayPkce
{
    /// <summary>The only challenge method monday accepts.</summary>
    public const string ChallengeMethod = "S256";

    /// <summary>The length of a generated verifier.</summary>
    public const int VerifierLength = 43;

    /// <summary>The shortest verifier RFC 7636 and monday accept.</summary>
    public const int MinVerifierLength = 43;

    /// <summary>The longest verifier RFC 7636 and monday accept.</summary>
    public const int MaxVerifierLength = 128;

    /// <summary>Whether the text is a well-formed verifier: 43–128 unreserved characters.</summary>
    public static bool IsVerifier(string? value) =>
        value is { Length: >= MinVerifierLength and <= MaxVerifierLength }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~');

    /// <summary>The S256 challenge of a verifier: the unpadded base64url SHA-256 of its ASCII bytes.</summary>
    /// <exception cref="ArgumentException">The verifier is malformed.</exception>
    public static string ChallengeOf(string verifier) =>
        IsVerifier(verifier)
            ? Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            : throw new ArgumentException("A PKCE verifier must be 43-128 unreserved characters.", nameof(verifier));

    /// <summary>A new verifier from the operating system's CSPRNG.</summary>
    internal static string CreateVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
