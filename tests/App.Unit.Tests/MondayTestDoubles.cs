using System.Net;
using System.Text;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Unit.Tests;

/// <summary>One request the fake monday server received, captured before the client disposed it.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

/// <summary>
/// A deterministic fake of monday's API endpoint: answers from a script, records every request, and fails the test on
/// a request nobody scripted.
/// </summary>
internal sealed class ScriptedMondayServer : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    private Func<HttpRequestMessage, HttpResponseMessage>? _always;

    internal List<RecordedRequest> Requests { get; } = [];

    internal ScriptedMondayServer Then(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        _script.Enqueue(_ => Respond(status, body, configure));
        return this;
    }

    internal ScriptedMondayServer Then(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _script.Enqueue(respond);
        return this;
    }

    internal ScriptedMondayServer ThenThrow(Exception exception)
    {
        _script.Enqueue(_ => throw exception);
        return this;
    }

    internal ScriptedMondayServer Always(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        _always = _ => Respond(status, body, configure);
        return this;
    }

    internal ScriptedMondayServer AlwaysThrow(Func<Exception> exception)
    {
        _always = _ => throw exception();
        return this;
    }

    internal static HttpResponseMessage Respond(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        configure?.Invoke(response);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri, headers, body));

        cancellationToken.ThrowIfCancellationRequested();

        if (_script.TryDequeue(out var next))
        {
            return next(request);
        }

        return _always is not null
            ? _always(request)
            : throw new InvalidOperationException("The fake monday server received a request nobody scripted.");
    }
}

/// <summary>A credential source that answers from a script, then from a default, and counts its calls.</summary>
internal sealed class ScriptedCredentialSource : IMondayCredentialSource
{
    private readonly Queue<Func<MondayRequestContext, MondayCredentialResult>> _script = new();

    internal ScriptedCredentialSource(Func<MondayRequestContext, MondayCredentialResult> fallback) => Fallback = fallback;

    internal Func<MondayRequestContext, MondayCredentialResult> Fallback { get; set; }

    /// <summary>Answers a renewal after a rejection; not renewed unless a test says otherwise.</summary>
    internal Func<MondayAccessCredential, MondayCredentialResult> Renewal { get; set; } = _ => MondayCredentialResult.NotRenewed();

    internal int Calls { get; private set; }

    internal List<MondayAccessCredential> Rejected { get; } = [];

    internal ScriptedCredentialSource Then(Func<MondayRequestContext, MondayCredentialResult> answer)
    {
        _script.Enqueue(answer);
        return this;
    }

    public ValueTask<MondayCredentialResult> AcquireAsync(MondayRequestContext context, CancellationToken cancellationToken)
    {
        Calls++;
        var answer = _script.TryDequeue(out var next) ? next : Fallback;
        return ValueTask.FromResult(answer(context));
    }

    public ValueTask<MondayCredentialResult> RenewAfterRejectionAsync(
        MondayRequestContext context,
        MondayAccessCredential rejected,
        CancellationToken cancellationToken)
    {
        Rejected.Add(rejected);
        return ValueTask.FromResult(Renewal(rejected));
    }
}

/// <summary>Records the waits between attempts instead of sleeping through them.</summary>
internal sealed class RecordingDelay : IMondayDelay
{
    internal List<TimeSpan> Waits { get; } = [];

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waits.Add(delay);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A clock that stands still.</summary>
internal sealed class StoppedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>A monday client wired to the fakes, with a fixed jitter of 0.5.</summary>
internal sealed class MondayHarness
{
    /// <summary>An obviously fake, low-entropy stand-in for an access token.</summary>
    internal const string Token = "test-token-not-a-secret";

    internal static readonly TenantId Tenant = TenantId.From(Guid.Parse("0192a3b4-0000-7000-8000-00000000a001"));
    internal static readonly Guid Connection = Guid.Parse("0192a3b4-0000-7000-8000-00000000c001");
    internal static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    internal MondayHarness()
    {
        Credentials = new ScriptedCredentialSource(_ => MondayCredentialResult.Acquired(Credential()));
        Client = new MondayReadClient(new HttpClient(Server), Credentials, new StoppedClock(Now), Delay, () => 0.5);
    }

    internal ScriptedMondayServer Server { get; } = new();

    internal ScriptedCredentialSource Credentials { get; }

    internal RecordingDelay Delay { get; } = new();

    internal MondayReadClient Client { get; }

    internal static ConnectorIdentity Identity { get; } =
        new(Tenant, Connection, ConnectorIdentityKind.DelegatedUser, "58201774");

    internal static MondayRequestContext Context() => new(Tenant, Connection, CorrelationId.New());

    internal static MondayAccessCredential Credential(ConnectorIdentity? identity = null, DateTimeOffset? expiresAt = null) =>
        new(Token, identity ?? Identity, expiresAt ?? Now.AddMinutes(30));
}

/// <summary>JSON bodies shaped like monday's answers.</summary>
internal static class MondayJson
{
    internal const string DefaultColumnValues =
        """[{"id":"status","type":"status","text":"Working on it","value":"{\"index\":0}"},{"id":"text0","type":"text","text":null,"value":null}]""";

    internal static string Item(
        string id = "1111",
        string state = "active",
        string updatedAt = "\"2026-09-28T17:45:12Z\"",
        string columnValues = DefaultColumnValues) =>
        "{\"id\":\"" + id + "\",\"name\":\"Quarterly plan\",\"state\":\"" + state
        + "\",\"created_at\":\"2026-09-01T08:00:00Z\",\"updated_at\":" + updatedAt
        + ",\"board\":{\"id\":\"9001\"},\"group\":{\"id\":\"topics\"},\"column_values\":" + columnValues + "}";

    internal static string Items(params string[] items) =>
        "{\"data\":{\"items\":[" + string.Join(",", items) + "]},\"account_id\":1}";

    internal static string BoardPage(string cursor, params string[] items) =>
        "{\"data\":{\"boards\":[{\"items_page\":{\"cursor\":" + cursor + ",\"items\":[" + string.Join(",", items) + "]}}]}}";

    internal static string NextPage(string cursor, params string[] items) =>
        "{\"data\":{\"next_items_page\":{\"cursor\":" + cursor + ",\"items\":[" + string.Join(",", items) + "]}}}";

    internal static string Error(string code, string message = "An error.", int statusCode = 200, string extra = "") =>
        "{\"errors\":[{\"message\":\"" + message + "\",\"extensions\":{\"code\":\"" + code + "\",\"status_code\":"
        + statusCode + extra + "}}],\"account_id\":1}";
}
