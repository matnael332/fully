using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Harness.Monday.Composition;
using WorkContinuity.Harness.Monday.Configuration;
using WorkContinuity.Harness.Monday.Hosting;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.InitialSync;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Synthetic values that must never leave the places they belong to. None is a real credential: each is a fixed,
/// obviously fake marker the leak tests search for.
/// </summary>
internal static class HarnessSentinels
{
    /// <summary>The fake monday client credential the tests place in in-memory secret storage.</summary>
    internal const string ClientCredential = "SENTINEL-harness-client-credential";

    /// <summary>The fake authorization code a callback carries.</summary>
    internal const string Code = "SENTINEL-harness-authorization-code";

    /// <summary>The fake refresh token the fake provider issues.</summary>
    internal const string RefreshToken = "SENTINEL-harness-refresh-token";

    /// <summary>The marker inside the fake access token's claims.</summary>
    internal const string AccessMarker = "SENTINEL-harness-access-marker";

    /// <summary>The fake short-lived token inside a webhook token's claims.</summary>
    internal const string ShortLivedToken = "SENTINEL-harness-short-lived-token";

    /// <summary>A callback parameter the harness does not know, with a value that must not be kept anywhere.</summary>
    internal const string UnknownCallbackValue = "SENTINEL-harness-unknown-callback-value";

    /// <summary>The name of that unknown callback parameter, which must not be kept either.</summary>
    internal const string UnknownCallbackName = "sentinel_unknown_parameter";

    /// <summary>Business content of a webhook body: it belongs in its raw receipt and nowhere else.</summary>
    internal const string BodyMarker = "SENTINEL-harness-business-body-marker";
}

/// <summary>A source of timer ticks the test fires by hand, recording the period each timer was asked for.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];

    /// <summary>Gets the due time and the period of every timer created so far.</summary>
    internal IReadOnlyList<(TimeSpan DueTime, TimeSpan Period)> Requested
    {
        get
        {
            lock (_gate)
            {
                return _timers.Select(timer => (timer.DueTime, timer.Period)).ToList();
            }
        }
    }

    /// <summary>Gets how many timers are still running.</summary>
    internal int Running
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => !timer.Disposed);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Fires every running timer once, as the passing of one period would.</summary>
    internal void Tick()
    {
        ManualTimer[] running;
        lock (_gate)
        {
            running = _timers.Where(timer => !timer.Disposed).ToArray();
        }

        foreach (var timer in running)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;

        public TimeSpan Period { get; private set; } = period;

        public bool Disposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            Period = period;
            return true;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Keeps every record the harness's log pipeline lets through.</summary>
internal sealed class CapturingHarnessSink : IHarnessLogSink
{
    private readonly ConcurrentQueue<HarnessLogRecord> _records = new();

    internal IReadOnlyList<HarnessLogRecord> Records => [.. _records];

    /// <summary>Gets everything a record carries, as one text to search.</summary>
    internal string Text => string.Join('\n', _records.Select(record => $"{record.Category}|{record.Level}|{record.EventId}|{record.Message}|{record.ExceptionType}"));

    public void Write(HarnessLogRecord record) => _records.Enqueue(record);
}

/// <summary>
/// A certificate for the loopback operator listener, created in memory for the test run. It is never installed
/// anywhere and no trust setting changes: the test's own client accepts exactly this certificate, by its hash.
/// </summary>
internal static class LoopbackCertificate
{
    private static readonly Lazy<X509Certificate2> Shared = new(Create);

    internal static X509Certificate2 Instance => Shared.Value;

    private static X509Certificate2 Create()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=wce-harness-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));

        // Through PKCS#12 so the platform's TLS stack can use the private key; nothing is added to a certificate store.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}

/// <summary>What a test changes about the composition; the defaults are a complete offline harness over fakes.</summary>
internal sealed class HarnessTestSetup
{
    /// <summary>Whether in-memory secret storage with the synthetic client credential and Signing Secret is supplied.</summary>
    public bool WithSecrets { get; set; } = true;

    /// <summary>Whether the fake provider transport is supplied.</summary>
    public bool WithProvider { get; set; } = true;

    /// <summary>The tenant to configure; a new one when null.</summary>
    public TenantId? Tenant { get; set; }

    /// <summary>The canonical user to configure; a new one of the tenant when null.</summary>
    public UserId? User { get; set; }

    /// <summary>Secret storage to share with another instance.</summary>
    public MemorySecrets? Secrets { get; set; }

    /// <summary>A clock to share with another instance.</summary>
    public MovingClock? Clock { get; set; }

    /// <summary>Whether the relay and the consumer run continuously.</summary>
    public bool RunLoops { get; set; }

    /// <summary>The local Service Bus emulator to use as the broker; the in-memory broker when null.</summary>
    public string? EmulatorConnectionString { get; set; }
}

/// <summary>One authorization the operator started in a browser: where the browser was sent and what it holds.</summary>
internal sealed record StartedAuthorization(string StartAddress, Uri ProviderAddress, string State, string Binding);

/// <summary>
/// The offline harness composed in process over the suite's own fakes: the real local PostgreSQL as the runtime role,
/// an in-memory broker, in-memory secret storage with synthetic values, a deterministic fake of monday's endpoints, a
/// movable clock and a timer the test fires. Nothing here contacts monday, and no machine setting changes.
/// </summary>
internal sealed class HarnessTestHost : IAsyncDisposable
{
    internal const string ClientId = "wce-harness-test-client";
    internal const string Account = "9001";
    internal const string BindingCookie = "__Host-wce-harness-binding";

    private readonly DatabaseFixture _fixture;
    private readonly StringBuilder _transcript = new();
    private int _delivered;

    private HarnessTestHost(
        DatabaseFixture fixture,
        HarnessHost host,
        HarnessOptions options,
        TenantId tenant,
        UserId user,
        MemorySecrets? secrets,
        FakeMonday? monday,
        MovingClock clock,
        ManualTimeProvider time,
        InMemoryTransport transport,
        CapturingHarnessSink logs)
    {
        _fixture = fixture;
        Host = host;
        Options = options;
        Tenant = tenant;
        User = user;
        Secrets = secrets;
        Monday = monday;
        Clock = clock;
        Time = time;
        Transport = transport;
        Logs = logs;
        Account1 = new FakeMondayWorkspace();
        monday?.AnswerApiAsync(Account1.AnswerAsync);
        OperatorClient = OperatorClientFor(options);
        WebhookClient = new HttpClient { BaseAddress = host.WebhookAddress };
    }

    internal HarnessHost Host { get; }

    internal HarnessOptions Options { get; }

    internal TenantId Tenant { get; }

    internal UserId User { get; }

    internal MemorySecrets? Secrets { get; }

    internal FakeMonday? Monday { get; }

    /// <summary>The fake monday account every unscripted API call is answered from.</summary>
    internal FakeMondayWorkspace Account1 { get; }

    internal MovingClock Clock { get; }

    internal ManualTimeProvider Time { get; }

    internal InMemoryTransport Transport { get; }

    internal CapturingHarnessSink Logs { get; }

    /// <summary>A client for the operator listener that accepts only the test's own certificate and keeps no cookies.</summary>
    internal HttpClient OperatorClient { get; }

    internal HttpClient WebhookClient { get; }

    /// <summary>Everything the operator console answered so far.</summary>
    internal string Transcript => _transcript.ToString();

    /// <summary>The access tokens the fake provider issued, for the leak tests.</summary>
    internal List<string> IssuedAccessTokens { get; } = [];

    /// <summary>The webhook tokens the tests presented, for the leak tests.</summary>
    internal List<string> PresentedWebhookTokens { get; } = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts a harness and requires it to start.</summary>
    internal static async Task<HarnessTestHost> StartAsync(DatabaseFixture fixture, HarnessTestSetup? setup = null)
    {
        setup ??= new HarnessTestSetup();
        var tenant = setup.Tenant ?? await ConnectorAuthorizationData.NewTenantAsync(fixture, Token);
        var user = setup.User ?? await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, Token);

        MemorySecrets? secrets = null;
        if (setup.WithSecrets)
        {
            secrets = setup.Secrets ?? new MemorySecrets();
            secrets.Put(MondaySecretName.ClientSecret, HarnessSentinels.ClientCredential);
            secrets.Put(MondaySecretName.SigningSecret, WebhookTestTokens.SigningSecret);
        }

        var monday = setup.WithProvider ? new FakeMonday() : null;
        var clock = setup.Clock ?? new MovingClock(DateTimeOffset.UtcNow);
        var time = new ManualTimeProvider();
        var transport = new InMemoryTransport();
        var logs = new CapturingHarnessSink();
        var seams = new HarnessSeams
        {
            SecretStore = secrets,
            ProviderTransport = monday,
            Clock = clock,
            TimeProvider = time,
            Delay = new NoWait(),
            TransportPublisher = setup.EmulatorConnectionString is null ? transport : null,
            OperatorCertificate = LoopbackCertificate.Instance,
            LogSink = logs,
            RunBackboneLoops = setup.RunLoops,
        };

        // The operator listener's port is part of the redirect address, so it is chosen first; a port taken in the
        // meantime is tried again with another.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var options = OptionsFor(fixture.ApplicationConnectionString, tenant, user, FreePort(), setup.EmulatorConnectionString);
            var started = await HarnessHost.StartAsync(options, seams, Token);
            if (started.Host is { } host)
            {
                return new HarnessTestHost(fixture, host, options, tenant, user, secrets, monday, clock, time, transport, logs);
            }

            Assert.Equal(HarnessRefusal.ListenerUnavailable, started.Refusal);
        }

        throw new InvalidOperationException("No loopback port could be bound for the operator listener.");
    }

    /// <summary>The harness's configuration as the reviewed boundary reads it.</summary>
    internal static IConfiguration ConfigurationFor(string connectionString, TenantId tenant, UserId user, int operatorPort, string? emulator = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HarnessOptions.TenantKey] = tenant.ToString(),
                [HarnessOptions.CanonicalUserKey] = user.ToString(),
                [HarnessOptions.DatabaseKey] = connectionString,
                [HarnessOptions.ClientIdKey] = ClientId,
                [HarnessOptions.RedirectKey] = $"https://127.0.0.1:{operatorPort}/operator/callback",
                [HarnessOptions.WebhookPortKey] = "0",
                [HarnessOptions.WebhookBaseAddressKey] = WebhookTestTokens.BaseAddress,
                [HarnessOptions.TransportKey] = emulator,
            })
            .Build();

    internal static HarnessOptions OptionsFor(string connectionString, TenantId tenant, UserId user, int operatorPort, string? emulator = null)
    {
        var read = HarnessOptions.Read(ConfigurationFor(connectionString, tenant, user, operatorPort, emulator));
        Assert.Equal(HarnessRefusal.None, read.Refusal);
        return Assert.IsType<HarnessOptions>(read.Options);
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A GET to the operator listener, exactly as given: no cookie, no redirect followed.</summary>
    internal Task<HttpResponseMessage> GetOperatorAsync(string pathAndQuery, Action<HttpRequestMessage>? adjust = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        adjust?.Invoke(request);
        return OperatorClient.SendAsync(request, Token);
    }

    /// <summary>The operator asks for an authorization at the console and opens the address in a browser.</summary>
    internal async Task<StartedAuthorization> StartAuthorizationAsync(Guid? reauthorize = null)
    {
        var answer = await ConsoleAsync(reauthorize is { } connection ? $"reauthorize {connection:D}" : "authorize");
        var startAddress = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(line => line.StartsWith("https://", StringComparison.Ordinal));
        using var response = await GetOperatorAsync(new Uri(startAddress).PathAndQuery);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var provider = Assert.IsType<Uri>(response.Headers.Location);
        return new StartedAuthorization(startAddress, provider, QueryValue(provider, "state"), BindingOf(response));
    }

    /// <summary>The provider's redirect back, as the browser that holds the binding would follow it.</summary>
    internal Task<HttpResponseMessage> CallbackAsync(string? state, string? code, string? binding, string extraQuery = "", Action<HttpRequestMessage>? adjust = null)
    {
        var query = new List<string>();
        if (state is not null)
        {
            query.Add("state=" + Uri.EscapeDataString(state));
        }

        if (code is not null)
        {
            query.Add("code=" + Uri.EscapeDataString(code));
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/operator/callback?" + string.Join('&', query) + extraQuery);
        if (binding is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", BindingCookie + "=" + binding);
        }

        adjust?.Invoke(request);
        return OperatorClient.SendAsync(request, Token);
    }

    /// <summary>Scripts the fake provider's answers to one code exchange and the identity read that follows it.</summary>
    internal string ScriptGrant()
    {
        var monday = Assert.IsType<FakeMonday>(Monday);
        var access = FakeMonday.Jwt(Clock.UtcNow.AddHours(1), HarnessSentinels.AccessMarker + "-" + IssuedAccessTokens.Count);
        IssuedAccessTokens.Add(access);
        monday.ThenToken(FakeMonday.Tokens(access, HarnessSentinels.RefreshToken));
        monday.ThenApi(HttpStatusCode.OK, FakeMonday.Me);
        return access;
    }

    /// <summary>A whole authorization against the fake provider; returns the new connection.</summary>
    internal async Task<Guid> AuthorizeAsync()
    {
        var before = await ConnectionsAsync();
        var started = await StartAuthorizationAsync();
        ScriptGrant();
        using var callback = await CallbackAsync(started.State, HarnessSentinels.Code, started.Binding);
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);
        return Assert.Single((await ConnectionsAsync()).Except(before));
    }

    /// <summary>The connections that hold an authorization in the harness's tenant, as its own evidence reader lists them.</summary>
    internal async Task<IReadOnlyList<Guid>> ConnectionsAsync()
    {
        var evidence = await Host.Operator.ReadEvidenceAsync(Token);
        Assert.True(evidence.IsSuccess, evidence.IsSuccess ? string.Empty : evidence.Error.Code);
        return evidence.Value.Authorizations.Select(authorization => authorization.ConnectionId).ToList();
    }

    /// <summary>Posts a body to a connection's webhook address with the given <c>Authorization</c> value, when there is one.</summary>
    internal Task<HttpResponseMessage> PostWebhookAsync(Guid connection, string body, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MondayWebhookHost.PathOf(connection))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return WebhookClient.SendAsync(request, Token);
    }

    /// <summary>Delivers a signed status change of one item, as monday would, and returns its trigger id.</summary>
    internal async Task<string> DeliverAsync(Guid connection, string item, string label, string board)
    {
        var trigger = WebhookData.NewTrigger();
        var token = WebhookTestTokens.Mint(connection, Account, Clock.UtcNow, shortLivedToken: HarnessSentinels.ShortLivedToken);
        PresentedWebhookTokens.Add(token);
        using var response = await PostWebhookAsync(connection, WebhookBody(item, label, trigger, board), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return trigger;
    }

    /// <summary>A documented status change whose item name carries the business-body marker.</summary>
    internal static string WebhookBody(string item, string label, string trigger, string board) =>
        "{\"event\":{\"userId\":9603417,\"originalTriggerUuid\":null,\"boardId\":" + board + ",\"groupId\":\"topics\",\"pulseId\":" + item
        + ",\"pulseName\":\"" + HarnessSentinels.BodyMarker + "\",\"columnId\":\"status\",\"columnType\":\"color\",\"columnTitle\":\"Status\",\"value\":{\"label\":{\"index\":3,\"text\":\""
        + label + "\",\"style\":{\"color\":\"#0086c0\"}},\"post_id\":null},\"previousValue\":null,\"changedAt\":1633944017.473193,"
        + "\"isTopGroup\":true,\"app\":\"monday\",\"type\":\"update_column_value\",\"triggerTime\":\"2021-10-11T09:20:18.022Z\","
        + "\"subscriptionId\":73761176,\"triggerUuid\":\"" + trigger + "\"}}";

    /// <summary>
    /// Runs bounded relay passes until the harness tenant's own outbox is published. Older pending work of earlier tests
    /// is relayed first, as the suite's own relay helper documents; it goes to this test's in-memory broker and no further.
    /// </summary>
    internal async Task RelayOwnAsync()
    {
        for (var pass = 0; pass < 100; pass++)
        {
            if (await PendingOutboxAsync() == 0)
            {
                return;
            }

            await Host.RelayOnceAsync(Token);
        }

        Assert.Fail("The harness tenant's outbox was not published within 100 relay passes.");
    }

    /// <summary>Hands the harness tenant's newly published messages, and no other tenant's, to the harness's consumer.</summary>
    internal async Task<IReadOnlyList<TransportSettlementKind>> PumpOwnAsync()
    {
        var session = TransportEnvelope.SessionIdFor(Tenant);
        var own = Transport.Accepted.Where(wire => wire.SessionId == session).ToList();
        var settled = new List<TransportSettlementKind>();
        while (_delivered < own.Count)
        {
            var wire = own[_delivered++];
            var settlement = await Host.ConsumeOwnSessionAsync(Received(wire, _delivered), Token);
            settled.Add(Assert.IsType<TransportSettlement>(settlement).Kind);
        }

        return settled;
    }

    internal static ReceivedTransportMessage Received(TransportWireMessage wire, long sequence) =>
        new(wire.MessageId, wire.SessionId, wire.CorrelationId, wire.ContentType, wire.ApplicationProperties, wire.Body, DeliveryCount: 1, sequence);

    /// <summary>Relays and consumes whatever the harness tenant has pending.</summary>
    internal async Task<IReadOnlyList<TransportSettlementKind>> RelayAndPumpAsync()
    {
        await RelayOwnAsync();
        return await PumpOwnAsync();
    }

    /// <summary>Fires the cleanup timer once, as five minutes passing would, and waits for the cleanup it starts to finish.</summary>
    internal async Task TickCleanupAsync()
    {
        await WaitForAsync(() => Time.Running >= 1);
        var before = Host.Diagnostics.Snapshot().VerifierCleanup.Runs;
        Time.Tick();
        await WaitForAsync(() => Host.Diagnostics.Snapshot().VerifierCleanup.Runs > before);
    }

    /// <summary>Waits, for at most ten seconds, until a condition holds.</summary>
    internal static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Token);
        }

        Assert.Fail("The condition did not hold within ten seconds.");
    }

    /// <summary>Types one line at the operator console and returns its answer.</summary>
    internal async Task<string> ConsoleAsync(string line)
    {
        using var output = new StringWriter();
        Assert.True(await Host.Console.ExecuteAsync(line, output, Token));
        var answer = output.ToString();
        _transcript.Append(answer);
        return answer;
    }

    internal async Task<long> PendingOutboxAsync()
    {
        await using var admin = await _fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM outbox_messages WHERE tenant_id = @tenant AND published_at_utc IS NULL;", admin);
        command.Parameters.AddWithValue("tenant", Tenant.Value);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    internal static string QueryValue(Uri address, string name) =>
        Uri.UnescapeDataString(address.Query.TrimStart('?').Split('&').Single(pair => pair.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..]);

    /// <summary>The binding cookie's value, from the one <c>Set-Cookie</c> header of a start response.</summary>
    internal static string BindingOf(HttpResponseMessage response)
    {
        var cookie = Assert.Single(CookiesOf(response));
        Assert.StartsWith(BindingCookie + "=", cookie, StringComparison.Ordinal);
        return cookie[(BindingCookie.Length + 1)..].Split(';')[0];
    }

    /// <summary>Every <c>Set-Cookie</c> header of a response; none when it sets no cookie.</summary>
    internal static IReadOnlyList<string> CookiesOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) ? [.. cookies] : [];

    /// <summary>A response header's value, or a marker when the response does not carry it.</summary>
    internal static string HeaderOf(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(" | ", values) : "(absent)";

    public async ValueTask DisposeAsync()
    {
        OperatorClient.Dispose();
        WebhookClient.Dispose();
        await Host.DisposeAsync();
    }

    private static HttpClient OperatorClientFor(HarnessOptions options)
    {
        var expected = LoopbackCertificate.Instance.GetCertHashString(HashAlgorithmName.SHA256);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    presented is not null && string.Equals(presented.GetCertHashString(HashAlgorithmName.SHA256), expected, StringComparison.Ordinal),
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri(options.OperatorOrigin) };
    }
}
