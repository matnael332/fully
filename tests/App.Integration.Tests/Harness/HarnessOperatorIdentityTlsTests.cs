using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Composition;
using WorkContinuity.Harness.Monday.Configuration;
using WorkContinuity.Harness.Monday.Operator;
using WorkContinuity.Harness.Monday.Trust;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, Stage B4: the operator's honest DEV/TEST identity (owner decision B-12) and the standalone run's ephemeral
/// loopback certificate (owner decision B-13). Every certificate here is created in memory and disposed; no certificate
/// store is written, no provider is contacted, and every connection stays on the loopback address.
/// </summary>
public sealed class HarnessOperatorIdentityTests
{
    [Fact]
    public void The_operator_is_the_harness_s_own_dev_test_identity_and_not_the_local_demo_provider()
    {
        var options = HarnessTestHost.OptionsFor(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused", TenantId.New(), UserId.New(), HarnessOptions.OperatorCallbackPort);
        var trust = new HarnessTrust(options);

        Assert.True(trust.Operator.IsAuthenticated);
        Assert.Equal(options.CanonicalUser, trust.Operator.UserId);
        var identity = Assert.IsType<ExternalIdentity>(trust.Operator.ExternalIdentity);
        Assert.Equal("wce-dev-harness", identity.Provider.Value);
        Assert.Equal(IdentityProviderId.From(HarnessTrust.OperatorProviderKey), identity.Provider);
        Assert.NotEqual(IdentityProviderId.LocalDemo, identity.Provider);
        Assert.NotEqual(IdentityProviderId.EntraId, identity.Provider);
        Assert.Equal("wce-harness-operator", identity.Subject);
    }

    [Theory]
    [InlineData("attest", true)]
    [InlineData("Attest", false)]
    [InlineData("ATTEST", false)]
    [InlineData(" attest", false)]
    [InlineData("attest ", false)]
    [InlineData("attest\t", false)]
    [InlineData("attested", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_exact_answer_is_the_attestation(string? line, bool given) =>
        Assert.Equal(given, OperatorAttestation.IsGiven(line));

    [Fact]
    public void The_attestation_says_dev_test_only_names_the_honest_provider_and_names_no_tenant_or_user()
    {
        var statement = string.Join('\n', OperatorAttestation.Statement);

        Assert.Equal(2, OperatorAttestation.Statement.Count);
        Assert.All(OperatorAttestation.Statement, line => Assert.StartsWith("harness: ", line, StringComparison.Ordinal));
        Assert.Contains("DEV/TEST only", statement, StringComparison.Ordinal);
        Assert.Contains("'wce-dev-harness'", statement, StringComparison.Ordinal);
        Assert.Contains("never the production trust model", statement, StringComparison.Ordinal);
        Assert.Contains("type 'attest'", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("local-demo", statement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", statement);
    }
}

/// <summary>Gate 3G, Stage B4: the certificate itself, created in memory, examined and disposed (owner decision B-13).</summary>
public sealed class HarnessOperatorCertificateTests
{
    private static readonly StoreName[] Stores = [StoreName.My, StoreName.Root, StoreName.CertificateAuthority, StoreName.TrustedPeople];

    [Fact]
    public void Each_run_gets_a_new_self_signed_server_certificate_for_the_loopback_address_only_valid_one_day_at_most()
    {
        var now = DateTimeOffset.UtcNow;
        using var first = OperatorTlsCertificate.Create(now);
        using var second = OperatorTlsCertificate.Create(now);

        // Its own key, and a new one each run.
        Assert.True(first.HasPrivateKey);
        Assert.NotEqual(first.GetCertHashString(HashAlgorithmName.SHA256), second.GetCertHashString(HashAlgorithmName.SHA256));
        Assert.NotEqual(first.GetPublicKeyString(), second.GetPublicKeyString());
        using (var key = first.GetRSAPublicKey())
        {
            Assert.Equal(2048, Assert.IsAssignableFrom<RSA>(key).KeySize);
        }

        // Self-signed, signed with SHA-256, named for what it is.
        Assert.Equal("CN=wce-dev-harness operator DEV-TEST ephemeral", first.Subject);
        Assert.Equal(first.Subject, first.Issuer);
        Assert.Equal("1.2.840.113549.1.1.11", first.SignatureAlgorithm.Value);

        // Valid from just before now, for one day at most.
        var notBefore = first.NotBefore.ToUniversalTime();
        var notAfter = first.NotAfter.ToUniversalTime();
        Assert.InRange(notBefore, now.UtcDateTime.AddMinutes(-2), now.UtcDateTime);
        Assert.InRange(notAfter - notBefore, TimeSpan.FromHours(23), TimeSpan.FromDays(1));
        Assert.True(notAfter > now.UtcDateTime);

        // Exactly one name: the IPv4 loopback address. No host name, no other address of any kind.
        var san = Assert.Single(first.Extensions.Cast<X509Extension>(), extension => extension.Oid?.Value == "2.5.29.17");
        Assert.False(san.Critical);
        var names = new AsnReader(san.RawData, AsnEncodingRules.DER).ReadSequence();
        var ipTag = new Asn1Tag(TagClass.ContextSpecific, 7);
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, names.ReadOctetString(ipTag));
        Assert.False(names.HasData);
        var parsed = new X509SubjectAlternativeNameExtension(san.RawData);
        Assert.Equal([IPAddress.Loopback], parsed.EnumerateIPAddresses());
        Assert.Empty(parsed.EnumerateDnsNames());

        // An end-entity TLS server certificate and nothing more.
        var constraints = Assert.Single(first.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.False(constraints.CertificateAuthority);
        Assert.True(constraints.Critical);
        var usage = Assert.Single(first.Extensions.OfType<X509KeyUsageExtension>());
        Assert.Equal(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, usage.KeyUsages);
        Assert.True(usage.Critical);
        var purposes = Assert.Single(first.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Equal(["1.3.6.1.5.5.7.3.1"], purposes.EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value));
    }

    [Fact]
    public void The_fingerprint_is_the_sha_256_of_the_certificate_in_the_form_browsers_show()
    {
        using var certificate = OperatorTlsCertificate.Create(DateTimeOffset.UtcNow);
        var fingerprint = OperatorTlsCertificate.Fingerprint(certificate);

        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);
        Assert.Equal(certificate.GetCertHashString(HashAlgorithmName.SHA256), fingerprint.Replace(":", string.Empty, StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), fingerprint.Replace(":", string.Empty, StringComparison.Ordinal));
        Assert.Throws<ArgumentNullException>(() => OperatorTlsCertificate.Fingerprint(null!));
    }

    [Fact]
    public void Creating_and_disposing_the_certificate_changes_no_certificate_store_and_the_machine_does_not_trust_it()
    {
        var before = StoreSnapshot();

        string thumbprint;
        using (var certificate = OperatorTlsCertificate.Create(DateTimeOffset.UtcNow))
        {
            thumbprint = certificate.Thumbprint;
            Assert.Equal(before, StoreSnapshot());
            Assert.All(before, state => Assert.DoesNotContain(thumbprint, state.Thumbprints, StringComparison.Ordinal));

            // The machine's trust is unchanged, so the chain does not build: the owner accepts the browser's warning once.
            // No revocation check and no download: nothing leaves the process.
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            Assert.False(chain.Build(certificate));
            Assert.Contains(chain.ChainStatus, status => status.Status == X509ChainStatusFlags.UntrustedRoot);
        }

        Assert.Equal(before, StoreSnapshot());
    }

    // The current user's and the machine's stores that could make a certificate trusted or findable, read only. A store
    // the platform does not have, or that does not exist, is recorded as absent and must stay absent.
    private static List<StoreState> StoreSnapshot()
    {
        var snapshot = new List<StoreState>();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            foreach (var name in Stores)
            {
                try
                {
                    using var store = new X509Store(name, location);
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    var thumbprints = store.Certificates.Select(certificate => certificate.Thumbprint).Order(StringComparer.Ordinal);
                    snapshot.Add(new StoreState($"{location}/{name}", true, string.Join(',', thumbprints)));
                }
                catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
                {
                    snapshot.Add(new StoreState($"{location}/{name}", false, string.Empty));
                }
            }
        }

        Assert.Contains(snapshot, state => state.Exists);
        return snapshot;
    }

    private sealed record StoreState(string Store, bool Exists, string Thumbprints);
}

/// <summary>Gate 3G, Stage B4: the harness host serves its operator listener with the ephemeral certificate (owner decision B-13).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessOperatorTlsHostTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_operator_listener_presents_the_ephemeral_certificate_whose_name_matches_the_loopback_address()
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);
        using var certificate = OperatorTlsCertificate.Create(DateTimeOffset.UtcNow);
        var fingerprint = OperatorTlsCertificate.Fingerprint(certificate);
        var seams = new HarnessSeams { OperatorCertificate = certificate, TransportPublisher = new InMemoryTransport(), LogSink = new CapturingHarnessSink() };

        HarnessHost? host = null;
        HarnessOptions? options = null;
        for (var attempt = 0; attempt < 5 && host is null; attempt++)
        {
            options = HarnessTestHost.OptionsFor(fixture.ApplicationConnectionString, tenant, user, HarnessTestHost.FreePort());
            var started = await HarnessHost.StartAsync(options, seams, Token);
            host = started.Host;
            if (host is null)
            {
                Assert.Equal(HarnessRefusal.ListenerUnavailable, started.Refusal);
            }
        }

        await using var running = Assert.IsType<HarnessHost>(host);
        var operatorPort = Assert.IsType<HarnessOptions>(options).OperatorPort;

        // A TLS client for 127.0.0.1: the listener presents exactly this run's certificate, and the only policy error is
        // the untrusted root; the name matches the loopback address, so the browser warns about trust only.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, operatorPort, Token);
        string? presented = null;
        SslPolicyErrors? errors = null;
        await using (var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "127.0.0.1",
                    CertificateChainPolicy = new X509ChainPolicy { RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true },
                    RemoteCertificateValidationCallback = (_, offered, _, policy) =>
                    {
                        presented = offered is null ? null : offered.GetCertHashString(HashAlgorithmName.SHA256);
                        errors = policy;
                        return presented is not null && string.Equals(presented, fingerprint.Replace(":", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
                    },
                },
                Token);
            Assert.True(tls.IsAuthenticated);
        }

        Assert.Equal(certificate.GetCertHashString(HashAlgorithmName.SHA256), presented);
        Assert.Equal(SslPolicyErrors.RemoteCertificateChainErrors, errors);

        // And it answers HTTP over that TLS: an unknown route is refused by the listener itself.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = new X509ChainPolicy { RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true },
                RemoteCertificateValidationCallback = (_, offered, _, _) =>
                    offered is not null && string.Equals(offered.GetCertHashString(HashAlgorithmName.SHA256), certificate.GetCertHashString(HashAlgorithmName.SHA256), StringComparison.Ordinal),
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(options!.OperatorOrigin) };
        using var response = await http.GetAsync(new Uri("/not-an-operator-route", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
