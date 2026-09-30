using System.Buffers.Text;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Web.Connectors;
using WorkContinuity.Web.Platform;

namespace WorkContinuity.Integration.Tests.Connectors;

/// <summary>
/// A deterministic fake of monday's webhook tokens: HS256 JWTs signed with obviously fake Signing Secrets for the
/// test base address. Nothing here is a real monday secret, token or account.
/// </summary>
internal static class WebhookTestTokens
{
    /// <summary>The Signing Secret the tests place in secret storage. It protects nothing.</summary>
    internal const string SigningSecret = "test-signing-secret-A";

    /// <summary>Another app's Signing Secret. It protects nothing.</summary>
    internal const string OtherSecret = "test-signing-secret-B";

    internal const string BaseAddress = "https://fully.example.test";

    internal static string Audience(Guid connectionId) => BaseAddress + "/api/connectors/monday/webhooks/" + connectionId.ToString("D");

    /// <summary>A token as monday documents it, issued at the given instant and valid for five minutes.</summary>
    internal static string Mint(
        Guid audienceConnection,
        string account,
        DateTimeOffset issuedAt,
        string userId = "4242",
        string shortLivedToken = "short-lived-token-marker",
        string secret = SigningSecret,
        TimeSpan? lifetime = null)
    {
        var claims = "{\"accountId\":" + account + ",\"userId\":" + userId + ",\"aud\":\"" + Audience(audienceConnection) + "\""
            + ",\"exp\":" + Seconds(issuedAt.Add(lifetime ?? TimeSpan.FromMinutes(5))) + ",\"iat\":" + Seconds(issuedAt)
            + ",\"shortLivedToken\":\"" + shortLivedToken + "\"}";
        return Sign(claims, secret);
    }

    internal static string Sign(string claimsJson, string secret = SigningSecret)
    {
        var signingInput = Base64Url.EncodeToString("""{"alg":"HS256","typ":"JWT"}"""u8)
            + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claimsJson));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(signingInput));
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    internal static string WithClaims(string token, string claimsJson)
    {
        var parts = token.Split('.');
        return parts[0] + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claimsJson)) + "." + parts[2];
    }

    private static string Seconds(DateTimeOffset instant) => instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}

/// <summary>Secret storage whose Signing Secret a test places, removes or makes unreadable.</summary>
internal sealed class WebhookSecrets : IMondaySecretStore
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal bool Unavailable { get; set; }

    internal int Reads { get; private set; }

    internal void Put(MondaySecretName name, string value)
    {
        lock (_gate)
        {
            _values[name.Value] = Encoding.ASCII.GetBytes(value);
        }
    }

    public ValueTask<MondaySecretStoreStatus> SetAsync(MondaySecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Webhook intake never writes a secret.");

    public ValueTask<MondaySecretLookup> GetAsync(MondaySecretName name, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Reads++;
            return ValueTask.FromResult(
                Unavailable ? MondaySecretLookup.Unavailable()
                : _values.TryGetValue(name.Value, out var value) ? MondaySecretLookup.Found(value)
                : MondaySecretLookup.Missing());
        }
    }

    public ValueTask<MondaySecretStoreStatus> DeleteAsync(MondaySecretName name, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Webhook intake never deletes a secret.");
}

/// <summary>A monday connection as 3B leaves it: its tenant, the authorizing user and the connection id.</summary>
internal sealed record SeededConnection(TenantId Tenant, UserId User, Guid Connection, string Account);

/// <summary>
/// The real monday webhook endpoint in a real Kestrel server on a loopback port, composed as the Web host composes it:
/// the correlation and exception middleware, persistence bound to the runtime role, the flow-scoped accessors and
/// <see cref="MondayWebhookRegistration.AddMondayWebhookIntake"/>. The tests supply the clock, the secret storage and the
/// configuration, and capture every log record of every category at every level.
/// </summary>
internal sealed class MondayWebhookHost : IAsyncDisposable
{
    internal static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly WebApplication _app;

    private MondayWebhookHost(WebApplication app, HttpClient client, CapturingLoggerProvider logs, WebhookSecrets? secrets, MovingClock clock)
    {
        _app = app;
        Client = client;
        Logs = logs;
        Secrets = secrets;
        Clock = clock;
    }

    internal HttpClient Client { get; }

    internal CapturingLoggerProvider Logs { get; }

    /// <summary>Gets the test's secret storage, or null when the host runs with its own fail-closed default.</summary>
    internal WebhookSecrets? Secrets { get; }

    internal MovingClock Clock { get; }

    /// <summary>Starts a host over the given database.</summary>
    /// <param name="connectionString">The runtime role's connection string, or one that cannot be reached.</param>
    /// <param name="configured">Whether the public base address is configured.</param>
    /// <param name="withSecretStore">Whether the test supplies secret storage holding the Signing Secret.</param>
    /// <param name="configure">Further service changes, applied after intake is composed.</param>
    /// <param name="hideServerBodyLimit">
    /// Whether each request is served as by a server that offers no per-request body limit, so that only the
    /// endpoint's own bound limits how much of a body is read.
    /// </param>
    internal static async Task<MondayWebhookHost> StartAsync(
        string connectionString,
        bool configured = true,
        bool withSecretStore = true,
        Action<IServiceCollection>? configure = null,
        bool hideServerBodyLimit = false)
    {
        var logs = new CapturingLoggerProvider();
        var clock = new MovingClock(Start);
        var secrets = withSecretStore ? new WebhookSecrets() : null;
        secrets?.Put(MondaySecretName.SigningSecret, WebhookTestTokens.SigningSecret);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter((_, _) => true);
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            [MondayWebhookRegistration.BaseAddressKey] = configured ? WebhookTestTokens.BaseAddress : string.Empty,

            // Every category at every level, whatever an appsettings file next to the tests says.
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
        });

        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        builder.Services.AddPersistence(builder.Configuration);
        if (secrets is not null)
        {
            builder.Services.AddSingleton<IMondaySecretStore>(secrets);
        }

        builder.Services.AddMondayWebhookIntake(builder.Configuration);
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseMiddleware<PlatformExceptionMiddleware>();
        if (hideServerBodyLimit)
        {
            app.Use((context, next) =>
            {
                context.Features.Set<IHttpMaxRequestBodySizeFeature>(null);
                return next(context);
            });
        }

        app.MapMondayWebhooks();
        await app.StartAsync(TestContext.Current.CancellationToken);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = Assert.Single(Assert.IsType<IServerAddressesFeature>(addresses, exactMatch: false).Addresses);
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new MondayWebhookHost(app, client, logs, secrets, clock);
    }

    /// <summary>The path of a connection's endpoint, as monday would call it.</summary>
    internal static string PathOf(Guid connectionId) => "/api/connectors/monday/webhooks/" + connectionId.ToString("D");

    /// <summary>Posts a JSON body, with the given <c>Authorization</c> value when there is one.</summary>
    internal Task<HttpResponseMessage> PostAsync(string path, string body, string? authorization = null, Action<HttpRequestMessage>? adjust = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        adjust?.Invoke(request);
        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Posts an event with a token minted now, by the host's clock, for the connection's own audience.</summary>
    internal Task<HttpResponseMessage> PostEventAsync(SeededConnection connection, string body, string? account = null, string? userId = null) =>
        PostAsync(
            PathOf(connection.Connection),
            body,
            WebhookTestTokens.Mint(connection.Connection, account ?? connection.Account, Clock.UtcNow, userId ?? "4242"));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
    }
}

/// <summary>Seeds monday connections as 3B leaves them, and changes their state as 3B or an administrator would.</summary>
internal static class WebhookData
{
    /// <summary>A new active tenant with an active user who authorized a new monday connection for the account.</summary>
    internal static async Task<SeededConnection> NewMondayConnectionAsync(DatabaseFixture fixture, CancellationToken cancellationToken, string account = "9001")
    {
        var tenant = await ConnectorAuthorizationData.NewTenantAsync(fixture, cancellationToken);
        var user = await ConnectorAuthorizationData.SeedUserAsync(fixture, tenant, cancellationToken);
        var grant = ConnectorAuthorizationData.Grant(user, account: account);
        await using var scoped = ConnectorAuthorizationData.Store(fixture, tenant);
        var activated = await scoped.Store.ActivateAsync(grant, cancellationToken);
        Assert.True(activated.IsSuccess && activated.Value, activated.IsSuccess ? "not activated" : activated.Error.Code);
        return new SeededConnection(tenant, user, grant.ConnectionId, account);
    }

    internal static async Task DisconnectAsync(DatabaseFixture fixture, SeededConnection connection, CancellationToken cancellationToken)
    {
        await using var scoped = ConnectorAuthorizationData.Store(fixture, connection.Tenant);
        var disconnected = await scoped.Store.DisconnectAsync(connection.Connection, connection.User, cancellationToken);
        Assert.True(disconnected.IsSuccess, disconnected.IsSuccess ? string.Empty : disconnected.Error.Code);
    }

    internal static async Task RequireReauthorizationAsync(DatabaseFixture fixture, SeededConnection connection, CancellationToken cancellationToken)
    {
        await using var scoped = ConnectorAuthorizationData.Store(fixture, connection.Tenant);
        var required = await scoped.Store.RequireReauthorizationAsync(connection.Connection, 1, null, cancellationToken);
        Assert.True(required.IsSuccess && required.Value, required.IsSuccess ? "not changed" : required.Error.Code);
    }

    /// <summary>Runs one statement as the schema owner, as an administrator's change would.</summary>
    internal static async Task ExecuteAsAdministratorAsync(DatabaseFixture fixture, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    /// <summary>
    /// Every text-like and binary column of every table in the schema, searched for every sentinel. A finding names the
    /// sentinel by its position only, so a failure never prints a secret.
    /// </summary>
    internal static async Task<List<string>> FindAnywhereAsync(DatabaseFixture fixture, IReadOnlyList<string> sentinels, CancellationToken cancellationToken)
    {
        await using var admin = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        var columns = new List<(string Table, string Column, string Type)>();
        await using (var list = new NpgsqlCommand(
            """
            SELECT c.table_name, c.column_name, c.data_type
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema = 'public' AND c.data_type IN ('text', 'character varying', 'jsonb', 'json', 'bytea');
            """,
            admin))
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        Assert.Contains(columns, column => column.Table == "raw_receipts" && column.Column == "payload");
        var found = new List<string>();
        foreach (var (table, column, type) in columns)
        {
            var predicate = type == "bytea"
                ? $"position(convert_to(v.value, 'UTF8') in t.\"{column}\") > 0"
                : $"position(v.value in t.\"{column}\"::text) > 0";
            await using var search = new NpgsqlCommand(
                $"SELECT DISTINCT v.ordinality FROM public.\"{table}\" AS t, unnest(@values) WITH ORDINALITY AS v(value, ordinality) WHERE {predicate};",
                admin);
            search.Parameters.AddWithValue("values", sentinels.ToArray());
            await using var reader = await search.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add($"{table}.{column} holds sentinel #{reader.GetInt64(0)}");
            }
        }

        return found;
    }

    /// <summary>A monday-shaped event body; the pulse name is provider text carrying the marker.</summary>
    internal static string EventBody(string marker, string? triggerUuid, string extraEvent = "", string extraRoot = "") =>
        "{\"event\":{\"app\":\"monday\",\"type\":\"update_column_value\",\"triggerTime\":\"2026-09-30T11:59:59.512Z\",\"subscriptionId\":73019,"
        + "\"userId\":58201774,\"originalTriggerUuid\":null,\"boardId\":4401,\"groupId\":\"topics\",\"pulseId\":8812,\"pulseName\":\""
        + marker + "\",\"columnId\":\"status\",\"columnType\":\"color\",\"value\":{\"label\":{\"index\":1,\"text\":\"Done\"}},"
        + "\"previousValue\":null,\"changedAt\":1727697599.4971,\"isTopGroup\":true"
        + (triggerUuid is null ? string.Empty : ",\"triggerUuid\":\"" + triggerUuid + "\"") + extraEvent + "}" + extraRoot + "}";

    internal static string NewTrigger() => Guid.NewGuid().ToString("N");

    /// <summary>A stream of unknown length, so a request body goes out chunked, without a declared size.</summary>
    internal sealed class UnknownLengthStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>The media type header of a request body.</summary>
    internal static MediaTypeHeaderValue MediaType(string value) => MediaTypeHeaderValue.Parse(value);
}
