using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Integration.Tests.Connectors;

/// <summary>A fake of monday's token, revocation and API endpoints, routed by address. No real monday is contacted.</summary>
internal sealed class FakeMonday : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>> _token = new();
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _api = new();

    internal ConcurrentQueue<(Uri? Uri, string? Authorization, string Body)> Requests { get; } = new();

    internal int TokenCalls => Requests.Count(request => request.Uri == MondayEndpoints.Token);

    internal int RevokeCalls => Requests.Count(request => request.Uri == MondayEndpoints.Revoke);

    internal void ThenToken(string body) => _token.Enqueue(_ => Task.FromResult(Json(HttpStatusCode.OK, body)));

    internal void ThenToken(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _token.Enqueue(respond);

    internal void ThenApi(HttpStatusCode status, string body) => _api.Enqueue(() => Json(status, body));

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal static string Jwt(DateTimeOffset expiresAt, string marker) =>
        Base64Url.EncodeToString("""{"alg":"none"}"""u8) + "."
        + Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"exp\":" + expiresAt.ToUnixTimeSeconds() + ",\"jti\":\"" + marker + "\"}"))
        + ".not-a-signature";

    internal static string Tokens(string access, string refresh) =>
        "{\"access_token\":\"" + access + "\",\"refresh_token\":\"" + refresh + "\",\"token_type\":\"Bearer\",\"scope\":\"boards:read me:read\"}";

    internal const string Me = """{"data":{"me":{"id":"58201774","account":{"id":"9001"}}}}""";

    internal const string Items =
        """{"data":{"items":[{"id":"1111","name":"Plan","state":"active","created_at":"2026-09-01T08:00:00Z","updated_at":"2026-09-28T17:45:12Z","board":{"id":"9001"},"group":{"id":"topics"},"column_values":[]}]}}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.RequestUri, request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null, body));

        if (request.RequestUri == MondayEndpoints.Revoke)
        {
            return Json(HttpStatusCode.OK, """{"success":true}""");
        }

        if (request.RequestUri == MondayEndpoints.Token)
        {
            return _token.TryDequeue(out var token) ? await token(cancellationToken) : throw new InvalidOperationException("Unscripted token request.");
        }

        if (request.RequestUri == MondayEndpoints.Api)
        {
            return _api.TryDequeue(out var api) ? api() : throw new InvalidOperationException("Unscripted API request.");
        }

        throw new InvalidOperationException("A request went to an address the connector must never call.");
    }
}

/// <summary>In-memory secret storage: the stand-in for the approved secret store, which Gate 3B does not deploy.</summary>
internal sealed class MemorySecrets : IMondaySecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    internal IReadOnlyCollection<string> Names => _values.Keys.ToArray();

    internal void Put(MondaySecretName name, string value) => _values[name.Value] = Encoding.ASCII.GetBytes(value);

    internal string Text(string name) => Encoding.UTF8.GetString(_values[name]);

    public ValueTask<MondaySecretStoreStatus> SetAsync(MondaySecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        _values[name.Value] = value.ToArray();
        return ValueTask.FromResult(MondaySecretStoreStatus.Succeeded);
    }

    public ValueTask<MondaySecretLookup> GetAsync(MondaySecretName name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_values.TryGetValue(name.Value, out var value) ? MondaySecretLookup.Found(value) : MondaySecretLookup.Missing());

    public ValueTask<MondaySecretStoreStatus> DeleteAsync(MondaySecretName name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_values.TryRemove(name.Value, out _) ? MondaySecretStoreStatus.Succeeded : MondaySecretStoreStatus.NotFound);
}

/// <summary>A clock the test moves.</summary>
internal sealed class MovingClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>A delay that does not wait, so a test never sleeps.</summary>
internal sealed class NoWait : IMondayDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
