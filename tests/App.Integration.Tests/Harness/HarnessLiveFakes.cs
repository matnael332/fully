using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// A fake Microsoft identity platform token endpoint for the Stage B2 token source (owner decisions B-1, B-7, B-10). It
/// records each request it is handed and answers what the test scripts; by default a synthetic Bearer token. It opens no
/// socket and resolves no name: nothing here is evidence of how Entra behaves.
/// </summary>
internal sealed class FakeEntra : HttpMessageHandler
{
    internal const string IssuedToken = "synthetic-entra-access-token-for-key-vault";

    private readonly Lock _gate = new();
    private readonly List<Captured> _requests = [];

    internal Func<Captured, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(Answer($$"""{"token_type":"Bearer","expires_in":3599,"access_token":"{{IssuedToken}}"}"""));

    internal IReadOnlyList<Captured> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Completes when the next request has been recorded.</summary>
    internal TaskCompletionSource Received { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new Captured(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType,
            body is null ? new Dictionary<string, string>() : Form(body));
        TaskCompletionSource received;
        lock (_gate)
        {
            _requests.Add(captured);
            received = Received;
            Received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        received.TrySetResult();
        return await Respond(captured, cancellationToken);
    }

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    internal sealed record Captured(HttpMethod Method, Uri Uri, string? Authorization, string? MediaType, IReadOnlyDictionary<string, string> Form);
}

/// <summary>A handler that records what reaches it and answers 200, so a test can see whether a guard passed a request.</summary>
internal sealed class RecordingTransport : HttpMessageHandler
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    internal Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Respond(request));
    }
}

/// <summary>
/// Ephemeral test certificates, generated in memory for each test and never written anywhere. None is a real credential.
/// </summary>
internal static class TestCertificates
{
    private const string Subject = "CN=wce-stage-b2-ephemeral-test";

    internal static DateTimeOffset Now => new SteppedTimeProvider().GetUtcNow();

    internal static X509Certificate2 Rsa(int bits = 2048, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(bits);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notBefore ?? Now.AddDays(-1), notAfter ?? Now.AddDays(30));
    }

    internal static X509Certificate2 Ecdsa()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(Now.AddDays(-1), Now.AddDays(30));
    }

    /// <summary>The same certificate without its private key.</summary>
    internal static X509Certificate2 PublicOnly(X509Certificate2 certificate) => X509CertificateLoader.LoadCertificate(certificate.RawData);
}
