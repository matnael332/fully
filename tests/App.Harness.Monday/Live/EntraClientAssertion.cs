using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace WorkContinuity.Harness.Monday.Live;

/// <summary>
/// Stage B2 (owner decision B-10): the hand-written certificate assertion of the Microsoft identity platform, a JWT the
/// client signs with its own certificate in place of a client secret. Authored offline only.
/// </summary>
/// <remarks>
/// <para>
/// The contract is Microsoft's "certificate credentials" documentation, read on 2026-10-06: header <c>alg</c> PS256,
/// <c>typ</c> JWT and <c>x5t#S256</c>, the base64url SHA-256 thumbprint of the certificate's DER encoding; claims
/// <c>aud</c>, the tenant's v2.0 token endpoint, <c>iss</c> and <c>sub</c>, the client id, a fresh GUID <c>jti</c>,
/// <c>nbf</c> and <c>iat</c> at the current time, and <c>exp</c> a short time later (the documentation says 5 to 10 minutes
/// at most; this uses five); an RSA signature with PSS padding over SHA-256.
/// </para>
/// <para>
/// Only an RSA certificate with its private key, of at least 2,048 bits, inside its validity period, is used. Anything
/// else fails closed. Nothing here keeps, logs or renders the assertion or the key.
/// </para>
/// </remarks>
internal static class EntraClientAssertion
{
    /// <summary>The algorithm the documentation names.</summary>
    internal const string Algorithm = "PS256";

    /// <summary>How long an assertion is valid after it is issued.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The smallest RSA key accepted.</summary>
    internal const int MinimumKeySize = 2048;

    /// <summary>
    /// Whether a certificate can sign assertions at all: an RSA certificate with its private key, of at least the minimum
    /// size. Its validity period is checked separately, at the time of each use.
    /// </summary>
    internal static bool IsSuitable(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            return false;
        }

        try
        {
            using var key = certificate.GetRSAPrivateKey();
            return key is not null && key.KeySize >= MinimumKeySize;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Whether a suitable certificate is inside its validity period at the given time.</summary>
    internal static bool IsValidAt(X509Certificate2 certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var instant = now.UtcDateTime;
        return instant >= certificate.NotBefore.ToUniversalTime() && instant < certificate.NotAfter.ToUniversalTime();
    }

    /// <summary>Creates a signed assertion, or null when the certificate cannot sign one at this time.</summary>
    /// <param name="audience">The tenant's token endpoint.</param>
    /// <param name="client">The client (application) id, as issuer and subject.</param>
    /// <param name="certificate">The client's certificate, with its private key.</param>
    /// <param name="now">The issue time.</param>
    /// <param name="id">The assertion's unique id.</param>
    internal static string? Create(Uri audience, Guid client, X509Certificate2 certificate, DateTimeOffset now, Guid id)
    {
        ArgumentNullException.ThrowIfNull(audience);
        ArgumentNullException.ThrowIfNull(certificate);
        if (client == Guid.Empty || id == Guid.Empty || !IsSuitable(certificate) || !IsValidAt(certificate, now))
        {
            return null;
        }

        try
        {
            using var key = certificate.GetRSAPrivateKey();
            if (key is null)
            {
                return null;
            }

            var issued = now.ToUnixTimeSeconds();
            var header = Base64Url.EncodeToString(Json(writer =>
            {
                writer.WriteString("alg", Algorithm);
                writer.WriteString("typ", "JWT");
                writer.WriteString("x5t#S256", Base64Url.EncodeToString(SHA256.HashData(certificate.RawData)));
            }));
            var claims = Base64Url.EncodeToString(Json(writer =>
            {
                writer.WriteString("aud", audience.AbsoluteUri);
                writer.WriteString("iss", client.ToString("D"));
                writer.WriteString("sub", client.ToString("D"));
                writer.WriteString("jti", id.ToString("D"));
                writer.WriteNumber("nbf", issued);
                writer.WriteNumber("iat", issued);
                writer.WriteNumber("exp", issued + (long)Lifetime.TotalSeconds);
            }));

            var signed = header + "." + claims;
            var signature = key.SignData(System.Text.Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return signed + "." + Base64Url.EncodeToString(signature);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
