using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WorkContinuity.Harness.Monday.Operator;

/// <summary>
/// The operator listener's TLS certificate for one standalone run (owner decision B-13): self-signed, created in memory
/// when the run starts, named for the loopback address only, valid for at most one day, and never trusted by the machine.
/// </summary>
/// <remarks>
/// <para>
/// The certificate is not a credential of any provider: it only lets the owner's browser reach the loopback operator
/// listener over HTTPS, which the production redirect rules require. The owner compares its SHA-256 fingerprint, printed
/// before the listener starts, with the one the browser shows, and accepts the browser's warning once. Nothing is added
/// to a certificate store, and no store is read (owner decision D3G-5).
/// </para>
/// <para>
/// The platform's TLS server cannot use a key that lives only in process memory, so the new certificate is reloaded
/// through an in-memory PKCS#12 copy with the default key storage: the private key then sits in a per-user key container
/// of its own while the certificate is in use, and is deleted when the certificate is disposed at the end of the run. The
/// PKCS#12 bytes are cleared as soon as they are loaded, and the key is never exportable or persisted.
/// </para>
/// </remarks>
public static class OperatorTlsCertificate
{
    /// <summary>How long the certificate is valid, from its start: one day, the most owner decision B-13 allows.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(1);

    /// <summary>How far before its creation the certificate's validity starts, so a browser on the same clock accepts it at once.</summary>
    public static readonly TimeSpan Backdating = TimeSpan.FromMinutes(1);

    /// <summary>The size of the certificate's RSA key, in bits.</summary>
    public const int KeySize = 2048;

    /// <summary>The certificate's subject: what it is, never a host name.</summary>
    public const string Subject = "CN=wce-dev-harness operator DEV-TEST ephemeral";

    /// <summary>The TLS server authentication purpose, the certificate's only extended key usage.</summary>
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>Creates a new certificate for one run. The caller disposes it when the run ends.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The certificate, with its private key.</returns>
    public static X509Certificate2 Create(DateTimeOffset now)
    {
        using var key = RSA.Create(KeySize);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // The loopback address is the certificate's one name: no host name, no other address.
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], critical: false));

        var notBefore = now - Backdating;
        using var created = request.CreateSelfSigned(notBefore, notBefore + Validity);

        var pkcs12 = created.Export(X509ContentType.Pfx);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pkcs12, password: null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    /// <summary>The certificate's SHA-256 fingerprint, as browsers show it: upper-case hexadecimal pairs separated by colons.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The fingerprint.</returns>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Join(':', Convert.ToHexString(SHA256.HashData(certificate.RawData)).Chunk(2).Select(pair => new string(pair)));
    }
}
