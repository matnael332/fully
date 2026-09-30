using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// A deterministic fake of monday's OAuth 2.1 token and revocation endpoints and its API endpoint, routed by address.
/// Every request is recorded; a token or API request nobody scripted fails the test. Revocations succeed by default.
/// </summary>
internal sealed class FakeMondayAuthServer : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _token = new();
    private readonly ConcurrentQueue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _api = new();

    internal ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    internal Func<HttpRequestMessage, HttpResponseMessage> Revoke { get; set; } =
        _ => Json(HttpStatusCode.OK, """{"success":true}""");

    internal Func<HttpRequestMessage, HttpResponseMessage>? ApiDefault { get; set; }

    internal IEnumerable<RecordedRequest> TokenRequests => Requests.Where(request => request.Uri == MondayEndpoints.Token);

    internal IEnumerable<RecordedRequest> RevokeRequests => Requests.Where(request => request.Uri == MondayEndpoints.Revoke);

    internal IEnumerable<RecordedRequest> ApiRequests => Requests.Where(request => request.Uri == MondayEndpoints.Api);

    internal FakeMondayAuthServer ThenToken(HttpStatusCode status, string body)
    {
        _token.Enqueue((_, _) => Task.FromResult(Json(status, body)));
        return this;
    }

    /// <summary>Scripts one token answer; the responder sees the request's cancellation, so a timeout can be real.</summary>
    internal FakeMondayAuthServer ThenToken(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _token.Enqueue(respond);
        return this;
    }

    internal FakeMondayAuthServer ThenApi(HttpStatusCode status, string body)
    {
        _api.Enqueue(_ => Task.FromResult(Json(status, body)));
        return this;
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

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
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri, headers, body));
        cancellationToken.ThrowIfCancellationRequested();

        if (request.RequestUri == MondayEndpoints.Revoke)
        {
            return Revoke(request);
        }

        if (request.RequestUri == MondayEndpoints.Token)
        {
            return _token.TryDequeue(out var token)
                ? await token(request, cancellationToken)
                : throw new InvalidOperationException("The fake token endpoint received a request nobody scripted.");
        }

        if (request.RequestUri == MondayEndpoints.Api)
        {
            return _api.TryDequeue(out var api)
                ? await api(request)
                : ApiDefault?.Invoke(request) ?? throw new InvalidOperationException("The fake API received a request nobody scripted.");
        }

        throw new InvalidOperationException("A request went to an address the connector must never call.");
    }
}

/// <summary>Deterministic in-memory secret storage with failure injection. Values are kept exactly as set.</summary>
internal sealed class InMemorySecretStore : IMondaySecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    internal Func<MondaySecretName, MondaySecretStoreStatus?> SetOverride { get; set; } = _ => null;

    internal Func<MondaySecretName, MondaySecretStoreStatus?> GetOverride { get; set; } = _ => null;

    internal Func<MondaySecretName, MondaySecretStoreStatus?> DeleteOverride { get; set; } = _ => null;

    internal ConcurrentQueue<string> Operations { get; } = new();

    internal IReadOnlyCollection<string> Names => _values.Keys.ToArray();

    internal void Put(MondaySecretName name, byte[] value) => _values[name.Value] = value;

    /// <summary>Places a value under a name the connector used, as a swapped or tampered value would be.</summary>
    internal void Put(string name, byte[] value) => _values[name] = value;

    internal void PutClientSecret(string value) => Put(MondaySecretName.ClientSecret, Encoding.ASCII.GetBytes(value));

    internal byte[]? Peek(string name) => _values.TryGetValue(name, out var value) ? value : null;

    internal void Remove(string name) => _values.TryRemove(name, out _);

    internal string AllText() => string.Join("\n", _values.Values.Select(Encoding.UTF8.GetString));

    public ValueTask<MondaySecretStoreStatus> SetAsync(MondaySecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        Operations.Enqueue("set " + name.Value);
        if (SetOverride(name) is { } forced)
        {
            return ValueTask.FromResult(forced);
        }

        _values[name.Value] = value.ToArray();
        return ValueTask.FromResult(MondaySecretStoreStatus.Succeeded);
    }

    public ValueTask<MondaySecretLookup> GetAsync(MondaySecretName name, CancellationToken cancellationToken)
    {
        Operations.Enqueue("get " + name.Value);
        return ValueTask.FromResult(GetOverride(name) switch
        {
            MondaySecretStoreStatus.Unavailable => MondaySecretLookup.Unavailable(),
            MondaySecretStoreStatus.NotFound => MondaySecretLookup.Missing(),
            _ => _values.TryGetValue(name.Value, out var value) ? MondaySecretLookup.Found(value) : MondaySecretLookup.Missing(),
        });
    }

    public ValueTask<MondaySecretStoreStatus> DeleteAsync(MondaySecretName name, CancellationToken cancellationToken)
    {
        Operations.Enqueue("delete " + name.Value);
        if (DeleteOverride(name) is { } forced)
        {
            return ValueTask.FromResult(forced);
        }

        return ValueTask.FromResult(_values.TryRemove(name.Value, out _) ? MondaySecretStoreStatus.Succeeded : MondaySecretStoreStatus.NotFound);
    }
}

/// <summary>
/// The shared state of the in-memory authorization store: every tenant's rows, and a database clock.
/// </summary>
internal sealed class InMemoryAuthorizationDatabase
{
    internal readonly object Gate = new();

    internal DateTimeOffset Now { get; set; } = MondayHarness.Now;

    internal Dictionary<UserId, (TenantId Tenant, bool Active)> Users { get; } = [];

    internal Dictionary<Guid, StoredConnection> Connections { get; } = [];

    internal Dictionary<Guid, StoredAuthorization> Authorizations { get; } = [];

    internal Dictionary<Guid, StoredTransaction> Transactions { get; } = [];

    internal sealed class StoredConnection
    {
        internal required TenantId Tenant { get; init; }

        internal required string Source { get; init; }

        internal bool Enabled { get; set; } = true;
    }

    internal sealed class StoredAuthorization
    {
        internal required TenantId Tenant { get; init; }

        internal required Guid ConnectionId { get; init; }

        internal required UserId User { get; init; }

        internal ConnectorAuthorizationState State { get; set; } = ConnectorAuthorizationState.Active;

        internal required string Subject { get; init; }

        internal required string Account { get; init; }

        internal long Generation { get; set; } = 1;

        internal Guid MaterialVersion { get; set; }

        internal DateTimeOffset AccessExpiresAt { get; set; }

        internal string Scopes { get; set; } = string.Empty;

        internal Guid? Claim { get; set; }

        internal DateTimeOffset? ClaimedAt { get; set; }

        internal bool? RevocationConfirmed { get; set; }
    }

    internal sealed class StoredTransaction
    {
        internal required TenantId Tenant { get; init; }

        internal required ConnectorAuthorizationTransaction Transaction { get; init; }

        internal required DateTimeOffset ExpiresAt { get; init; }

        internal DateTimeOffset? ConsumedAt { get; set; }
    }
}

/// <summary>
/// An in-memory <see cref="IConnectorAuthorizationStore"/> with the real store's semantics: bound to one trusted
/// tenant, compare-and-swap on every change, the database clock for times. A test can fail any operation before it
/// changes anything, or after it did, to model an unknown commit outcome.
/// </summary>
internal sealed class InMemoryAuthorizationStore(InMemoryAuthorizationDatabase database, TenantId tenant) : IConnectorAuthorizationStore
{
    private static readonly Error Unavailable = Error.Infrastructure("persistence.unavailable", "The database is unavailable.");

    internal Func<string, bool> FailBefore { get; set; } = _ => false;

    internal Func<string, bool> FailAfter { get; set; } = _ => false;

    internal ConcurrentQueue<string> Calls { get; } = new();

    public Task<Result<bool>> OpenTransactionAsync(ConnectorAuthorizationTransaction transaction, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Run(nameof(OpenTransactionAsync), () =>
        {
            if (lifetime <= TimeSpan.Zero || lifetime > ConnectorAuthorizationTransaction.MaxLifetime)
            {
                throw new InvalidOperationException("The lifetime is out of range.");
            }

            if (!IsActiveUser(transaction.CanonicalUser))
            {
                return false;
            }

            database.Transactions.Add(transaction.TransactionId, new InMemoryAuthorizationDatabase.StoredTransaction
            {
                Tenant = tenant,
                Transaction = transaction,
                ExpiresAt = database.Now + lifetime,
            });
            return true;
        });

    public Task<Result<ConnectorTransactionConsumption>> ConsumeTransactionAsync(
        Guid transactionId,
        UserId canonicalUser,
        ReadOnlyMemory<byte> stateHash,
        CancellationToken cancellationToken) =>
        Run(nameof(ConsumeTransactionAsync), () =>
        {
            if (!database.Transactions.TryGetValue(transactionId, out var stored) || stored.Tenant != tenant)
            {
                return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.NotFound);
            }

            if (stored.ExpiresAt <= database.Now)
            {
                return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.Expired);
            }

            if (stored.Transaction.CanonicalUser != canonicalUser)
            {
                return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.BindingMismatch);
            }

            if (!CryptographicOperations.FixedTimeEquals(stored.Transaction.StateHash.Span, stateHash.Span))
            {
                return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.StateMismatch);
            }

            if (stored.ConsumedAt is not null)
            {
                return ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.AlreadyConsumed);
            }

            stored.ConsumedAt = database.Now;
            return ConnectorTransactionConsumption.Consumed(stored.Transaction);
        });

    public Task<Result<IReadOnlyList<Guid>>> CloseExpiredTransactionsAsync(int limit, CancellationToken cancellationToken) =>
        Run<IReadOnlyList<Guid>>(nameof(CloseExpiredTransactionsAsync), () =>
        {
            var due = database.Transactions.Values
                .Where(stored => stored.Tenant == tenant && stored.ConsumedAt is null && stored.ExpiresAt <= database.Now)
                .OrderBy(stored => stored.ExpiresAt)
                .Take(limit)
                .ToList();
            due.ForEach(stored => stored.ConsumedAt = database.Now);
            return due.Select(stored => stored.Transaction.TransactionId).ToList();
        });

    public Task<Result<ConnectorAuthorization?>> ReadAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Run(nameof(ReadAsync), () => Visible(connectionId) is { } row ? Project(row) : null);

    public Task<Result<bool>> ActivateAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken) =>
        Run(nameof(ActivateAsync), () =>
        {
            if (!IsActiveUser(grant.CanonicalUser))
            {
                return false;
            }

            if (database.Connections.ContainsKey(grant.ConnectionId))
            {
                throw new InvalidOperationException("duplicate connection");
            }

            database.Connections.Add(grant.ConnectionId, new InMemoryAuthorizationDatabase.StoredConnection { Tenant = tenant, Source = grant.SourceSystem });
            database.Authorizations.Add(grant.ConnectionId, new InMemoryAuthorizationDatabase.StoredAuthorization
            {
                Tenant = tenant,
                ConnectionId = grant.ConnectionId,
                User = grant.CanonicalUser,
                Subject = grant.ProviderSubject,
                Account = grant.ProviderAccount,
                MaterialVersion = grant.MaterialVersion,
                AccessExpiresAt = grant.AccessExpiresAt,
                Scopes = grant.GrantedScopes,
            });
            return true;
        });

    public Task<Result<bool>> ReauthorizeAsync(ConnectorAuthorizationGrant grant, CancellationToken cancellationToken) =>
        Run(nameof(ReauthorizeAsync), () =>
        {
            if (Visible(grant.ConnectionId) is not { } row
                || row.State != ConnectorAuthorizationState.ReauthorizationRequired
                || row.User != grant.CanonicalUser
                || row.Subject != grant.ProviderSubject
                || row.Account != grant.ProviderAccount
                || !database.Connections[grant.ConnectionId].Enabled
                || !IsActiveUser(row.User))
            {
                return false;
            }

            row.State = ConnectorAuthorizationState.Active;
            row.Generation++;
            row.MaterialVersion = grant.MaterialVersion;
            row.AccessExpiresAt = grant.AccessExpiresAt;
            row.Scopes = grant.GrantedScopes;
            row.Claim = null;
            row.ClaimedAt = null;
            return true;
        });

    public Task<Result<bool>> ClaimRefreshAsync(Guid connectionId, long generation, Guid claim, CancellationToken cancellationToken) =>
        Run(nameof(ClaimRefreshAsync), () =>
        {
            if (Visible(connectionId) is not { State: ConnectorAuthorizationState.Active, Claim: null } row || row.Generation != generation)
            {
                return false;
            }

            row.Claim = claim;
            row.ClaimedAt = database.Now;
            return true;
        });

    public Task<Result<bool>> CompleteRefreshAsync(
        Guid connectionId,
        long generation,
        Guid claim,
        DateTimeOffset accessExpiresAt,
        string grantedScopes,
        CancellationToken cancellationToken) =>
        Run(nameof(CompleteRefreshAsync), () =>
        {
            if (Visible(connectionId) is not { State: ConnectorAuthorizationState.Active } row || row.Generation != generation || row.Claim != claim)
            {
                return false;
            }

            row.Generation++;
            row.MaterialVersion = claim;
            row.AccessExpiresAt = accessExpiresAt;
            row.Scopes = grantedScopes;
            row.Claim = null;
            row.ClaimedAt = null;
            return true;
        });

    public Task<Result<bool>> ReleaseRefreshAsync(Guid connectionId, Guid claim, CancellationToken cancellationToken) =>
        Run(nameof(ReleaseRefreshAsync), () =>
        {
            if (Visible(connectionId) is not { } row || row.Claim != claim)
            {
                return false;
            }

            row.Claim = null;
            row.ClaimedAt = null;
            return true;
        });

    public Task<Result<bool>> RequireReauthorizationAsync(Guid connectionId, long generation, Guid? claim, CancellationToken cancellationToken) =>
        Run(nameof(RequireReauthorizationAsync), () =>
        {
            if (Visible(connectionId) is not { State: ConnectorAuthorizationState.Active } row || row.Generation != generation || row.Claim != claim)
            {
                return false;
            }

            row.State = ConnectorAuthorizationState.ReauthorizationRequired;
            row.Claim = null;
            row.ClaimedAt = null;
            return true;
        });

    public Task<Result<ConnectorDisconnection>> DisconnectAsync(Guid connectionId, UserId requestedBy, CancellationToken cancellationToken) =>
        Run(nameof(DisconnectAsync), () =>
        {
            if (Visible(connectionId) is not { } row)
            {
                return ConnectorDisconnection.Unchanged(ConnectorDisconnectOutcome.NotFound);
            }

            if (row.User != requestedBy)
            {
                return ConnectorDisconnection.Unchanged(ConnectorDisconnectOutcome.NotPermitted);
            }

            if (row.State == ConnectorAuthorizationState.Revoked)
            {
                return ConnectorDisconnection.Unchanged(ConnectorDisconnectOutcome.AlreadyDisconnected);
            }

            var pending = row.Claim;
            row.State = ConnectorAuthorizationState.Revoked;
            row.Claim = null;
            row.ClaimedAt = null;
            database.Connections[connectionId].Enabled = false;
            return ConnectorDisconnection.Disconnected(row.MaterialVersion, pending);
        });

    public Task<Result<bool>> RecordProviderRevocationAsync(Guid connectionId, bool confirmed, CancellationToken cancellationToken) =>
        Run(nameof(RecordProviderRevocationAsync), () =>
        {
            if (Visible(connectionId) is not { State: ConnectorAuthorizationState.Revoked } row)
            {
                return false;
            }

            row.RevocationConfirmed = confirmed;
            return true;
        });

    private Task<Result<T>> Run<T>(string operation, Func<T> work)
    {
        Calls.Enqueue(operation);
        lock (database.Gate)
        {
            if (FailBefore(operation))
            {
                return Task.FromResult(Result<T>.Failure(Unavailable));
            }

            try
            {
                var value = work();
                return Task.FromResult(FailAfter(operation) ? Result<T>.Failure(Unavailable) : Result<T>.Success(value));
            }
            catch (InvalidOperationException)
            {
                return Task.FromResult(Result<T>.Failure(Unavailable));
            }
        }
    }

    private bool IsActiveUser(UserId user) =>
        database.Users.TryGetValue(user, out var member) && member.Tenant == tenant && member.Active;

    private InMemoryAuthorizationDatabase.StoredAuthorization? Visible(Guid connectionId) =>
        database.Authorizations.TryGetValue(connectionId, out var row) && row.Tenant == tenant ? row : null;

    private ConnectorAuthorization Project(InMemoryAuthorizationDatabase.StoredAuthorization row) => new(
        row.Tenant,
        row.ConnectionId,
        database.Connections[row.ConnectionId].Source,
        database.Connections[row.ConnectionId].Enabled,
        row.User,
        IsActiveUser(row.User),
        row.State,
        row.Subject,
        row.Account,
        row.Generation,
        row.MaterialVersion,
        row.AccessExpiresAt,
        row.Scopes,
        row.Claim,
        row.ClaimedAt is { } claimedAt ? database.Now - claimedAt : null);
}

/// <summary>A clock a test moves.</summary>
internal sealed class MutableClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>A trusted tenant context fixed for the scope, or none.</summary>
internal sealed class FixedTenantContextAccessor(ITenantContext current) : ITenantContextAccessor
{
    public ITenantContext Current { get; } = current;

    public void SetResolved(ITenantContext tenantContext) => throw new NotSupportedException("The test fixes the tenant context.");
}

/// <summary>Obviously fake JWT access tokens and monday-shaped token responses.</summary>
internal static class TestTokens
{
    /// <summary>An unsigned, obviously fake JWT whose <c>exp</c> is the given instant.</summary>
    internal static string Access(DateTimeOffset expiresAt, string marker) => Jwt($$"""{"exp":{{expiresAt.ToUnixTimeSeconds()}},"jti":"{{marker}}"}""");

    internal static string Jwt(string payloadJson) =>
        Base64Url.EncodeToString("""{"alg":"none","typ":"JWT"}"""u8) + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson)) + ".not-a-signature";

    internal static string Response(string access, string refresh, string scope = "boards:read me:read", string tokenType = "Bearer", string extra = "") =>
        "{\"access_token\":\"" + access + "\",\"refresh_token\":\"" + refresh + "\",\"token_type\":\"" + tokenType + "\",\"scope\":\"" + scope + "\"" + extra + "}";

    internal static string Me(string userId = "58201774", string accountId = "9001") =>
        "{\"data\":{\"me\":{\"id\":\"" + userId + "\",\"account\":{\"id\":\"" + accountId + "\"}}},\"account_id\":" + accountId + "}";

    internal static JsonElement Body(RecordedRequest request) => JsonDocument.Parse(request.Body).RootElement.Clone();
}

/// <summary>
/// The monday connection lifecycle wired to the fakes: one tenant, one active canonical user, the fake monday endpoints,
/// in-memory secret storage with the client secret in place, and the in-memory store.
/// </summary>
internal sealed class OAuthHarness
{
    internal const string ClientId = "wce-test-client";
    internal const string ClientSecretValue = "test-client-credential-not-real";
    internal static readonly Uri Redirect = new("https://app.example.test/connectors/monday/callback");

    internal OAuthHarness(MondayOAuthOptions? options = null, TenantId? tenant = null)
    {
        Tenant = tenant ?? MondayHarness.Tenant;
        Options = options ?? new MondayOAuthOptions(ClientId, Redirect);
        Database = new InMemoryAuthorizationDatabase();
        Store = new InMemoryAuthorizationStore(Database, Tenant);
        User = AddUser(Tenant);
        Secrets.PutClientSecret(ClientSecretValue);
        Clock = new MutableClock(MondayHarness.Now);
        Http = new HttpClient(Server);
        TenantContext = new FixedTenantContextAccessor(Core.Tenancy.TenantContext.Resolved(Tenant, TenantResolutionSource.AuthenticatedPrincipal));
        Credentials = new MondayConnectionCredentialSource(Store, Secrets, Options, Http, Clock, Delay);
        Reader = new MondayReadClient(Http, Credentials, Clock, Delay, () => 0.5);
        Authorizer = new MondayConnectionAuthorizer(Store, Secrets, Options, Http, Reader, TenantContext, Clock);
        Server.ApiDefault = _ => FakeMondayAuthServer.Json(HttpStatusCode.OK, TestTokens.Me());
    }

    internal TenantId Tenant { get; }

    internal MondayOAuthOptions Options { get; }

    internal InMemoryAuthorizationDatabase Database { get; }

    internal InMemoryAuthorizationStore Store { get; }

    internal InMemorySecretStore Secrets { get; } = new();

    internal FakeMondayAuthServer Server { get; } = new();

    internal RecordingDelay Delay { get; } = new();

    internal MutableClock Clock { get; }

    internal HttpClient Http { get; }

    internal FixedTenantContextAccessor TenantContext { get; }

    internal MondayConnectionCredentialSource Credentials { get; }

    internal MondayReadClient Reader { get; }

    internal MondayConnectionAuthorizer Authorizer { get; }

    internal UserId User { get; }

    internal Principal Principal => PrincipalOf(User);

    internal static Principal PrincipalOf(UserId user) =>
        Core.Identity.Principal.Authenticated(user, new ExternalIdentity(IdentityProviderId.EntraId, "entra-" + user.Value.ToString("N")));

    internal UserId AddUser(TenantId tenant, bool active = true)
    {
        var user = UserId.New();
        lock (Database.Gate)
        {
            Database.Users[user] = (tenant, active);
        }

        return user;
    }

    internal void SetUserActive(UserId user, bool active)
    {
        lock (Database.Gate)
        {
            Database.Users[user] = (Database.Users[user].Tenant, active);
        }
    }

    internal InMemoryAuthorizationDatabase.StoredAuthorization Row(Guid connectionId)
    {
        lock (Database.Gate)
        {
            return Database.Authorizations[connectionId];
        }
    }

    internal MondayRequestContext Context(Guid connectionId) => new(Tenant, connectionId, CorrelationId.New());

    internal async Task<MondayAuthorizationRedirect> StartAsync(Guid? reauthorize = null)
    {
        var started = await Authorizer.StartAsync(Principal, reauthorize, TestContext.Current.CancellationToken);
        Assert.True(started.IsSuccess, started.IsSuccess ? string.Empty : started.Error.Code);
        return started.Value;
    }

    internal static IReadOnlyDictionary<string, string> Query(Uri address) =>
        address.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

    internal static MondayAuthorizationCallback CallbackFor(
        MondayAuthorizationRedirect redirect,
        string? code = "auth-code-not-real",
        string? error = null,
        string? issuer = null,
        string? state = null) =>
        new(state ?? Query(redirect.AuthorizationUri)["state"], code, error, issuer);

    /// <summary>Scripts one successful exchange: a pair expiring an hour from now, then the identity read.</summary>
    internal (string Access, string Refresh) ScriptExchange(string marker = "1", string userId = "58201774", string accountId = "9001", TimeSpan? lifetime = null)
    {
        var access = TestTokens.Access(Clock.UtcNow + (lifetime ?? TimeSpan.FromHours(1)), "access-" + marker);
        var refresh = "refresh-token-not-real-" + marker;
        Server.ThenToken(HttpStatusCode.OK, TestTokens.Response(access, refresh));
        Server.ThenApi(HttpStatusCode.OK, TestTokens.Me(userId, accountId));
        return (access, refresh);
    }

    /// <summary>Authorizes a new connection end to end through the fakes.</summary>
    internal async Task<(Guid ConnectionId, string Access, string Refresh)> ConnectAsync(string marker = "1", TimeSpan? lifetime = null)
    {
        var redirect = await StartAsync();
        var (access, refresh) = ScriptExchange(marker, lifetime: lifetime);
        var completed = await Authorizer.CompleteAsync(Principal, CallbackFor(redirect), TestContext.Current.CancellationToken);
        Assert.True(completed.IsSuccess, completed.IsSuccess ? string.Empty : completed.Error.Code);
        return (redirect.ConnectionId, access, refresh);
    }
}
