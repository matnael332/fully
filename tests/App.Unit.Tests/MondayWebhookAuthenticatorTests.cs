using System.Buffers.Text;
using System.Text;
using WorkContinuity.Connectors.Monday;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Gate 3C webhook authentication: a delivery's JWT verifies only when it is an HS256 token signed with the Signing
/// Secret from secret storage, unexpired, for the exact audience of the connection it is presented for, and naming a
/// monday account. Everything else is refused, and nothing verifies without the secret or the configuration.
/// </summary>
public sealed class MondayWebhookAuthenticatorTests
{
    private readonly WebhookHarness _harness = new();

    [Fact]
    public async Task A_token_signed_with_the_signing_secret_for_the_connection_verifies_raw_or_as_bearer()
    {
        var token = WebhookTokens.Valid(_harness.Connection);

        foreach (var authorization in new[] { token, "Bearer " + token })
        {
            var result = await _harness.AuthenticateAsync(authorization);

            Assert.Equal(MondayWebhookAuthenticationStatus.Verified, result.Status);
            Assert.True(result.IsVerified);
            Assert.Equal(_harness.Connection, result.ConnectionId);
            Assert.DoesNotContain(token, result.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Only_the_exact_raw_token_or_exactly_bearer_and_one_space_is_accepted()
    {
        // Accepting both forms is a 3C compatibility decision (monday documents neither); anything else is refused.
        var token = WebhookTokens.Valid(_harness.Connection);

        foreach (var ambiguous in new[]
                 {
                     "bearer " + token,
                     "BEARER " + token,
                     "Bearer  " + token,
                     " Bearer " + token,
                     "Bearer " + token + " ",
                     "Bearer " + token + ",",
                     "Bearer " + token + "," + token,
                     "Bearer " + token + " " + token,
                     token + ", " + token,
                     token + "," + token,
                     "Token " + token,
                     "JWT " + token,
                     "Bearer=" + token,
                     "Bearer:" + token,
                 })
        {
            await AssertRefusedAsync(ambiguous);
        }
    }

    [Fact]
    public async Task A_wrong_signature_or_another_apps_secret_is_refused()
    {
        var claims = WebhookTokens.Claims(_harness.Connection);
        var signed = WebhookTokens.Mint(claims);
        var lastSignatureCharacter = signed[^1] == 'A' ? 'B' : 'A';

        await AssertRefusedAsync(WebhookTokens.Mint(claims, WebhookTokens.OtherSecret));
        await AssertRefusedAsync(signed[..^1] + lastSignatureCharacter);
        await AssertRefusedAsync(signed[..(signed.LastIndexOf('.') + 1)] + Base64Url.EncodeToString(new byte[32]));
    }

    [Fact]
    public async Task A_claim_changed_after_signing_is_refused()
    {
        var token = WebhookTokens.Valid(_harness.Connection);

        await AssertRefusedAsync(WebhookTokens.WithClaims(token, WebhookTokens.Claims(_harness.Connection, WebhookTokens.OtherAccount)));
        await AssertRefusedAsync(WebhookTokens.WithClaims(token, WebhookTokens.Claims(_harness.Connection, lifetime: TimeSpan.FromDays(3650))));
        await AssertRefusedAsync(WebhookTokens.WithClaims(token, WebhookTokens.Claims(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task Expiry_is_required_and_enforced_with_a_thirty_second_tolerance()
    {
        var expiresAt = WebhookTokens.Now.AddMinutes(5);
        var token = WebhookTokens.Valid(_harness.Connection);

        _harness.Clock.UtcNow = expiresAt.AddSeconds(29);
        Assert.True((await _harness.AuthenticateAsync(token)).IsVerified);

        _harness.Clock.UtcNow = expiresAt.AddSeconds(30);
        await AssertRefusedAsync(token);

        _harness.Clock.UtcNow = expiresAt.AddDays(1);
        await AssertRefusedAsync(token);

        _harness.Clock.UtcNow = WebhookTokens.Now;
        var withoutExpiry = """{"accountId":9001,"aud":"AUD"}""".Replace("AUD", WebhookTokens.Audience(_harness.Connection), StringComparison.Ordinal);
        await AssertRefusedAsync(WebhookTokens.Mint(withoutExpiry));
        await AssertRefusedAsync(WebhookTokens.Mint(withoutExpiry[..^1] + ",\"exp\":\"tomorrow\"}"));
        await AssertRefusedAsync(WebhookTokens.Mint(withoutExpiry[..^1] + ",\"exp\":-1}"));
        await AssertRefusedAsync(WebhookTokens.Mint(withoutExpiry[..^1] + ",\"exp\":1e300}"));
    }

    [Fact]
    public async Task A_token_that_is_not_yet_valid_or_issued_in_the_future_is_refused()
    {
        var future = WebhookTokens.Now.AddMinutes(10).ToUnixTimeSeconds();

        await AssertRefusedAsync(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, issuedAt: WebhookTokens.Now.AddMinutes(2))));
        await AssertRefusedAsync(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, extra: ",\"nbf\":" + future)));

        // Within the tolerance, a clock slightly behind the issuer's still accepts the token.
        Assert.True((await _harness.AuthenticateAsync(
            WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, issuedAt: WebhookTokens.Now.AddSeconds(20))))).IsVerified);
    }

    [Fact]
    public async Task The_audience_must_be_exactly_the_connections_own_address()
    {
        var own = WebhookTokens.Audience(_harness.Connection);

        await AssertRefusedAsync(WebhookTokens.Mint(WebhookTokens.Claims(Guid.CreateVersion7())));
        foreach (var audience in new[]
                 {
                     "https://fully.example.test",
                     "https://fully.example.test/",
                     own + "/",
                     own.ToUpperInvariant(),
                     own.Replace("https://", "http://", StringComparison.Ordinal),
                     own.Replace("fully.example.test", "fully.example.test.evil.test", StringComparison.Ordinal),
                     own + "?x=1",
                 })
        {
            await AssertRefusedAsync(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, audienceJson: "\"" + audience + "\"")));
        }

        await AssertRefusedAsync(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, audienceJson: "[]")));
        await AssertRefusedAsync(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, audienceJson: "null")));
        await AssertRefusedAsync(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, audienceJson: "[\"" + own + "\",7]")));
        await AssertRefusedAsync(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, audienceJson: "[\"https://other.example.test\"]")));
        await AssertRefusedAsync(WebhookTokens.Mint(
            """{"accountId":9001,"exp":EXP}""".Replace("EXP", WebhookTokens.Now.AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)));

        Assert.True((await _harness.AuthenticateAsync(WebhookTokens.Mint(
            WebhookTokens.Claims(_harness.Connection, audienceJson: "[\"https://other.example.test\",\"" + own + "\"]")))).IsVerified);
    }

    [Fact]
    public async Task The_audience_follows_the_configured_base_address_and_never_the_request()
    {
        var prefixed = new WebhookHarness(new MondayWebhookOptions(new Uri("https://fully.example.test/prefix/")));
        var audience = "https://fully.example.test/prefix/api/connectors/monday/webhooks/" + prefixed.Connection.ToString("D");

        Assert.Equal(audience, prefixed.Options!.AudienceFor(prefixed.Connection));
        Assert.True((await prefixed.AuthenticateAsync(
            WebhookTokens.Mint(WebhookTokens.Claims(prefixed.Connection, audienceJson: "\"" + audience + "\"")))).IsVerified);
        Assert.False((await prefixed.AuthenticateAsync(WebhookTokens.Valid(prefixed.Connection))).IsVerified);
    }

    [Theory]
    [InlineData("\"9001\"", true)]
    [InlineData("9001", true)]
    [InlineData("\"abc\"", false)]
    [InlineData("-9001", false)]
    [InlineData("9001.5", false)]
    [InlineData("9.001e3", false)]
    [InlineData("\"\"", false)]
    [InlineData("\"123456789012345678901\"", false)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    [InlineData("\"9001@example.test\"", false)]
    public async Task The_account_claim_must_be_a_monday_numeric_id(string accountJson, bool verifies)
    {
        var result = await _harness.AuthenticateAsync(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection, accountJson)));

        Assert.Equal(verifies, result.IsVerified);
    }

    [Fact]
    public async Task A_token_without_an_account_is_refused()
    {
        var claims = """{"aud":"AUD","exp":EXP}"""
            .Replace("AUD", WebhookTokens.Audience(_harness.Connection), StringComparison.Ordinal)
            .Replace("EXP", WebhookTokens.Now.AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        await AssertRefusedAsync(WebhookTokens.Mint(claims));
    }

    [Theory]
    [InlineData("""{"alg":"none","typ":"JWT"}""")]
    [InlineData("""{"alg":"NONE","typ":"JWT"}""")]
    [InlineData("""{"alg":"None"}""")]
    [InlineData("""{"alg":"","typ":"JWT"}""")]
    [InlineData("""{"alg":"HS384","typ":"JWT"}""")]
    [InlineData("""{"alg":"HS512","typ":"JWT"}""")]
    [InlineData("""{"alg":"RS256","typ":"JWT"}""")]
    [InlineData("""{"alg":"RS512","typ":"JWT"}""")]
    [InlineData("""{"alg":"PS256","typ":"JWT"}""")]
    [InlineData("""{"alg":"ES256","typ":"JWT"}""")]
    [InlineData("""{"alg":"ES384","typ":"JWT"}""")]
    [InlineData("""{"alg":"EdDSA","typ":"JWT"}""")]
    [InlineData("""{"alg":"HS256 ","typ":"JWT"}""")]
    [InlineData("""{"alg":"dir","enc":"A256GCM"}""")]
    [InlineData("""{"alg":"hs256","typ":"JWT"}""")]
    [InlineData("""{"typ":"JWT"}""")]
    [InlineData("""{"alg":["HS256"]}""")]
    [InlineData("""{"alg":"HS256","crit":["exp"]}""")]
    [InlineData("""{"alg":"HS256","typ":"JWE"}""")]
    [InlineData("""{"alg":"HS256","alg":"HS256"}""")]
    [InlineData("""["HS256"]""")]
    [InlineData("""not json""")]
    public async Task Only_an_hs256_header_is_accepted_even_when_the_signature_matches(string header)
    {
        await AssertRefusedAsync(WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection), headerJson: header));
    }

    [Theory]
    [InlineData("""{"alg":"HS256"}""")]
    [InlineData("""{"alg":"HS256","typ":"jwt"}""")]
    [InlineData("""{"alg":"HS256","typ":"JWT","kid":"ignored"}""")]
    public async Task An_hs256_header_with_or_without_a_jwt_type_is_accepted(string header)
    {
        Assert.True((await _harness.AuthenticateAsync(
            WebhookTokens.Mint(WebhookTokens.Claims(_harness.Connection), headerJson: header))).IsVerified);
    }

    [Fact]
    public async Task A_malformed_token_is_refused()
    {
        var token = WebhookTokens.Valid(_harness.Connection);
        var parts = token.Split('.');

        foreach (var malformed in new[]
                 {
                     parts[0] + "." + parts[1],
                     token + ".extra",
                     "." + parts[1] + "." + parts[2],
                     parts[0] + ".." + parts[2],
                     parts[0] + "." + parts[1] + ".",
                     token + "=",
                     parts[0] + "." + parts[1] + "=." + parts[2],
                     token.Replace('-', '+').Replace('_', '/') + "+/",
                     token + " ",
                     " " + token,
                     token[..10] + " " + token[10..],
                     token + "é",
                     "Bearer",
                     "Bearer ",
                     "Basic " + token,
                     "Bearer Bearer " + token,
                     "Bearer\t" + token,
                     "not a token",
                     new string('a', 9000),
                 })
        {
            await AssertRefusedAsync(malformed);
        }
    }

    [Fact]
    public async Task A_non_canonical_encoding_of_the_same_token_is_refused()
    {
        var token = WebhookTokens.Valid(_harness.Connection);
        var signature = token[(token.LastIndexOf('.') + 1)..];

        // The signature's 43rd character carries two unused bits; a character differing only in them spells the same
        // bytes a second way. The strict decoder refuses it, and the verifier's canonical check would too.
        var last = signature[^1];
        var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var index = alphabet.IndexOf(last, StringComparison.Ordinal);
        var twin = alphabet[(index & ~3) | ((index + 1) & 3)];
        var respelled = token[..^1] + twin;

        Assert.Equal(0, index & 3);
        Assert.NotEqual(token, respelled);
        await AssertRefusedAsync(respelled);
    }

    [Fact]
    public async Task A_token_with_repeated_or_unreadable_claims_is_refused()
    {
        var claims = WebhookTokens.Claims(_harness.Connection);

        await AssertRefusedAsync(WebhookTokens.Mint(claims[..^1] + ",\"accountId\":9002}"));
        await AssertRefusedAsync(WebhookTokens.Mint("[" + claims + "]"));
        await AssertRefusedAsync(WebhookTokens.Mint(claims[..^1]));
        await AssertRefusedAsync(WebhookTokens.MintRaw(
            Encoding.UTF8.GetBytes(WebhookTokens.HeaderJson),
            [0xFF, 0xFE, 0x7B, 0x7D],
            Encoding.UTF8.GetBytes(WebhookTokens.SigningSecret)));
    }

    [Fact]
    public async Task A_missing_or_empty_authorization_is_refused()
    {
        await AssertRefusedAsync(null);
        await AssertRefusedAsync(string.Empty);
        Assert.Equal(
            MondayWebhookAuthenticationStatus.Refused,
            (await _harness.AuthenticateAsync(WebhookTokens.Valid(Guid.Empty), Guid.Empty)).Status);
    }

    [Fact]
    public async Task Without_a_readable_well_formed_signing_secret_nothing_verifies_and_the_answer_is_unavailable()
    {
        var token = WebhookTokens.Valid(_harness.Connection);

        var missing = new WebhookHarness(withSecret: false);
        Assert.Equal(MondayWebhookAuthenticationStatus.Unavailable, (await missing.AuthenticateAsync(WebhookTokens.Valid(missing.Connection))).Status);

        _harness.Secrets.GetOverride = _ => MondaySecretStoreStatus.Unavailable;
        Assert.Equal(MondayWebhookAuthenticationStatus.Unavailable, (await _harness.AuthenticateAsync(token)).Status);
        _harness.Secrets.GetOverride = _ => null;

        foreach (var malformed in new[] { Array.Empty<byte>(), "secret with spaces"u8.ToArray(), [0xC3, 0xA9], new byte[1025] })
        {
            _harness.Secrets.Put(MondaySecretName.SigningSecret, malformed);
            Assert.Equal(MondayWebhookAuthenticationStatus.Unavailable, (await _harness.AuthenticateAsync(token)).Status);
        }

        var unconfigured = new WebhookHarness(unconfigured: true);
        Assert.Equal(MondayWebhookAuthenticationStatus.Unavailable, (await unconfigured.AuthenticateAsync(WebhookTokens.Valid(unconfigured.Connection))).Status);

        // A request that brings no token is the caller's fault, whatever the configuration.
        Assert.Equal(MondayWebhookAuthenticationStatus.Refused, (await unconfigured.AuthenticateAsync(null)).Status);
    }

    [Fact]
    public async Task The_signing_secret_is_read_once_per_instance_from_its_fixed_name_and_only_read()
    {
        var authenticator = _harness.Authenticator();
        var token = WebhookTokens.Valid(_harness.Connection);

        Assert.True((await authenticator.AuthenticateAsync(_harness.Connection, token, TestContext.Current.CancellationToken)).IsVerified);
        Assert.True((await authenticator.AuthenticateAsync(_harness.Connection, "Bearer " + token, TestContext.Current.CancellationToken)).IsVerified);
        Assert.False((await authenticator.AuthenticateAsync(_harness.Connection, "wrong", TestContext.Current.CancellationToken)).IsVerified);

        Assert.Equal(["get wce-monday-signing-secret"], _harness.Secrets.Operations);
        Assert.Equal("wce-monday-signing-secret", MondaySecretName.SigningSecret.Value);
    }

    [Fact]
    public void The_webhook_options_accept_only_an_https_base_address_without_user_information_query_or_fragment()
    {
        foreach (var address in new[]
                 {
                     "http://fully.example.test",
                     "https://user:pass@fully.example.test",
                     "https://fully.example.test/?q=1",
                     "https://fully.example.test/#f",
                     "ftp://fully.example.test",
                 })
        {
            Assert.Throws<ArgumentException>(() => new MondayWebhookOptions(new Uri(address)));
        }

        Assert.Throws<ArgumentException>(() => new MondayWebhookOptions(new Uri("/relative", UriKind.Relative)));
        Assert.Throws<ArgumentNullException>(() => new MondayWebhookOptions(null!));

        var connection = Guid.CreateVersion7();
        Assert.Equal(WebhookTokens.Audience(connection), new MondayWebhookOptions(new Uri("https://FULLY.example.test/")).AudienceFor(connection));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    [InlineData("0191f3a0-7a44-7c1b-9d2e-3f5a6b7c8d9e ")]
    public void A_route_value_that_is_not_a_canonical_connection_id_names_no_connection(string? value)
    {
        Assert.False(MondayWebhooks.TryParseConnectionId(value, out var connection));
        Assert.Equal(Guid.Empty, connection);
    }

    [Fact]
    public void Only_the_canonical_lowercase_spelling_of_a_connection_id_is_accepted()
    {
        var connection = Guid.CreateVersion7();

        Assert.True(MondayWebhooks.TryParseConnectionId(connection.ToString("D"), out var parsed));
        Assert.Equal(connection, parsed);
        Assert.False(MondayWebhooks.TryParseConnectionId(connection.ToString("D").ToUpperInvariant(), out _));
        Assert.False(MondayWebhooks.TryParseConnectionId(connection.ToString("N"), out _));
        Assert.False(MondayWebhooks.TryParseConnectionId(connection.ToString("B"), out _));
        Assert.Equal("/api/connectors/monday/webhooks/{connectionId}", MondayWebhooks.RouteTemplate);
    }

    private async Task AssertRefusedAsync(string? authorization)
    {
        var result = await _harness.AuthenticateAsync(authorization);

        Assert.Equal(MondayWebhookAuthenticationStatus.Refused, result.Status);
        Assert.False(result.IsVerified);
    }
}
