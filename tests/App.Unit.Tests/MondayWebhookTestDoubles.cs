using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// A deterministic fake of monday's webhook tokens: HS256 JWTs signed with obviously fake Signing Secrets, for an
/// audience made from the test base address. Nothing here is a real monday secret, token or account.
/// </summary>
internal static class WebhookTokens
{
    /// <summary>The Signing Secret the tests place in secret storage. It protects nothing.</summary>
    internal const string SigningSecret = "test-signing-secret-A";

    /// <summary>Another app's Signing Secret. It protects nothing.</summary>
    internal const string OtherSecret = "test-signing-secret-B";

    internal const string Account = "9001";

    internal const string OtherAccount = "9002";

    /// <summary>A marker standing in for the credential monday puts in the token; it must never surface anywhere.</summary>
    internal const string ShortLivedMarker = "short-lived-token-marker-7f3a";

    internal const string HeaderJson = """{"alg":"HS256","typ":"JWT"}""";

    internal static readonly Uri BaseAddress = new("https://fully.example.test");

    internal static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    internal static MondayWebhookOptions Options { get; } = new(BaseAddress);

    internal static string Audience(Guid connectionId) =>
        "https://fully.example.test/api/connectors/monday/webhooks/" + connectionId.ToString("D");

    /// <summary>The claims monday documents, as JSON, valid at <see cref="Now"/> for five minutes unless told otherwise.</summary>
    internal static string Claims(
        Guid connectionId,
        string account = Account,
        DateTimeOffset? issuedAt = null,
        TimeSpan? lifetime = null,
        string userId = "4242",
        string? audienceJson = null,
        string extra = "")
    {
        var iat = (issuedAt ?? Now).ToUnixTimeSeconds();
        var exp = (issuedAt ?? Now).Add(lifetime ?? TimeSpan.FromMinutes(5)).ToUnixTimeSeconds();
        var aud = audienceJson ?? "\"" + Audience(connectionId) + "\"";
        return "{\"accountId\":" + account + ",\"userId\":" + userId + ",\"aud\":" + aud + ",\"exp\":" + exp + ",\"iat\":" + iat
            + ",\"shortLivedToken\":\"" + ShortLivedMarker + "\"" + extra + "}";
    }

    /// <summary>A token for the claims, signed with the secret.</summary>
    internal static string Mint(string claimsJson, string secret = SigningSecret, string headerJson = HeaderJson) =>
        MintRaw(Encoding.UTF8.GetBytes(headerJson), Encoding.UTF8.GetBytes(claimsJson), Encoding.UTF8.GetBytes(secret));

    internal static string MintRaw(byte[] header, byte[] claims, byte[] secret)
    {
        var signingInput = Base64Url.EncodeToString(header) + "." + Base64Url.EncodeToString(claims);
        return signingInput + "." + Base64Url.EncodeToString(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(signingInput)));
    }

    /// <summary>A valid token for the connection.</summary>
    internal static string Valid(Guid connectionId, string account = Account) => Mint(Claims(connectionId, account));

    /// <summary>Replaces the claims segment of a signed token, keeping its header and signature.</summary>
    internal static string WithClaims(string token, string claimsJson)
    {
        var parts = token.Split('.');
        return parts[0] + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claimsJson)) + "." + parts[2];
    }
}

/// <summary>An authenticator over in-memory secret storage and a clock the test moves.</summary>
internal sealed class WebhookHarness
{
    internal WebhookHarness(MondayWebhookOptions? options = null, bool withSecret = true, bool unconfigured = false)
    {
        if (withSecret)
        {
            Secrets.Put(MondaySecretName.SigningSecret, Encoding.ASCII.GetBytes(WebhookTokens.SigningSecret));
        }

        Options = unconfigured ? null : options ?? WebhookTokens.Options;
    }

    internal InMemorySecretStore Secrets { get; } = new();

    internal MutableClock Clock { get; } = new(WebhookTokens.Now);

    internal MondayWebhookOptions? Options { get; }

    internal Guid Connection { get; } = Guid.CreateVersion7();

    /// <summary>A new authenticator, as a new request scope gets one.</summary>
    internal MondayWebhookAuthenticator Authenticator() => new(Secrets, Clock, Options);

    internal Task<MondayWebhookAuthentication> AuthenticateAsync(string? authorization, Guid? connectionId = null) =>
        Authenticator().AuthenticateAsync(connectionId ?? Connection, authorization, TestContext.Current.CancellationToken).AsTask();
}

/// <summary>
/// The monday verifier over the in-memory authorization store: one trusted tenant with one active monday connection,
/// authorized by an active canonical user for account <see cref="WebhookTokens.Account"/>.
/// </summary>
internal sealed class VerifierHarness
{
    internal VerifierHarness()
    {
        Database.Users[User] = (Tenant, true);
        Database.Connections[Connection] = new InMemoryAuthorizationDatabase.StoredConnection { Tenant = Tenant, Source = "monday" };
        Database.Authorizations[Connection] = new InMemoryAuthorizationDatabase.StoredAuthorization
        {
            Tenant = Tenant,
            ConnectionId = Connection,
            User = User,
            Subject = "58201774",
            Account = WebhookTokens.Account,
            MaterialVersion = Guid.CreateVersion7(),
            AccessExpiresAt = WebhookTokens.Now.AddHours(1),
            Scopes = "boards:read me:read",
        };
        Store = new InMemoryAuthorizationStore(Database, Tenant);
    }

    internal TenantId Tenant { get; } = TenantId.New();

    internal UserId User { get; } = UserId.New();

    internal Guid Connection { get; } = Guid.CreateVersion7();

    internal InMemoryAuthorizationDatabase Database { get; } = new();

    internal InMemoryAuthorizationStore Store { get; }

    internal WebhookHarness Webhooks { get; } = new();

    internal InMemoryAuthorizationDatabase.StoredAuthorization Authorization => Database.Authorizations[Connection];

    /// <summary>The verifier as ingress uses it, in the given tenant context (the connection's own by default).</summary>
    internal MondayWebhookVerifier Verifier(ITenantContext? context = null) => new(
        Webhooks.Authenticator(),
        Store,
        new FixedTenantContextAccessor(context ?? TenantContext.Resolved(Tenant, TenantResolutionSource.TrustedServiceIdentity)));

    /// <summary>A webhook delivery on the connection, carrying the token as its proof.</summary>
    internal WorkContinuity.Core.Ingress.IngressSubmission Delivery(
        string? token,
        Guid? connectionId = null,
        string sourceSystem = "monday",
        string schemaVersion = MondayWebhooks.SchemaVersion) =>
        new(
            connectionId ?? Connection,
            sourceSystem,
            "trigger-1",
            schemaVersion,
            """{"event":{"type":"create_item","triggerUuid":"trigger-1"}}""",
            WorkContinuity.Core.Correlation.CorrelationId.New(),
            token);

    /// <summary>Adds a second connection in the same tenant, authorized for another account.</summary>
    internal Guid AddConnection(string account, ConnectorAuthorizationState state = ConnectorAuthorizationState.Active)
    {
        var connection = Guid.CreateVersion7();
        Database.Connections[connection] = new InMemoryAuthorizationDatabase.StoredConnection { Tenant = Tenant, Source = "monday" };
        Database.Authorizations[connection] = new InMemoryAuthorizationDatabase.StoredAuthorization
        {
            Tenant = Tenant,
            ConnectionId = connection,
            User = User,
            State = state,
            Subject = "58201774",
            Account = account,
            MaterialVersion = Guid.CreateVersion7(),
            AccessExpiresAt = WebhookTokens.Now.AddHours(1),
            Scopes = "boards:read me:read",
        };
        return connection;
    }
}
