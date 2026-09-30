using System.Text.RegularExpressions;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Gate 3B primitives: PKCE S256 against RFC 7636's own example, the server-side OAuth configuration, the read-only
/// scope policy, the secret names, and the provider-neutral authorization records of Core.
/// </summary>
public sealed class MondayOAuthPrimitivesTests
{
    /// <summary>Every scope monday's authorization server metadata lists (reverified 2026-09-30).</summary>
    public static TheoryData<string> DocumentedScopes => new(
        "ai:consume", "me:read", "apps:read", "apps:write", "docs:read", "docs:write", "tags:read", "items:read", "items:write",
        "teams:read", "teams:write", "users:read", "users:write", "assets:read", "boards:read", "boards:write", "account:read",
        "updates:read", "updates:write", "webhooks:read", "webhooks:write", "workspaces:read", "workspaces:write",
        "departments:read", "departments:write", "notifications:write");

    [Fact]
    public void The_challenge_of_the_rfc_7636_example_verifier_is_the_rfcs_own()
    {
        // RFC 7636, Appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", MondayPkce.ChallengeOf("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Assert.Equal("S256", MondayPkce.ChallengeMethod);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjX", false)]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", true)]
    [InlineData("abc.def~ghi_jkl-mnoABCDEFGHIJKLMNOPQRSTUVWXYZ0", true)]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk+", false)]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk=", false)]
    [InlineData("dBjftJeZ4CVP mB92K27uhbUJU1p1r_wW1gFWFOEjXk", false)]
    public void A_verifier_is_43_to_128_unreserved_characters(string? verifier, bool valid)
    {
        Assert.Equal(valid, MondayPkce.IsVerifier(verifier));
        if (!valid && verifier is not null)
        {
            Assert.Throws<ArgumentException>(() => MondayPkce.ChallengeOf(verifier));
        }
    }

    [Fact]
    public void The_longest_verifier_is_128_characters()
    {
        Assert.True(MondayPkce.IsVerifier(new string('a', 128)));
        Assert.False(MondayPkce.IsVerifier(new string('a', 129)));
    }

    [Fact]
    public void The_default_request_is_the_minimum_scopes_and_every_allowed_scope_is_a_read()
    {
        var options = new MondayOAuthOptions(OAuthHarness.ClientId, OAuthHarness.Redirect);

        Assert.Equal("boards:read me:read", options.Scopes);
        Assert.Equal(MondayScopePolicy.DefaultScopes, options.Scopes);
        Assert.All(MondayScopePolicy.Allowed, scope => Assert.EndsWith(":read", scope, StringComparison.Ordinal));
        Assert.Equal(14, MondayScopePolicy.Allowed.Count);
        Assert.Equal(new[] { "boards:read", "me:read" }, MondayScopePolicy.Required.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(DocumentedScopes))]
    public void A_documented_scope_is_allowed_exactly_when_it_only_reads(string scope)
    {
        var withRequired = "boards:read me:read " + scope;
        var canonical = scope is "boards:read" or "me:read" ? "boards:read me:read" : string.Join(' ', withRequired.Split(' ').Distinct().Order(StringComparer.Ordinal));
        var reads = scope.EndsWith(":read", StringComparison.Ordinal);

        Assert.Equal(reads, MondayScopePolicy.Allowed.Contains(scope));
        Assert.Equal(reads, MondayScopePolicy.IsPermittedGrant(canonical));
        Assert.Equal(reads, MondayScopePolicy.IsPermittedRequest(canonical));
        if (!reads)
        {
            Assert.Throws<ArgumentException>(() => new MondayOAuthOptions(OAuthHarness.ClientId, OAuthHarness.Redirect, canonical));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("boards:read")]
    [InlineData("me:read")]
    [InlineData("boards:read me:read unknown:read")]
    [InlineData("boards:read  me:read")]
    [InlineData(" boards:read me:read")]
    [InlineData("boards:read me:read me:read")]
    [InlineData("boards:read me:read BOARDS:READ")]
    public void A_scope_list_that_is_malformed_unknown_or_missing_a_required_scope_is_refused(string? scopes)
    {
        Assert.False(MondayScopePolicy.IsPermittedRequest(scopes));
        Assert.False(MondayScopePolicy.IsPermittedGrant(scopes));
    }

    [Theory]
    [InlineData("http://app.example.test/callback")]
    [InlineData("https://user:password@app.example.test/callback")]
    [InlineData("https://app.example.test/callback#fragment")]
    [InlineData("/connectors/monday/callback")]
    public void The_redirect_address_must_be_an_absolute_https_address_without_user_information_or_fragment(string address)
    {
        var redirect = new Uri(address, UriKind.RelativeOrAbsolute);
        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions(OAuthHarness.ClientId, redirect));
    }

    [Theory]
    [InlineData("")]
    [InlineData("client id")]
    [InlineData("client\r\nid")]
    [InlineData("clienté")]
    public void The_client_id_must_be_visible_ascii(string clientId) =>
        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions(clientId, OAuthHarness.Redirect));

    [Fact]
    public void The_timings_stay_within_their_bounds_and_a_transaction_never_outlives_the_codes_ten_minutes()
    {
        var defaults = new MondayOAuthOptions(OAuthHarness.ClientId, OAuthHarness.Redirect);
        Assert.Equal(
            (TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30)),
            (defaults.TransactionLifetime, defaults.RefreshMargin, defaults.RefreshClaimTimeout, defaults.HttpTimeout));

        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions(OAuthHarness.ClientId, OAuthHarness.Redirect, transactionLifetime: TimeSpan.FromMinutes(10).Add(TimeSpan.FromTicks(1))));
        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions(OAuthHarness.ClientId, OAuthHarness.Redirect, transactionLifetime: TimeSpan.FromSeconds(59)));
        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions(OAuthHarness.ClientId, OAuthHarness.Redirect, refreshMargin: TimeSpan.FromMinutes(31)));
        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions(OAuthHarness.ClientId, OAuthHarness.Redirect, httpTimeout: TimeSpan.FromSeconds(61)));

        // A claim must outlive one OAuth call, or a live renewal could be judged abandoned.
        Assert.Throws<ArgumentException>(() => new MondayOAuthOptions(
            OAuthHarness.ClientId, OAuthHarness.Redirect, refreshClaimTimeout: TimeSpan.FromSeconds(30), httpTimeout: TimeSpan.FromSeconds(30)));
        Assert.Equal(ConnectorAuthorizationTransaction.MaxLifetime, TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Secret_names_are_key_vault_compatible_and_carry_their_tenant()
    {
        var harness = new OAuthHarness();
        await harness.ConnectAsync();

        var form = new Regex("^[a-z0-9-]{1,127}$", RegexOptions.CultureInvariant);
        Assert.NotEmpty(harness.Secrets.Names);
        Assert.All(harness.Secrets.Names.Concat(harness.Secrets.Operations.Select(operation => operation.Split(' ')[1])), name => Assert.Matches(form, name));
        Assert.All(
            harness.Secrets.Names.Where(name => name != MondaySecretName.ClientSecret.Value),
            name => Assert.Contains(harness.Tenant.Value.ToString("N"), name, StringComparison.Ordinal));
        Assert.Equal("wce-monday-client-secret", MondaySecretName.ClientSecret.ToString());
    }

    [Fact]
    public void A_scope_list_is_well_formed_only_as_distinct_names_separated_by_single_spaces()
    {
        Assert.True(ConnectorScopes.IsWellFormed("boards:read me:read"));
        Assert.False(ConnectorScopes.IsWellFormed(null));
        Assert.False(ConnectorScopes.IsWellFormed("a  b"));
        Assert.False(ConnectorScopes.IsWellFormed("a a"));
        Assert.False(ConnectorScopes.IsWellFormed("a\"b"));
        Assert.False(ConnectorScopes.IsWellFormed(new string('a', ConnectorScopes.MaxNameLength + 1)));
        Assert.Equal("a b c", ConnectorScopes.Normalize(["c", "a", "b", "a"]));
        Assert.Throws<ArgumentException>(() => ConnectorScopes.Normalize([]));
        Assert.Throws<ArgumentException>(() => ConnectorScopes.Parse("a  b"));
    }

    [Fact]
    public void An_authorization_transaction_refuses_anything_incomplete_or_malformed()
    {
        var user = UserId.New();
        byte[] hash = new byte[32];

        _ = new ConnectorAuthorizationTransaction(Guid.NewGuid(), user, Guid.NewGuid(), "monday", ConnectorAuthorizationPurpose.Connect, hash, hash, "me:read");
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationTransaction(Guid.Empty, user, Guid.NewGuid(), "monday", ConnectorAuthorizationPurpose.Connect, hash, hash, "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationTransaction(Guid.NewGuid(), default, Guid.NewGuid(), "monday", ConnectorAuthorizationPurpose.Connect, hash, hash, "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationTransaction(Guid.NewGuid(), user, Guid.Empty, "monday", ConnectorAuthorizationPurpose.Connect, hash, hash, "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationTransaction(Guid.NewGuid(), user, Guid.NewGuid(), "Monday", ConnectorAuthorizationPurpose.Connect, hash, hash, "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationTransaction(Guid.NewGuid(), user, Guid.NewGuid(), "monday", ConnectorAuthorizationPurpose.Unspecified, hash, hash, "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationTransaction(Guid.NewGuid(), user, Guid.NewGuid(), "monday", ConnectorAuthorizationPurpose.Connect, new byte[31], hash, "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationTransaction(Guid.NewGuid(), user, Guid.NewGuid(), "monday", ConnectorAuthorizationPurpose.Connect, hash, new byte[33], "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationTransaction(Guid.NewGuid(), user, Guid.NewGuid(), "monday", ConnectorAuthorizationPurpose.Connect, hash, hash, "me:read  x"));
    }

    [Fact]
    public void A_grant_and_an_authorization_refuse_email_identities_and_missing_components()
    {
        var user = UserId.New();
        var tenant = TenantId.New();
        var expires = DateTimeOffset.UtcNow.AddHours(1);

        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationGrant(Guid.NewGuid(), "monday", user, "someone@example.test", "9001", Guid.NewGuid(), expires, "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationGrant(Guid.NewGuid(), "monday", user, "58201774", "a@b", Guid.NewGuid(), expires, "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationGrant(Guid.NewGuid(), "monday", user, "58201774", "9001", Guid.Empty, expires, "me:read"));
        Assert.Throws<ArgumentException>(() => new ConnectorAuthorizationGrant(Guid.NewGuid(), "monday", default, "58201774", "9001", Guid.NewGuid(), expires, "me:read"));

        ConnectorAuthorization Authorization(ConnectorAuthorizationState state = ConnectorAuthorizationState.Active, long generation = 1, string subject = "58201774", Guid? claim = null, TimeSpan? age = null) =>
            new(tenant, Guid.NewGuid(), "monday", true, user, true, state, subject, "9001", generation, Guid.NewGuid(), expires, "me:read", claim, age);

        Assert.Equal(ConnectorIdentityKind.DelegatedUser, Authorization().DelegatedIdentity.Kind);
        Assert.Equal("58201774", Authorization().DelegatedIdentity.Subject);
        Assert.Throws<ArgumentException>(() => Authorization(ConnectorAuthorizationState.Unspecified));
        Assert.Throws<ArgumentException>(() => Authorization(generation: 0));
        Assert.Throws<ArgumentException>(() => Authorization(subject: "someone@example.test"));
        Assert.Throws<ArgumentException>(() => Authorization(claim: Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Authorization(age: TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => Authorization(claim: Guid.Empty, age: TimeSpan.Zero));
        Assert.DoesNotContain("58201774", Authorization().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Consumption_and_disconnection_results_cannot_be_built_inconsistently()
    {
        Assert.Throws<ArgumentException>(() => ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.Consumed));
        Assert.Throws<ArgumentException>(() => ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.Unspecified));
        Assert.Throws<ArgumentNullException>(() => ConnectorTransactionConsumption.Consumed(null!));
        Assert.Throws<ArgumentException>(() => ConnectorDisconnection.Unchanged(ConnectorDisconnectOutcome.Disconnected));
        Assert.Throws<ArgumentException>(() => ConnectorDisconnection.Disconnected(Guid.Empty, null));
        Assert.Null(ConnectorTransactionConsumption.Refused(ConnectorTransactionOutcome.Expired).Transaction);
    }

    [Fact]
    public void Authorization_error_codes_are_unique_stable_and_their_messages_fixed()
    {
        var codes = ConnectorAuthorizationErrors.All.Select(error => error.Code).ToList();

        Assert.Equal(23, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ConnectorAuthorizationErrors.All, error =>
        {
            Assert.StartsWith("connector.authorization.", error.Code, StringComparison.Ordinal);
            Assert.True(error.Code.All(character => char.IsAsciiLetterLower(character) || character is '.' or '_'), error.Code);
            Assert.NotEqual(ErrorCategory.None, error.Category);
        });
        Assert.Contains(ConnectorErrors.ReauthorizationRequired, ConnectorErrors.All);
    }

    [Fact]
    public void The_monday_endpoints_are_the_metadatas_own()
    {
        Assert.Equal("https://auth.monday.com", MondayEndpoints.Issuer);
        Assert.Equal("https://auth.monday.com/oauth2/authorize", MondayEndpoints.Authorize.AbsoluteUri);
        Assert.Equal("https://auth.monday.com/oauth_ms/oauth/token", MondayEndpoints.Token.AbsoluteUri);
        Assert.Equal("https://auth.monday.com/oauth_ms/oauth/revoke", MondayEndpoints.Revoke.AbsoluteUri);
    }
}
