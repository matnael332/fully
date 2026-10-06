using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Harness.Monday.Live;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G Stage B2 (owner decisions B-1, B-10, B-11 of 2026-10-06): the pinned network handler and the three destination
/// guards. The pinned handler is only constructed and inspected, never sent through; every request in these tests goes to
/// a recording in-memory handler, so no name is resolved and no socket is opened.
/// </summary>
public sealed class HarnessLiveTransportTests
{
    private static readonly Guid Tenant = Guid.Parse("4f1f6c3a-0b5e-4d7a-9c2e-1a2b3c4d5e6f");
    private static readonly string TokenUrl = "https://login.microsoftonline.com/" + Tenant.ToString("D") + "/oauth2/v2.0/token";
    private static readonly Uri Vault = new("https://wce-test-vault.vault.azure.net/");
    private const string Version = "?api-version=2025-07-01";

    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // --- The pinned handler ---------------------------------------------------------------------------------------

    [Fact]
    public void The_pinned_handler_follows_no_redirect_keeps_no_cookie_uses_no_proxy_or_ambient_credential_and_bounds_connecting()
    {
        using var handler = LiveTransport.CreatePinnedHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
        Assert.True(handler.ConnectTimeout <= TimeSpan.FromSeconds(5));
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(64, handler.MaxResponseHeadersLength);
        Assert.Null(handler.ActivityHeadersPropagator);

        // The platform validates certificates as always: no callback of our own, no connection hook.
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.LocalCertificateSelectionCallback);
        Assert.Null(handler.SslOptions.ClientCertificates);
        Assert.Null(handler.ConnectCallback);
        Assert.Null(handler.PlaintextStreamFilter);
    }

    [Fact]
    public void Each_destination_gets_its_own_guard_over_its_own_pinned_handler()
    {
        using var entra = (DelegatingHandler)LiveTransport.ForEntraToken(Tenant);
        using var vault = (DelegatingHandler)LiveTransport.ForKeyVault(Vault);
        using var monday = (DelegatingHandler)LiveTransport.ForMonday();

        Assert.IsType<EntraTokenDestination>(entra);
        Assert.IsType<KeyVaultDestination>(vault);
        Assert.IsType<MondayDestination>(monday);

        var inner = new[] { entra.InnerHandler, vault.InnerHandler, monday.InnerHandler }.Select(Assert.IsType<SocketsHttpHandler>).ToList();
        Assert.All(inner, handler =>
        {
            Assert.False(handler.AllowAutoRedirect);
            Assert.False(handler.UseCookies);
            Assert.False(handler.UseProxy);
            Assert.Null(handler.Credentials);
        });

        // Separate connection pools: nothing a request carries can ride a connection of another destination.
        Assert.Equal(3, inner.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.False(ReferenceEquals(LiveTransport.CreatePinnedHandler(), LiveTransport.CreatePinnedHandler()));

        // Every call builds a fresh pipeline: no destination shares a handler between two transports either.
        using var firstVault = (DelegatingHandler)LiveTransport.ForKeyVault(Vault);
        using var secondVault = (DelegatingHandler)LiveTransport.ForKeyVault(Vault);
        Assert.False(ReferenceEquals(firstVault.InnerHandler, secondVault.InnerHandler));
    }

    [Fact]
    public void A_guard_names_no_address_header_or_value()
    {
        using var entra = new EntraTokenDestination(Tenant, new RecordingTransport());
        using var vault = new KeyVaultDestination(Vault, new RecordingTransport());
        Assert.Equal("EntraTokenDestination", entra.ToString());
        Assert.Equal("KeyVaultDestination", vault.ToString());
    }

    // --- Entra ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_entra_guard_passes_exactly_the_tenant_token_request()
    {
        var inner = new RecordingTransport();
        using var guard = new EntraTokenDestination(Tenant, inner);
        using var invoker = new HttpMessageInvoker(guard);

        using var response = await invoker.SendAsync(TokenRequest(TokenUrl), Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    public static TheoryData<string, string> RefusedTokenRequests => new()
    {
        { "another host", "https://login.example.com/" + Tenant.ToString("D") + "/oauth2/v2.0/token" },
        { "a look-alike host", "https://login.microsoftonline.com.example.net/" + Tenant.ToString("D") + "/oauth2/v2.0/token" },
        { "HTTP", "http://login.microsoftonline.com/" + Tenant.ToString("D") + "/oauth2/v2.0/token" },
        { "another tenant", "https://login.microsoftonline.com/" + Guid.NewGuid().ToString("D") + "/oauth2/v2.0/token" },
        { "a shared tenant alias", "https://login.microsoftonline.com/common/oauth2/v2.0/token" },
        { "the v1 endpoint", "https://login.microsoftonline.com/" + Tenant.ToString("D") + "/oauth2/token" },
        { "another port", "https://login.microsoftonline.com:8443/" + Tenant.ToString("D") + "/oauth2/v2.0/token" },
        { "user information", "https://user:pass@login.microsoftonline.com/" + Tenant.ToString("D") + "/oauth2/v2.0/token" },
        { "a query", TokenUrl + "?x=1" },
        { "a fragment", TokenUrl + "#x" },
        { "a vault", "https://wce-test-vault.vault.azure.net/secrets/name" + Version },
    };

    [Theory]
    [MemberData(nameof(RefusedTokenRequests))]
    public async Task The_entra_guard_refuses_any_other_address_before_the_transport(string why, string address)
    {
        Assert.False(string.IsNullOrEmpty(why));
        var inner = new RecordingTransport();
        using var guard = new EntraTokenDestination(Tenant, inner);
        using var invoker = new HttpMessageInvoker(guard);

        var refusal = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(TokenRequest(address), Cancellation));

        Assert.Equal(0, inner.Calls);
        Assert.Equal(HttpRequestError.ConnectionError, refusal.HttpRequestError);
        Assert.DoesNotContain("microsoftonline", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Tenant.ToString("D"), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_entra_guard_refuses_a_bearer_credential_a_cookie_another_method_or_another_body()
    {
        var withBearer = TokenRequest(TokenUrl);
        withBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-vault-token");
        var withCookie = TokenRequest(TokenUrl);
        withCookie.Headers.Add("Cookie", "a=b");
        var asGet = new HttpRequestMessage(HttpMethod.Get, TokenUrl);
        var asJson = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        var noBody = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        var formAsPut = new HttpRequestMessage(HttpMethod.Put, TokenUrl) { Content = new FormUrlEncodedContent([new("client_id", "x")]) };

        foreach (var request in new[] { withBearer, withCookie, asGet, asJson, noBody, formAsPut })
        {
            var inner = new RecordingTransport();
            using var guard = new EntraTokenDestination(Tenant, inner);
            using var invoker = new HttpMessageInvoker(guard);
            await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, Cancellation));
            Assert.Equal(0, inner.Calls);
        }
    }

    [Fact]
    public void The_entra_guard_needs_a_tenant()
    {
        Assert.Throws<ArgumentException>(() => new EntraTokenDestination(Guid.Empty, new RecordingTransport()));
    }

    // --- Key Vault ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_vault_guard_passes_exactly_the_adapter_get_set_and_delete()
    {
        var inner = new RecordingTransport();
        using var guard = new KeyVaultDestination(Vault, inner);
        using var invoker = new HttpMessageInvoker(guard);

        foreach (var request in new[]
                 {
                     VaultRequest(HttpMethod.Get, "secrets/wce-monday-pkce-a1/" + Version),
                     VaultRequest(HttpMethod.Put, "secrets/wce-monday-pkce-a1" + Version, "{}"),
                     VaultRequest(HttpMethod.Delete, "secrets/wce-monday-pkce-a1" + Version),
                 })
        {
            using var response = await invoker.SendAsync(request, Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task The_vault_guard_refuses_a_token_request_and_every_other_request_before_the_transport()
    {
        var refused = new List<HttpRequestMessage>
        {
            // The shape of the Entra token request, carrying the client assertion: never sent to a vault.
            TokenRequest(Vault + "secrets/name" + Version),
            VaultRequest(HttpMethod.Post, "secrets/name" + Version, "{}"),
            VaultRequest(HttpMethod.Patch, "secrets/name" + Version, "{}"),
            VaultRequest(HttpMethod.Head, "secrets/name/" + Version),
            VaultRequest(HttpMethod.Get, "secrets/name/", bearer: true),
            VaultRequest(HttpMethod.Get, "secrets/name/?api-version=7.4"),
            VaultRequest(HttpMethod.Get, "secrets/name/" + Version + "&maxresults=1"),
            VaultRequest(HttpMethod.Get, "secrets/name" + Version),
            VaultRequest(HttpMethod.Get, "secrets/name/versions" + Version),
            VaultRequest(HttpMethod.Get, "secrets/" + Version),
            VaultRequest(HttpMethod.Get, "secrets/../keys/name/" + Version),
            VaultRequest(HttpMethod.Get, "deletedsecrets/name/" + Version),
            VaultRequest(HttpMethod.Post, "deletedsecrets/name/recover" + Version, "{}"),
            VaultRequest(HttpMethod.Delete, "deletedsecrets/name" + Version),
            VaultRequest(HttpMethod.Get, "secrets/Name/" + Version),
            VaultRequest(HttpMethod.Put, "secrets/name/" + Version, "{}"),
            VaultRequest(HttpMethod.Put, "secrets/name" + Version),
            VaultRequest(HttpMethod.Get, "secrets/name/" + Version, "{}"),
            VaultRequest(HttpMethod.Delete, "secrets/name" + Version, "{}"),
            VaultRequest(HttpMethod.Get, "secrets/name/" + Version, bearer: false),
            Absolute(HttpMethod.Get, "https://other-vault.vault.azure.net/secrets/name/" + Version),
            Absolute(HttpMethod.Get, "http://wce-test-vault.vault.azure.net/secrets/name/" + Version),
            Absolute(HttpMethod.Get, "https://wce-test-vault.vault.azure.net:444/secrets/name/" + Version),
            Absolute(HttpMethod.Get, "https://u:p@wce-test-vault.vault.azure.net/secrets/name/" + Version),
            Absolute(HttpMethod.Get, "https://wce-test-vault.vault.azure.net/secrets/name/" + Version + "#f"),
            Absolute(HttpMethod.Get, TokenUrl),
        };
        var formPut = VaultRequest(HttpMethod.Put, "secrets/name" + Version);
        formPut.Content = new FormUrlEncodedContent([new("a", "b")]);
        refused.Add(formPut);
        var cookie = VaultRequest(HttpMethod.Get, "secrets/name/" + Version);
        cookie.Headers.Add("Cookie", "a=b");
        refused.Add(cookie);
        var basic = VaultRequest(HttpMethod.Get, "secrets/name/" + Version, bearer: false);
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");
        refused.Add(basic);

        foreach (var request in refused)
        {
            var inner = new RecordingTransport();
            using var guard = new KeyVaultDestination(Vault, inner);
            using var invoker = new HttpMessageInvoker(guard);
            await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, Cancellation));
            Assert.Equal(0, inner.Calls);
        }
    }

    [Theory]
    [InlineData("http://wce-test-vault.vault.azure.net/")]
    [InlineData("https://wce-test-vault.vault.azure.net/secrets/")]
    [InlineData("https://login.microsoftonline.com/")]
    [InlineData("https://wce-test-vault.vault.azure.net:8443/")]
    public void The_vault_guard_takes_only_a_vault_origin(string address)
    {
        Assert.Throws<ArgumentException>(() => new KeyVaultDestination(new Uri(address), new RecordingTransport()));
    }

    // --- monday ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_monday_guard_passes_only_posts_to_the_three_fixed_endpoints()
    {
        foreach (var endpoint in new[] { MondayEndpoints.Token, MondayEndpoints.Revoke, MondayEndpoints.Api })
        {
            var inner = new RecordingTransport();
            using var guard = new MondayDestination(inner);
            using var invoker = new HttpMessageInvoker(guard);
            using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent("{}") }, Cancellation);
            Assert.Equal(1, inner.Calls);

            foreach (var refused in new[]
                     {
                         new HttpRequestMessage(HttpMethod.Get, endpoint),
                         new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.AbsoluteUri + "?x=1")),
                         new HttpRequestMessage(HttpMethod.Post, new UriBuilder(endpoint) { Scheme = "http", Port = 80 }.Uri),
                         new HttpRequestMessage(HttpMethod.Post, new UriBuilder(endpoint) { Port = 8443 }.Uri),
                         new HttpRequestMessage(HttpMethod.Post, new UriBuilder(endpoint) { Host = "example.com" }.Uri),
                         new HttpRequestMessage(HttpMethod.Post, new Uri(TokenUrl)),
                         new HttpRequestMessage(HttpMethod.Post, new Uri(Vault, "secrets/name")),
                     })
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(refused, Cancellation));
            }

            Assert.Equal(1, inner.Calls);
        }
    }

    // --- Redirects ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_redirect_is_handed_back_unfollowed_and_its_target_is_never_requested()
    {
        var inner = new RecordingTransport
        {
            Respond = _ =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("https://attacker.example/collect");
                return redirect;
            },
        };
        using var guard = new EntraTokenDestination(Tenant, inner);
        using var invoker = new HttpMessageInvoker(guard);

        using var response = await invoker.SendAsync(TokenRequest(TokenUrl), Cancellation);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(1, inner.Calls);

        // A redirect's target is not an address the guard admits, so even a hand-built follow-up would be refused.
        await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(TokenRequest("https://attacker.example/collect"), Cancellation));
        Assert.Equal(1, inner.Calls);
    }

    private static HttpRequestMessage TokenRequest(string address) =>
        new(HttpMethod.Post, address)
        {
            Content = new FormUrlEncodedContent([new("client_id", "x"), new("client_assertion", "synthetic.assertion.value")]),
        };

    private static HttpRequestMessage VaultRequest(HttpMethod method, string relative, string? json = null, bool bearer = true)
    {
        var request = new HttpRequestMessage(method, new Uri(Vault, relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-vault-token");
        }

        return request;
    }

    private static HttpRequestMessage Absolute(HttpMethod method, string address)
    {
        var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-vault-token");
        return request;
    }
}
