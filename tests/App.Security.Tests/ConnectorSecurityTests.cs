using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Gate 3A security properties of the connector contract and the monday client: credentials stay inside the connector
/// and never render, serialize or reach Core; no failure carries a token, a provider message or an exception's text;
/// a disabled, revoked, unavailable, foreign or expired credential fails closed before anything is sent, including
/// between attempts; and nothing but a declared, single read query can be sent.
/// </summary>
public sealed class ConnectorSecurityTests
{
    private const string Token = "test-token-not-a-secret";
    private const string Sentinel = "SENTINEL-provider-content-7f3a";

    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("0192a3b4-0000-7000-8000-0000000005a1"));
    private static readonly Guid Connection = Guid.Parse("0192a3b4-0000-7000-8000-0000000005c1");
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectorIdentity Identity = new(Tenant, Connection, ConnectorIdentityKind.DelegatedUser, "58201774");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Core_declares_no_member_that_could_carry_a_credential()
    {
        string[] forbidden = ["accesstoken", "refreshtoken", "bearer", "apikey", "secret", "password", "credential", "privatekey"];
        const BindingFlags everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // A credential can only travel as text or bytes, so no textual member of Core may be named like one, and no Core
        // type may be. Error descriptors and failure kinds that merely name a credential problem carry none.
        Type[] textual = [typeof(string), typeof(char[]), typeof(byte[]), typeof(ReadOnlyMemory<char>), typeof(ReadOnlyMemory<byte>),
            typeof(Memory<char>), typeof(Memory<byte>)];

        var violations = new List<string>();
        foreach (var type in typeof(WorkContinuity.Core.AssemblyMarker).Assembly.GetTypes())
        {
            var members = type.GetFields(everything).Select(field => (field.Name, Type: field.FieldType))
                .Concat(type.GetProperties(everything).Select(property => (property.Name, Type: property.PropertyType)))
                .Concat(type.GetMethods(everything).SelectMany(method => method.GetParameters())
                    .Select(parameter => (Name: parameter.Name ?? string.Empty, Type: parameter.ParameterType)))
                .Concat(type.GetConstructors(everything).SelectMany(constructor => constructor.GetParameters())
                    .Select(parameter => (Name: parameter.Name ?? string.Empty, Type: parameter.ParameterType)));

            foreach (var (name, memberType) in members)
            {
                if (textual.Contains(memberType) && IsCredentialName(name, forbidden))
                {
                    violations.Add($"{type.FullName}.{name}");
                }
            }

            if (IsCredentialName(type.Name, forbidden))
            {
                violations.Add(type.FullName ?? type.Name);
            }
        }

        Assert.Empty(violations);
        Assert.True(IsCredentialName("AccessToken", forbidden));
        Assert.True(IsCredentialName("client_secret", forbidden));
    }

    [Fact]
    public void The_credential_never_renders_serializes_or_exposes_its_token()
    {
        var credential = new MondayAccessCredential(Token, Identity, Now.AddMinutes(30));

        Assert.DoesNotContain(Token, credential.ToString(), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(credential));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new { Holder = credential }));

        var publicProperties = typeof(MondayAccessCredential)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "ExpiresAt", "Identity" }, publicProperties);
        Assert.Empty(typeof(MondayAccessCredential).GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(
            typeof(MondayAccessCredential).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.ReturnType == typeof(string) && method.Name != nameof(ToString));
    }

    [Theory]
    [InlineData("token\r\nX-Injected: 1")]
    [InlineData("token with space")]
    [InlineData("token\ttab")]
    [InlineData("tokén")]
    [InlineData("")]
    public void A_token_that_could_break_or_inject_a_header_is_refused(string token)
    {
        Assert.Throws<ArgumentException>(() => new MondayAccessCredential(token, Identity, expiresAt: null));
        Assert.Throws<ArgumentException>(() => new MondayAccessCredential(
            new string('t', MondayAccessCredential.MaxTokenLength + 1), Identity, expiresAt: null));
    }

    [Fact]
    public async Task No_failure_or_result_carries_the_token_a_provider_message_or_provider_content()
    {
        var bodies = new[]
        {
            "{\"errors\":[{\"message\":\"" + Sentinel + " " + Token
                + "\",\"extensions\":{\"code\":\"InvalidArgumentException\",\"status_code\":200,\"error_data\":{\"echo\":\"" + Sentinel + "\"}}}]}",
            "{\"errors\":[{\"message\":\"" + Sentinel + "\",\"extensions\":{\"code\":\"" + Sentinel + "\"}}]}",
            "{\"data\":{\"items\":[{\"id\":\"7777\",\"name\":\"" + Sentinel + "\"}]}}",
            "<html>" + Sentinel + " " + Token + "</html>",
        };

        foreach (var body in bodies)
        {
            var (client, server, _) = Harness();
            server.Always(HttpStatusCode.OK, body);

            var result = await client.ReadItemsByIdAsync(Context(), ["1111"], Cancel);

            Assert.False(result.IsSuccess);
            foreach (var text in Renderings(result))
            {
                Assert.DoesNotContain(Sentinel, text, StringComparison.Ordinal);
                Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task An_exception_text_never_reaches_a_failure()
    {
        var (client, server, _) = Harness();
        server.AlwaysThrow(() => new HttpRequestException($"{Sentinel} {Token}"));

        var result = await client.ReadItemsByIdAsync(Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorFailureKind.NetworkTransient, result.Failure.Kind);
        foreach (var text in Renderings(result))
        {
            Assert.DoesNotContain(Sentinel, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string, ConnectorFailureKind, string> RefusedCredentials => new()
    {
        { "disabled", ConnectorFailureKind.ConnectionDisabled, ConnectorErrors.ConnectionDisabled.Code },
        { "revoked", ConnectorFailureKind.AuthenticationFailed, ConnectorErrors.CredentialRevoked.Code },
        { "unavailable", ConnectorFailureKind.CredentialUnavailable, ConnectorErrors.CredentialUnavailable.Code },
    };

    [Theory]
    [MemberData(nameof(RefusedCredentials))]
    public async Task A_disabled_revoked_or_unavailable_connection_fails_closed_without_any_request(
        string refusal,
        ConnectorFailureKind kind,
        string code)
    {
        var (client, server, credentials) = Harness();
        credentials.Answer = refusal switch
        {
            "disabled" => _ => MondayCredentialResult.ConnectionDisabled(),
            "revoked" => _ => MondayCredentialResult.Revoked(),
            _ => _ => MondayCredentialResult.Unavailable(),
        };

        var result = await client.ReadItemsByIdAsync(Context(), ["1111"], Cancel);

        Assert.Equal(kind, result.Failure.Kind);
        Assert.Equal(code, result.Failure.Error.Code);
        Assert.Equal(0, result.Attempts);
        Assert.Null(result.Identity);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_credential_for_another_tenant_or_connection_is_refused_before_sending()
    {
        foreach (var foreign in new[]
                 {
                     new ConnectorIdentity(TenantId.New(), Connection, ConnectorIdentityKind.DelegatedUser, "58201774"),
                     new ConnectorIdentity(Tenant, Guid.NewGuid(), ConnectorIdentityKind.DelegatedUser, "58201774"),
                 })
        {
            var (client, server, credentials) = Harness();
            credentials.Answer = _ => MondayCredentialResult.Acquired(new MondayAccessCredential(Token, foreign, Now.AddMinutes(30)));

            var result = await client.ReadItemsByIdAsync(Context(), ["1111"], Cancel);

            Assert.Equal(ConnectorErrors.CredentialMismatch, result.Failure.Error);
            Assert.Empty(server.Requests);
        }
    }

    [Fact]
    public async Task An_expired_credential_is_refused_before_sending()
    {
        var (client, server, credentials) = Harness();
        credentials.Answer = _ => MondayCredentialResult.Acquired(new MondayAccessCredential(Token, Identity, Now));

        var result = await client.ReadItemsByIdAsync(Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorFailureKind.AuthenticationFailed, result.Failure.Kind);
        Assert.Equal(ConnectorErrors.CredentialExpired, result.Failure.Error);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Revocation_between_attempts_stops_the_very_next_attempt()
    {
        var (client, server, credentials) = Harness();
        server.Always(HttpStatusCode.ServiceUnavailable, string.Empty);
        var calls = 0;
        credentials.Answer = _ => ++calls == 1
            ? MondayCredentialResult.Acquired(new MondayAccessCredential(Token, Identity, Now.AddMinutes(30)))
            : MondayCredentialResult.Revoked();

        var result = await client.ReadItemsByIdAsync(Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorErrors.CredentialRevoked, result.Failure.Error);
        Assert.Equal(1, result.Attempts);
        Assert.Single(server.Requests);
        Assert.Equal(Identity, result.Identity);
    }

    [Fact]
    public async Task A_credential_that_changes_identity_between_attempts_is_refused()
    {
        var (client, server, credentials) = Harness();
        server.Always(HttpStatusCode.ServiceUnavailable, string.Empty);
        var other = new ConnectorIdentity(Tenant, Connection, ConnectorIdentityKind.DelegatedUser, "99999999");
        var calls = 0;
        credentials.Answer = _ => MondayCredentialResult.Acquired(
            new MondayAccessCredential(Token, ++calls == 1 ? Identity : other, Now.AddMinutes(30)));

        var result = await client.ReadItemsByIdAsync(Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorErrors.CredentialMismatch, result.Failure.Error);
        Assert.Single(server.Requests);
    }

    [Fact]
    public void Every_connector_operation_is_a_declared_low_risk_read_with_a_single_read_query()
    {
        Assert.Equal(4, MondayOperations.All.Count);
        Assert.All(MondayOperations.All, operation =>
        {
            Assert.True(operation.IsReadOnly, operation.Name);
            Assert.Equal(ConnectorActionType.Read, operation.ActionType);
            Assert.Equal(ConnectorRiskLevel.Low, operation.Risk);
            Assert.Equal(ConnectorSideEffect.None, operation.SideEffect);
            Assert.Equal(ConnectorRecovery.NotRequired, operation.Recovery);
            Assert.StartsWith("monday.", operation.Name, StringComparison.Ordinal);
        });
        Assert.All(MondayQueries.All, document => Assert.True(MondayQueryGuard.IsSingleReadQuery(document)));
        Assert.False(MondayQueryGuard.IsSingleReadQuery("mutation { delete_item(item_id: 1) { id } }"));
        Assert.False(MondayQueryGuard.IsSingleReadQuery("query A { a } mutation B { b }"));
    }

    private static MondayRequestContext Context() => new(Tenant, Connection, CorrelationId.New());

    private static bool IsCredentialName(string name, string[] forbidden)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return forbidden.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Renderings(ConnectorCallResult<MondayItemBatch> result) =>
    [
        result.ToString(),
        result.Failure.ToString(),
        result.Failure.Error.Code,
        result.Failure.Error.Message,
        result.Failure.Error.ToString(),
        result.Identity?.ToString() ?? string.Empty,
    ];

    private static (MondayReadClient Client, FakeServer Server, FakeCredentials Credentials) Harness()
    {
        var server = new FakeServer();
        var credentials = new FakeCredentials(_ => MondayCredentialResult.Acquired(new MondayAccessCredential(Token, Identity, Now.AddMinutes(30))));
        var client = new MondayReadClient(new HttpClient(server), credentials, new FixedClock(), new NoDelay(), () => 0.5);
        return (client, server, credentials);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        private Func<HttpResponseMessage>? _always;

        internal List<HttpRequestMessage> Requests { get; } = [];

        internal void Always(HttpStatusCode status, string body) =>
            _always = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        internal void AlwaysThrow(Func<Exception> exception) => _always = () => throw exception();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_always?.Invoke() ?? throw new InvalidOperationException("Unscripted request."));
        }
    }

    private sealed class FakeCredentials(Func<MondayRequestContext, MondayCredentialResult> answer) : IMondayCredentialSource
    {
        internal Func<MondayRequestContext, MondayCredentialResult> Answer { get; set; } = answer;

        public ValueTask<MondayCredentialResult> AcquireAsync(MondayRequestContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Answer(context));

        public ValueTask<MondayCredentialResult> RenewAfterRejectionAsync(
            MondayRequestContext context,
            MondayAccessCredential rejected,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MondayCredentialResult.NotRenewed());
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class NoDelay : IMondayDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
