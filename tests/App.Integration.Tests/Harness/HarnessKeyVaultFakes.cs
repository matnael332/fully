using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Secrets;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// A fake Key Vault data plane for the offline adapter (owner decisions S-1 to S-5). It models only what the adapter may
/// use: get the latest version, set (each set adds a version) and delete (all versions leave the active namespace). A
/// deleted name stays reserved, as the documentation describes for a vault with soft delete: it answers not-found to a
/// get and a delete, and a set under it is refused with 409. No real vault is contacted.
/// </summary>
internal sealed class FakeVault : HttpMessageHandler
{
    internal const string Host = "wce-test-vault.vault.azure.net";
    internal const string NotFoundBody = """{"error":{"code":"SecretNotFound","message":"A secret with this name was not found in this key vault."}}""";
    internal const string ConflictBody = """{"error":{"code":"Conflict","message":"A secret with this name is in a deleted but recoverable state."}}""";

    internal static readonly Uri Origin = new("https://" + Host + "/");

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<(string Value, string? ContentType)>> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private readonly Queue<Behavior> _script = new();
    private readonly List<Exchange> _exchanges = [];

    internal delegate Task<HttpResponseMessage> Behavior(FakeVault vault, Exchange exchange, CancellationToken cancellationToken);

    internal FakeVault(SteppedTimeProvider? time = null) => Time = time;

    internal SteppedTimeProvider? Time { get; }

    /// <summary>Gets how many sets the model refused because their name was deleted and reserved.</summary>
    internal int Conflicts { get; private set; }

    internal IReadOnlyList<Exchange> Exchanges
    {
        get
        {
            lock (_gate)
            {
                return [.. _exchanges];
            }
        }
    }

    /// <summary>Scripts the next request's answer; once the script is empty, the model answers.</summary>
    internal void Then(Behavior behavior)
    {
        lock (_gate)
        {
            _script.Enqueue(behavior);
        }
    }

    /// <summary>Places a value as the product owner would by hand: plain text, with or without a content type.</summary>
    internal void Place(MondaySecretName name, string value, string? contentType = null) => Place(name.Value, value, contentType);

    internal void Place(string name, string value, string? contentType = null)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(name, out var versions))
            {
                _active[name] = versions = [];
            }

            versions.Add((value, contentType));
        }
    }

    internal bool IsActive(string name)
    {
        lock (_gate)
        {
            return _active.ContainsKey(name);
        }
    }

    internal bool IsDeleted(string name)
    {
        lock (_gate)
        {
            return _deleted.Contains(name);
        }
    }

    internal int Versions(string name)
    {
        lock (_gate)
        {
            return _active.TryGetValue(name, out var versions) ? versions.Count : 0;
        }
    }

    internal (string Value, string? ContentType) Current(string name)
    {
        lock (_gate)
        {
            return _active[name][^1];
        }
    }

    internal IReadOnlyCollection<string> ActiveNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _active.Keys];
            }
        }
    }

    // --- Scripted behaviors -----------------------------------------------------------------------------------------

    internal static Behavior Status(HttpStatusCode status, string? body = null, TimeSpan? retryAfter = null, DateTimeOffset? retryAt = null) =>
        (_, _, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json") };
            if (retryAfter is { } delta)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
            }
            else if (retryAt is { } date)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(date);
            }

            return Task.FromResult(response);
        };

    /// <summary>The vault applies the request, then the answer is lost on the way back.</summary>
    internal static Behavior ApplyThenLose() =>
        (vault, exchange, _) =>
        {
            using var lost = vault.Model(exchange);
            throw new HttpRequestException("The connection was reset after the request was sent.");
        };

    /// <summary>The request never reaches the vault.</summary>
    internal static Behavior Fail() => (_, _, _) => throw new HttpRequestException("The connection could not be made.");

    /// <summary>The vault never answers: time passes until the attempt is cancelled.</summary>
    internal static Behavior Hang() => (vault, _, cancellationToken) => vault.HangAsync(cancellationToken);

    /// <summary>The vault applies the request and then never answers.</summary>
    internal static Behavior ApplyThenHang() =>
        (vault, exchange, cancellationToken) =>
        {
            using var lost = vault.Model(exchange);
            return vault.HangAsync(cancellationToken);
        };

    /// <summary>Runs the given action as the request arrives, then lets the model answer.</summary>
    internal static Behavior Before(Action action) =>
        (vault, exchange, _) =>
        {
            action();
            return Task.FromResult(vault.Model(exchange));
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var exchange = new Exchange(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization is { } authorization ? authorization.Scheme + " " + authorization.Parameter : null,
            body,
            request.Content?.Headers.ContentType?.MediaType);
        Behavior? scripted;
        lock (_gate)
        {
            _exchanges.Add(exchange);
            scripted = _script.TryDequeue(out var next) ? next : null;
        }

        return scripted is null ? Model(exchange) : await scripted(this, exchange, cancellationToken);
    }

    private async Task<HttpResponseMessage> HangAsync(CancellationToken cancellationToken)
    {
        var time = Time ?? throw new InvalidOperationException("A hanging vault needs the stepped time source.");
        while (!cancellationToken.IsCancellationRequested)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        await Task.Yield();
        throw new OperationCanceledException(cancellationToken);
    }

    // The vault's own behaviour, for exactly the three requests the adapter may send.
    private HttpResponseMessage Model(Exchange exchange)
    {
        var uri = exchange.Uri;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != Host || uri.Query != "?api-version=" + KeyVaultSecretStore.ApiVersion)
        {
            return Json(HttpStatusCode.BadRequest, """{"error":{"code":"BadParameter","message":"Unexpected request."}}""");
        }

        if (exchange.Authorization is not { } authorization || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.Unauthorized, """{"error":{"code":"Unauthorized","message":"No credential."}}""");
        }

        var path = uri.AbsolutePath;
        lock (_gate)
        {
            if (exchange.Method == HttpMethod.Get && path.StartsWith("/secrets/", StringComparison.Ordinal) && path.EndsWith('/') && path.Count(c => c == '/') == 3)
            {
                var name = path["/secrets/".Length..^1];
                if (!_active.TryGetValue(name, out var versions))
                {
                    return Json(HttpStatusCode.NotFound, NotFoundBody);
                }

                var (value, contentType) = versions[^1];
                return Json(HttpStatusCode.OK, Bundle(name, versions.Count, value, contentType));
            }

            if (path.StartsWith("/secrets/", StringComparison.Ordinal) && path.Count(c => c == '/') == 2)
            {
                var name = path["/secrets/".Length..];
                if (exchange.Method == HttpMethod.Put)
                {
                    if (_deleted.Contains(name))
                    {
                        Conflicts++;
                        return Json(HttpStatusCode.Conflict, ConflictBody);
                    }

                    using var document = JsonDocument.Parse(exchange.Body!);
                    var value = document.RootElement.GetProperty("value").GetString()!;
                    var contentType = document.RootElement.TryGetProperty("contentType", out var type) ? type.GetString() : null;
                    if (!_active.TryGetValue(name, out var versions))
                    {
                        _active[name] = versions = [];
                    }

                    versions.Add((value, contentType));
                    return Json(HttpStatusCode.OK, Bundle(name, versions.Count, value, contentType));
                }

                if (exchange.Method == HttpMethod.Delete)
                {
                    if (!_active.Remove(name))
                    {
                        return Json(HttpStatusCode.NotFound, NotFoundBody);
                    }

                    _deleted.Add(name);
                    return Json(
                        HttpStatusCode.OK,
                        "{\"recoveryId\":\"https://" + Host + "/deletedsecrets/" + name + "\",\"id\":\"https://" + Host + "/secrets/" + name + "\"}");
                }
            }
        }

        return Json(HttpStatusCode.BadRequest, """{"error":{"code":"BadParameter","message":"Unexpected request."}}""");
    }

    private static string Bundle(string name, int version, string value, string? contentType)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("value", value);
            if (contentType is not null)
            {
                writer.WriteString("contentType", contentType);
            }

            writer.WriteString("id", "https://" + Host + "/secrets/" + name + "/" + version.ToString("D32", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteStartObject("attributes");
            writer.WriteBoolean("enabled", true);
            writer.WriteString("recoveryLevel", "Recoverable+Purgeable");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>One request as the vault received it.</summary>
    internal sealed record Exchange(HttpMethod Method, Uri Uri, string? Authorization, byte[]? Body, string? MediaType);
}

/// <summary>A time source the test moves; due timers fire synchronously as it moves.</summary>
internal sealed class SteppedTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly Lock _gate = new();
    private readonly List<SteppedTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    internal TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromTicks(_ticks);
            }
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow() => Epoch + Elapsed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SteppedTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward and fires every timer that falls due.</summary>
    internal void Advance(TimeSpan by)
    {
        List<SteppedTimer> due;
        lock (_gate)
        {
            _ticks += by.Ticks;
            due = _timers.Where(timer => timer.DueAt is { } at && at <= _ticks).ToList();
            foreach (var timer in due)
            {
                timer.DueAt = null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private long Now
    {
        get
        {
            lock (_gate)
            {
                return _ticks;
            }
        }
    }

    private sealed class SteppedTimer(SteppedTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal long? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.Now + dueTime.Ticks;
            return true;
        }

        internal void Fire() => callback(state);

        public void Dispose() => DueAt = null;

        public ValueTask DisposeAsync()
        {
            DueAt = null;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A delay that records each wait and moves the stepped time by it, so no test sleeps.</summary>
internal sealed class RecordingDelay(SteppedTimeProvider? time = null) : IMondayDelay
{
    private readonly List<TimeSpan> _waits = [];

    internal IReadOnlyList<TimeSpan> Waits => [.. _waits];

    /// <summary>Runs as a wait begins, before the caller's cancellation is checked.</summary>
    internal Action? OnWait { get; set; }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        OnWait?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        _waits.Add(delay);
        time?.Advance(delay);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A token source that answers a fixed synthetic token, or fails.</summary>
internal sealed class FixedTokenSource(string? token) : IKeyVaultAccessTokenSource
{
    internal int Calls { get; private set; }

    internal Exception? Failure { get; init; }

    public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Failure is { } failure ? ValueTask.FromException<string?>(failure) : ValueTask.FromResult(token);
    }
}

/// <summary>The connector's own names for a credential version and a verifier, which it builds internally.</summary>
internal static class ConnectorSecretNames
{
    internal static MondaySecretName Tokens(TenantId tenant, Guid connectionId, Guid version) =>
        Invoke("ForTokens", tenant, connectionId, version);

    internal static MondaySecretName Verifier(TenantId tenant, Guid transactionId) =>
        Invoke("ForVerifier", tenant, transactionId);

    private static MondaySecretName Invoke(string method, params object[] arguments)
    {
        var factory = typeof(MondaySecretName).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The connector's name factory moved.");
        return (MondaySecretName)factory.Invoke(null, arguments)!;
    }
}
