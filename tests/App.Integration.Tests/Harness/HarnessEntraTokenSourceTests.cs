using System.Buffers.Text;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Live;
using WorkContinuity.Harness.Monday.Secrets;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G Stage B2 (owner decisions B-1, B-7, B-10 of 2026-10-06): the hand-written certificate assertion and the Entra
/// certificate token source, against a fake token endpoint and generated, ephemeral certificates only. No identity
/// provider, tenant or vault is contacted, and nothing here is evidence of how Entra or Key Vault behave.
/// </summary>
public sealed class HarnessEntraTokenSourceTests
{
    private static readonly Guid Tenant = Guid.Parse("4f1f6c3a-0b5e-4d7a-9c2e-1a2b3c4d5e6f");
    private static readonly Guid Client = Guid.Parse("8a7b6c5d-4e3f-4a1b-9c8d-7e6f5a4b3c2d");
    private static readonly Uri Endpoint = new("https://login.microsoftonline.com/4f1f6c3a-0b5e-4d7a-9c2e-1a2b3c4d5e6f/oauth2/v2.0/token");

    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // --- The assertion ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_assertion_has_exactly_the_documented_header_and_claims()
    {
        using var certificate = TestCertificates.Rsa();
        var now = TestCertificates.Now;
        var id = Guid.Parse("11111111-2222-4333-8444-555555555555");

        var assertion = EntraClientAssertion.Create(Endpoint, Client, certificate, now, id);

        var parts = assertion!.Split('.');
        Assert.Equal(3, parts.Length);
        var header = Decode(parts[0]);
        Assert.Equal(["alg", "typ", "x5t#S256"], header.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("PS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal(Base64Url.EncodeToString(SHA256.HashData(certificate.RawData)), header.GetProperty("x5t#S256").GetString());

        var claims = Decode(parts[1]);
        Assert.Equal(
            ["aud", "exp", "iat", "iss", "jti", "nbf", "sub"],
            claims.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Endpoint.AbsoluteUri, claims.GetProperty("aud").GetString());
        Assert.Equal(Client.ToString("D"), claims.GetProperty("iss").GetString());
        Assert.Equal(Client.ToString("D"), claims.GetProperty("sub").GetString());
        Assert.Equal(id.ToString("D"), claims.GetProperty("jti").GetString());
        Assert.Equal(now.ToUnixTimeSeconds(), claims.GetProperty("nbf").GetInt64());
        Assert.Equal(now.ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
        Assert.Equal(now.ToUnixTimeSeconds() + 300, claims.GetProperty("exp").GetInt64());
    }

    [Fact]
    public void The_assertion_lifetime_is_bounded_within_the_documented_ten_minutes()
    {
        using var certificate = TestCertificates.Rsa();
        var claims = Decode(EntraClientAssertion.Create(Endpoint, Client, certificate, TestCertificates.Now, Guid.NewGuid())!.Split('.')[1]);
        var lifetime = claims.GetProperty("exp").GetInt64() - claims.GetProperty("nbf").GetInt64();
        Assert.InRange(lifetime, 1, 600);
        Assert.Equal(TimeSpan.FromMinutes(5), EntraClientAssertion.Lifetime);
    }

    [Fact]
    public void The_assertion_is_signed_with_pss_over_sha256_by_the_certificate_key_and_by_nothing_else()
    {
        using var certificate = TestCertificates.Rsa();
        using var other = TestCertificates.Rsa();
        var assertion = EntraClientAssertion.Create(Endpoint, Client, certificate, TestCertificates.Now, Guid.NewGuid())!;
        var parts = assertion.Split('.');
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signature = Base64Url.DecodeFromChars(parts[2]);

        using var key = certificate.GetRSAPublicKey()!;
        Assert.True(key.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

        // Negative controls: another key, the other padding, and a changed claim do not verify.
        using var otherKey = other.GetRSAPublicKey()!;
        Assert.False(otherKey.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.False(key.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var tampered = Encoding.ASCII.GetBytes(parts[0] + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"aud\":\"x\"}")));
        Assert.False(key.VerifyData(tampered, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    [Fact]
    public void Each_assertion_has_its_own_id()
    {
        using var certificate = TestCertificates.Rsa();
        var ids = Enumerable.Range(0, 50)
            .Select(_ => Decode(EntraClientAssertion.Create(Endpoint, Client, certificate, TestCertificates.Now, Guid.NewGuid())!.Split('.')[1]))
            .Select(claims => claims.GetProperty("jti").GetString())
            .ToList();
        Assert.Equal(50, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_unsuitable_certificate_signs_nothing_and_cannot_build_a_source()
    {
        using var rsa = TestCertificates.Rsa();
        using var publicOnly = TestCertificates.PublicOnly(rsa);
        using var ecdsa = TestCertificates.Ecdsa();
        using var small = TestCertificates.Rsa(1024);

        foreach (var certificate in new[] { publicOnly, ecdsa, small })
        {
            Assert.False(EntraClientAssertion.IsSuitable(certificate));
            Assert.Null(EntraClientAssertion.Create(Endpoint, Client, certificate, TestCertificates.Now, Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => new EntraCertificateTokenSource(Tenant, Client, certificate, new FakeEntra(), new SteppedTimeProvider()));
        }

        // Positive control.
        Assert.True(EntraClientAssertion.IsSuitable(rsa));
    }

    [Fact]
    public void Empty_identifiers_are_refused()
    {
        using var certificate = TestCertificates.Rsa();
        Assert.Null(EntraClientAssertion.Create(Endpoint, Guid.Empty, certificate, TestCertificates.Now, Guid.NewGuid()));
        Assert.Null(EntraClientAssertion.Create(Endpoint, Client, certificate, TestCertificates.Now, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new EntraCertificateTokenSource(Guid.Empty, Client, certificate, new FakeEntra(), new SteppedTimeProvider()));
        Assert.Throws<ArgumentException>(() => new EntraCertificateTokenSource(Tenant, Guid.Empty, certificate, new FakeEntra(), new SteppedTimeProvider()));
    }

    [Fact]
    public async Task A_certificate_outside_its_validity_period_signs_nothing_and_no_request_is_sent()
    {
        var now = TestCertificates.Now;
        using var expired = TestCertificates.Rsa(notBefore: now.AddDays(-10), notAfter: now.AddDays(-1));
        using var future = TestCertificates.Rsa(notBefore: now.AddDays(1), notAfter: now.AddDays(10));
        foreach (var certificate in new[] { expired, future })
        {
            Assert.Null(EntraClientAssertion.Create(Endpoint, Client, certificate, now, Guid.NewGuid()));
            var entra = new FakeEntra();
            using var source = new EntraCertificateTokenSource(Tenant, Client, certificate, entra, new SteppedTimeProvider());
            Assert.Null(await source.GetAccessTokenAsync(Cancellation));
            Assert.Empty(entra.Requests);
        }
    }

    // --- The token request ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_request_is_exactly_the_documented_certificate_grant_for_the_key_vault_scope()
    {
        using var certificate = TestCertificates.Rsa();
        var entra = new FakeEntra();
        var time = new SteppedTimeProvider();
        using var source = new EntraCertificateTokenSource(Tenant, Client, certificate, entra, time);

        Assert.Equal(FakeEntra.IssuedToken, await source.GetAccessTokenAsync(Cancellation));

        var request = Assert.Single(entra.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Endpoint.AbsoluteUri, request.Uri.AbsoluteUri);
        Assert.Null(request.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", request.MediaType);
        Assert.Equal(["client_assertion", "client_assertion_type", "client_id", "grant_type", "scope"], request.Form.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Client.ToString("D"), request.Form["client_id"]);
        Assert.Equal("https://vault.azure.net/.default", request.Form["scope"]);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", request.Form["client_assertion_type"]);
        Assert.Equal("client_credentials", request.Form["grant_type"]);
        Assert.DoesNotContain("client_secret", request.Form.Keys);

        // The assertion sent is bound to this tenant's endpoint and this client, and verifies with the certificate.
        var parts = request.Form["client_assertion"].Split('.');
        var claims = Decode(parts[1]);
        Assert.Equal(Endpoint.AbsoluteUri, claims.GetProperty("aud").GetString());
        Assert.Equal(Client.ToString("D"), claims.GetProperty("iss").GetString());
        Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds(), claims.GetProperty("nbf").GetInt64());
        using var key = certificate.GetRSAPublicKey()!;
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    [Fact]
    public async Task Another_tenant_or_client_changes_the_audience_and_the_binding()
    {
        using var certificate = TestCertificates.Rsa();
        var otherTenant = Guid.NewGuid();
        var otherClient = Guid.NewGuid();
        var entra = new FakeEntra();
        using var source = new EntraCertificateTokenSource(otherTenant, otherClient, certificate, entra, new SteppedTimeProvider());

        await source.GetAccessTokenAsync(Cancellation);

        var request = Assert.Single(entra.Requests);
        Assert.Equal(EntraCertificateTokenSource.TokenEndpointFor(otherTenant), request.Uri);
        Assert.NotEqual(Endpoint, request.Uri);
        var claims = Decode(request.Form["client_assertion"].Split('.')[1]);
        Assert.Equal(request.Uri.AbsoluteUri, claims.GetProperty("aud").GetString());
        Assert.Equal(otherClient.ToString("D"), claims.GetProperty("sub").GetString());
        Assert.Equal(otherClient.ToString("D"), request.Form["client_id"]);
    }

    // --- The answer -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"token_type":"Bearer","expires_in":3599}""")]
    [InlineData("""{"expires_in":3599,"access_token":"abc"}""")]
    [InlineData("""{"token_type":"pop","expires_in":3599,"access_token":"abc"}""")]
    [InlineData("""{"token_type":null,"expires_in":3599,"access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":"3599","access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3599.5,"access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":300,"access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":0,"access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":-5,"access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":86401,"access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3599,"access_token":""}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3599,"access_token":null}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3599,"access_token":123}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3599,"access_token":"has space"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3599,"access_token":"café"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3599,"access_token":"abc","access_token":"def"}""")]
    [InlineData("""{"token_type":"Bearer","token_type":"Bearer","expires_in":3599,"access_token":"abc"}""")]
    [InlineData("""{"token_type":"Bearer","expires_in":3599,"expires_in":3599,"access_token":"abc"}""")]
    public async Task A_malformed_answer_gives_no_token(string body)
    {
        var (source, entra) = Source();
        using (source)
        {
            entra.Respond = (_, _) => Task.FromResult(FakeEntra.Answer(body));
            Assert.Null(await source.GetAccessTokenAsync(Cancellation));
            Assert.Single(entra.Requests);
        }
    }

    [Fact]
    public async Task A_well_formed_answer_with_extra_fields_and_lower_case_bearer_gives_the_token()
    {
        var (source, entra) = Source();
        using (source)
        {
            entra.Respond = (_, _) => Task.FromResult(FakeEntra.Answer("""{"token_type":"bearer","expires_in":3600,"ext_expires_in":3600,"access_token":"abc.def-ghi_jkl"}"""));
            Assert.Equal("abc.def-ghi_jkl", await source.GetAccessTokenAsync(Cancellation));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Created)]
    public async Task Any_status_but_200_gives_no_token_even_with_a_token_in_the_body(HttpStatusCode status)
    {
        var (source, entra) = Source();
        using (source)
        {
            entra.Respond = (_, _) =>
            {
                var answer = FakeEntra.Answer($$"""{"token_type":"Bearer","expires_in":3599,"access_token":"{{FakeEntra.IssuedToken}}"}""", status);
                answer.Headers.Location = new Uri("https://elsewhere.example/token");
                return Task.FromResult(answer);
            };
            Assert.Null(await source.GetAccessTokenAsync(Cancellation));
            Assert.Single(entra.Requests);
        }
    }

    [Fact]
    public async Task An_answer_that_is_not_json_by_its_media_type_gives_no_token()
    {
        var (source, entra) = Source();
        using (source)
        {
            entra.Respond = (_, _) => Task.FromResult(
                FakeEntra.Answer($$"""{"token_type":"Bearer","expires_in":3599,"access_token":"{{FakeEntra.IssuedToken}}"}""", mediaType: "text/plain"));
            Assert.Null(await source.GetAccessTokenAsync(Cancellation));
        }
    }

    [Fact]
    public async Task An_oversized_answer_gives_no_token_whether_declared_or_streamed()
    {
        var padding = new string('a', 70_000);
        var body = $$"""{"token_type":"Bearer","expires_in":3599,"access_token":"abc","padding":"{{padding}}"}""";

        var (declared, declaredEntra) = Source();
        using (declared)
        {
            declaredEntra.Respond = (_, _) => Task.FromResult(FakeEntra.Answer(body));
            Assert.Null(await declared.GetAccessTokenAsync(Cancellation));
        }

        var (streamed, streamedEntra) = Source();
        using (streamed)
        {
            streamedEntra.Respond = (_, _) =>
            {
                var content = new StreamContent(new UnseekableStream(Encoding.UTF8.GetBytes(body)));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            };
            Assert.Null(await streamed.GetAccessTokenAsync(Cancellation));
        }

        // Positive control: the same answer without the padding gives the token.
        var (control, controlEntra) = Source();
        using (control)
        {
            controlEntra.Respond = (_, _) => Task.FromResult(FakeEntra.Answer("""{"token_type":"Bearer","expires_in":3599,"access_token":"abc"}"""));
            Assert.Equal("abc", await control.GetAccessTokenAsync(Cancellation));
        }
    }

    [Fact]
    public async Task An_access_token_at_the_adapter_bound_is_accepted_and_one_beyond_it_is_not()
    {
        var atBound = new string('t', 16 * 1024);
        var beyond = new string('t', (16 * 1024) + 1);

        var (accepting, acceptingEntra) = Source();
        using (accepting)
        {
            acceptingEntra.Respond = (_, _) => Task.FromResult(FakeEntra.Answer($$"""{"token_type":"Bearer","expires_in":3599,"access_token":"{{atBound}}"}"""));
            Assert.Equal(atBound, await accepting.GetAccessTokenAsync(Cancellation));
        }

        var (refusing, refusingEntra) = Source();
        using (refusing)
        {
            refusingEntra.Respond = (_, _) => Task.FromResult(FakeEntra.Answer($$"""{"token_type":"Bearer","expires_in":3599,"access_token":"{{beyond}}"}"""));
            Assert.Null(await refusing.GetAccessTokenAsync(Cancellation));
        }
    }

    // --- The kept token -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_is_kept_until_five_minutes_before_its_expiry_and_then_requested_again()
    {
        var time = new SteppedTimeProvider();
        var (source, entra) = Source(time);
        using (source)
        {
            Assert.Equal(FakeEntra.IssuedToken, await source.GetAccessTokenAsync(Cancellation));
            Assert.Single(entra.Requests);

            // 3,599 seconds of life, minus the 300-second margin: usable for 3,299 seconds.
            time.Advance(TimeSpan.FromSeconds(3_298));
            Assert.Equal(FakeEntra.IssuedToken, await source.GetAccessTokenAsync(Cancellation));
            Assert.Single(entra.Requests);

            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(FakeEntra.IssuedToken, await source.GetAccessTokenAsync(Cancellation));
            Assert.Equal(2, entra.Requests.Count);

            // The second request carries a fresh assertion id and a fresh issue time.
            var first = Decode(entra.Requests[0].Form["client_assertion"].Split('.')[1]);
            var second = Decode(entra.Requests[1].Form["client_assertion"].Split('.')[1]);
            Assert.NotEqual(first.GetProperty("jti").GetString(), second.GetProperty("jti").GetString());
            Assert.Equal(first.GetProperty("nbf").GetInt64() + 3_299, second.GetProperty("nbf").GetInt64());
        }
    }

    [Fact]
    public async Task A_failure_is_never_kept_and_the_next_call_asks_again()
    {
        var (source, entra) = Source();
        using (source)
        {
            entra.Respond = (_, _) => Task.FromResult(FakeEntra.Answer("""{"error":"invalid_client"}""", HttpStatusCode.BadRequest));
            Assert.Null(await source.GetAccessTokenAsync(Cancellation));
            entra.Respond = (_, _) => Task.FromResult(FakeEntra.Answer($$"""{"token_type":"Bearer","expires_in":3599,"access_token":"{{FakeEntra.IssuedToken}}"}"""));
            Assert.Equal(FakeEntra.IssuedToken, await source.GetAccessTokenAsync(Cancellation));
            Assert.Equal(2, entra.Requests.Count);
        }
    }

    [Fact]
    public async Task Nothing_is_kept_beyond_the_instance_or_after_disposal()
    {
        using var certificate = TestCertificates.Rsa();
        var entra = new FakeEntra();
        var time = new SteppedTimeProvider();
        using (var first = new EntraCertificateTokenSource(Tenant, Client, certificate, entra, time))
        {
            await first.GetAccessTokenAsync(Cancellation);
        }

        using var second = new EntraCertificateTokenSource(Tenant, Client, certificate, entra, time);
        await second.GetAccessTokenAsync(Cancellation);
        Assert.Equal(2, entra.Requests.Count);

        // No static state of any kind on the type: a kept token can only live in an instance.
        Assert.DoesNotContain(
            typeof(EntraCertificateTokenSource).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
            field => !field.IsLiteral && !field.IsInitOnly);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_request()
    {
        var (source, entra) = Source();
        using (source)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            entra.Respond = async (_, _) =>
            {
                await release.Task;
                return FakeEntra.Answer($$"""{"token_type":"Bearer","expires_in":3599,"access_token":"{{FakeEntra.IssuedToken}}"}""");
            };
            var calls = Enumerable.Range(0, 5).Select(_ => source.GetAccessTokenAsync(Cancellation).AsTask()).ToList();
            release.SetResult();
            Assert.All(await Task.WhenAll(calls), token => Assert.Equal(FakeEntra.IssuedToken, token));
            Assert.Single(entra.Requests);
        }
    }

    // --- Cancellation and failure ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_caller_cancelled_before_the_call_gets_cancellation_and_nothing_is_sent()
    {
        var (source, entra) = Source();
        using (source)
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetAccessTokenAsync(cancelled.Token).AsTask());
            Assert.Empty(entra.Requests);
        }
    }

    [Fact]
    public async Task A_caller_cancelled_during_the_request_gets_cancellation_not_a_missing_token()
    {
        var (source, entra) = Source();
        using (source)
        {
            entra.Respond = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            };
            using var caller = new CancellationTokenSource();
            var received = entra.Received.Task;
            var call = source.GetAccessTokenAsync(caller.Token).AsTask();
            await received;
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(30), Cancellation));
        }
    }

    [Fact]
    public async Task The_request_bound_ends_a_stalled_request_as_no_token_not_as_cancellation()
    {
        var time = new SteppedTimeProvider();
        var (source, entra) = Source(time);
        using (source)
        {
            entra.Respond = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            };
            var received = entra.Received.Task;
            var call = source.GetAccessTokenAsync(Cancellation).AsTask();
            await received;
            time.Advance(TimeSpan.FromSeconds(9));
            Assert.False(call.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));

            // Bounded in real time, so a request that the bound did not end fails the test instead of hanging it.
            Assert.Null(await call.WaitAsync(TimeSpan.FromSeconds(30), Cancellation));
            Assert.Equal(TimeSpan.FromSeconds(10), EntraCertificateTokenSource.RequestTimeout);
        }
    }

    [Fact]
    public async Task A_connection_failure_or_any_other_fault_gives_no_token_and_no_exception()
    {
        foreach (var fault in new Exception[]
                 {
                     new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"),
                     new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"),
                     new IOException("reset"),
                     new InvalidOperationException("odd"),
                 })
        {
            var (source, entra) = Source();
            using (source)
            {
                entra.Respond = (_, _) => Task.FromException<HttpResponseMessage>(fault);
                Assert.Null(await source.GetAccessTokenAsync(Cancellation));
            }
        }
    }

    // --- Leak prevention ------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_token_assertion_identifier_or_answer_is_rendered_or_exposed()
    {
        var (source, entra) = Source();
        using (source)
        {
            var token = await source.GetAccessTokenAsync(Cancellation);
            Assert.Equal(FakeEntra.IssuedToken, token);
            var assertion = entra.Requests[0].Form["client_assertion"];

            var rendered = source.ToString();
            Assert.Equal("EntraCertificateTokenSource", rendered);
            foreach (var secret in new[] { FakeEntra.IssuedToken, assertion, Tenant.ToString("D"), Client.ToString("D") })
            {
                Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
            }

            // Positive control: the sentinel search would find the token where it is.
            Assert.Contains(FakeEntra.IssuedToken, token!, StringComparison.Ordinal);

            // The public surface gives the token only through the port's one method.
            Assert.Equal(
                ["Dispose", "GetAccessTokenAsync", "ToString", "TokenEndpointFor"],
                typeof(EntraCertificateTokenSource).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(method => !method.IsSpecialName)
                    .Select(method => method.Name)
                    .Order(StringComparer.Ordinal));
            Assert.Empty(typeof(EntraCertificateTokenSource).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        }
    }

    [Fact]
    public async Task A_failure_quotes_nothing_it_received()
    {
        const string secretAnswer = "sentinel-entra-error-description-4711";
        var (source, entra) = Source();
        using (source)
        {
            entra.Respond = (_, _) => Task.FromResult(FakeEntra.Answer($$"""{"error":"invalid_client","error_description":"{{secretAnswer}}"}""", HttpStatusCode.Unauthorized));
            Assert.Null(await source.GetAccessTokenAsync(Cancellation));
            Assert.DoesNotContain(secretAnswer, source.ToString(), StringComparison.Ordinal);
        }
    }

    // --- Compatibility with the existing adapter ----------------------------------------------------------------

    [Fact]
    public async Task The_source_satisfies_the_existing_key_vault_adapter_through_both_destination_guards()
    {
        using var certificate = TestCertificates.Rsa();
        var time = new SteppedTimeProvider();
        var entra = new FakeEntra();
        var vault = new FakeVault(time);
        using var source = new EntraCertificateTokenSource(Tenant, Client, certificate, new EntraTokenDestination(Tenant, entra), time);
        using var vaultPath = new KeyVaultDestination(FakeVault.Origin, vault);
        IKeyVaultAccessTokenSource port = source;
        var store = new KeyVaultSecretStore(FakeVault.Origin, vaultPath, port, new RecordingDelay(time), time);
        var name = ConnectorSecretNames.Verifier(TenantId.New(), Guid.CreateVersion7());

        Assert.Equal(WorkContinuity.Connectors.Monday.MondaySecretStoreStatus.Succeeded, await store.SetAsync(name, new byte[] { 1, 2, 3 }, Cancellation));
        var lookup = await store.GetAsync(name, Cancellation);
        Assert.Equal(WorkContinuity.Connectors.Monday.MondaySecretStoreStatus.Succeeded, lookup.Status);
        var value = typeof(WorkContinuity.Connectors.Monday.MondaySecretLookup).GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])value.GetValue(lookup)!);
        Assert.Equal(WorkContinuity.Connectors.Monday.MondaySecretStoreStatus.Succeeded, await store.DeleteAsync(name, Cancellation));

        // Three vault operations, one token request; every vault request carried the issued token as a bearer.
        Assert.Single(entra.Requests);
        Assert.Equal(3, vault.Exchanges.Count);
        Assert.All(vault.Exchanges, exchange => Assert.Equal("Bearer " + FakeEntra.IssuedToken, exchange.Authorization));
    }

    [Fact]
    public async Task Without_a_token_the_adapter_is_unavailable_and_the_vault_is_never_asked()
    {
        using var certificate = TestCertificates.Rsa();
        var time = new SteppedTimeProvider();
        var entra = new FakeEntra { Respond = (_, _) => Task.FromResult(FakeEntra.Answer("""{"error":"invalid_client"}""", HttpStatusCode.Unauthorized)) };
        var vault = new FakeVault(time);
        using var source = new EntraCertificateTokenSource(Tenant, Client, certificate, new EntraTokenDestination(Tenant, entra), time);
        using var vaultPath = new KeyVaultDestination(FakeVault.Origin, vault);
        var store = new KeyVaultSecretStore(FakeVault.Origin, vaultPath, source, new RecordingDelay(time), time);
        var name = ConnectorSecretNames.Verifier(TenantId.New(), Guid.CreateVersion7());

        Assert.Equal(WorkContinuity.Connectors.Monday.MondaySecretStoreStatus.Unavailable, await store.SetAsync(name, new byte[] { 1 }, Cancellation));
        Assert.Empty(vault.Exchanges);
    }

    private (EntraCertificateTokenSource Source, FakeEntra Entra) Source(SteppedTimeProvider? time = null)
    {
        var certificate = TestCertificates.Rsa();
        var entra = new FakeEntra();
        return (new EntraCertificateTokenSource(Tenant, Client, certificate, entra, time ?? new SteppedTimeProvider()), entra);
    }

    private static JsonElement Decode(string part) => JsonDocument.Parse(Base64Url.DecodeFromChars(part)).RootElement.Clone();

    // A stream that cannot report its length, so the bound is enforced while reading.
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();
    }
}
