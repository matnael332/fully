using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Diagnostics;
using WorkContinuity.Harness.Monday.Hosting;
using WorkContinuity.Harness.Monday.Operator;
using WorkContinuity.Integration.Tests.Connectors;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, offline slice: the operator listener. The fixed operator identity cannot be attached to through a request
/// the operator did not make or a browser that did not start the authorization: the start needs a single-use capability
/// from the console, every request must be for the exact loopback Host over TLS and from no foreign origin, the callback
/// needs the browser binding as well as the state, nothing is cached or sent on as a referrer, the terminal page takes no
/// parameter, and each listener serves its own routes only. Every flow here is synthetic.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HarnessOperatorSafetyTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_start_needs_the_capability_the_console_minted_and_takes_it_once()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var address = StartAddressOf(await harness.ConsoleAsync("authorize"));

        // Without it, with a malformed one and with a well-formed stranger: refused, and nothing is opened.
        await RefusedAsync(harness, "/operator/start", OperatorRoute.Start, OperatorRefusal.CapabilityMissing);
        await RefusedAsync(harness, "/operator/start?c=short", OperatorRoute.Start, OperatorRefusal.CapabilityMalformed);
        await RefusedAsync(harness, "/operator/start?c=" + new string('A', 43), OperatorRoute.Start, OperatorRefusal.CapabilityUnknown);
        await RefusedAsync(harness, address + "&next=1", OperatorRoute.Start, OperatorRefusal.UnexpectedParameters);
        await RefusedAsync(harness, address + "&c=" + new string('A', 43), OperatorRoute.Start, OperatorRefusal.UnexpectedParameters);
        Assert.Equal(0L, (await harness.Host.Operator.ReadEvidenceAsync(Token)).Value.Transactions.Open);

        // The refusals spent nothing: the minted capability still works, once.
        using (var started = await harness.GetOperatorAsync(address))
        {
            Assert.Equal(HttpStatusCode.SeeOther, started.StatusCode);
            Assert.Equal("auth.monday.com", started.Headers.Location!.Host);
        }

        await RefusedAsync(harness, address, OperatorRoute.Start, OperatorRefusal.CapabilityUsed);
        Assert.Equal(1L, (await harness.Host.Operator.ReadEvidenceAsync(Token)).Value.Transactions.Open);
        Assert.Equal(1, harness.Host.Diagnostics.Snapshot().AuthorizationStarts[AuthorizationStartOutcome.Started]);
    }

    [Fact]
    public async Task A_capability_expires_after_two_minutes_and_a_new_one_replaces_the_old()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);

        var expired = StartAddressOf(await harness.ConsoleAsync("authorize"));
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(2);
        await RefusedAsync(harness, expired, OperatorRoute.Start, OperatorRefusal.CapabilityExpired);

        // Just inside its lifetime it works.
        var fresh = StartAddressOf(await harness.ConsoleAsync("authorize"));
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(2).AddSeconds(-1);
        using (var started = await harness.GetOperatorAsync(fresh))
        {
            Assert.Equal(HttpStatusCode.SeeOther, started.StatusCode);
        }

        // There is at most one: minting again makes the earlier one a stranger, and drops the pending browser binding.
        var first = StartAddressOf(await harness.ConsoleAsync("authorize"));
        var pending = await harness.StartAuthorizationAsync();
        await RefusedAsync(harness, first, OperatorRoute.Start, OperatorRefusal.CapabilityUnknown);
        _ = await harness.ConsoleAsync("authorize");
        using var orphaned = await harness.CallbackAsync(pending.State, HarnessSentinels.Code, pending.Binding);
        Assert.Equal(HttpStatusCode.Forbidden, orphaned.StatusCode);
        Assert.Empty(harness.Host.Diagnostics.Snapshot().AuthorizationCompletions);
    }

    [Fact]
    public async Task Every_request_must_be_a_get_for_the_exact_loopback_host_from_no_foreign_origin()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var address = StartAddressOf(await harness.ConsoleAsync("authorize"));
        var port = harness.Options.OperatorPort;

        // Another name for the same listener, as a rebinding page would use, and another port's authority.
        foreach (var host in new[] { $"localhost:{port}", $"evil.example.test:{port}", "127.0.0.1", $"127.0.0.1:{port + 1}" })
        {
            await RefusedAsync(harness, address, OperatorRoute.Start, OperatorRefusal.HostMismatch, request => request.Headers.Host = host);
            await RefusedAsync(harness, "/operator/done", OperatorRoute.Done, OperatorRefusal.HostMismatch, request => request.Headers.Host = host);
        }

        foreach (var origin in new[] { "https://evil.example.test", "null", $"http://127.0.0.1:{port}", "https://auth.monday.com" })
        {
            await RefusedAsync(
                harness, address, OperatorRoute.Start, OperatorRefusal.ForeignOrigin, request => request.Headers.TryAddWithoutValidation("Origin", origin));
        }

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Head })
        {
            using var request = new HttpRequestMessage(method, address);
            using var response = await harness.OperatorClient.SendAsync(request, Token);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        await RefusedAsync(harness, "/operator/other", OperatorRoute.Other, OperatorRefusal.UnknownRoute, expected: HttpStatusCode.NotFound);
        await RefusedAsync(harness, "/", OperatorRoute.Other, OperatorRefusal.UnknownRoute, expected: HttpStatusCode.NotFound);

        // None of it spent the capability or opened anything: the operator's own request still starts.
        using var started = await harness.GetOperatorAsync(address, request => request.Headers.TryAddWithoutValidation("Origin", harness.Options.OperatorOrigin));
        Assert.Equal(HttpStatusCode.SeeOther, started.StatusCode);
    }

    [Fact]
    public async Task A_start_that_a_site_initiated_and_a_request_that_is_not_a_navigation_are_refused()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var address = StartAddressOf(await harness.ConsoleAsync("authorize"));

        foreach (var site in new[] { "cross-site", "same-site", "same-origin" })
        {
            await RefusedAsync(
                harness, address, OperatorRoute.Start, OperatorRefusal.SiteInitiatedStart, request => request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site));
        }

        await RefusedAsync(harness, address, OperatorRoute.Start, OperatorRefusal.NotANavigation, request => request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors"));
        await RefusedAsync(harness, address, OperatorRoute.Start, OperatorRefusal.NotANavigation, request => request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "image"));

        // What a browser sends for an address the operator typed or pasted is accepted.
        var started = await harness.GetOperatorAsync(address, request =>
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        });
        Assert.Equal(HttpStatusCode.SeeOther, started.StatusCode);
        var state = HarnessTestHost.QueryValue(started.Headers.Location!, "state");
        var binding = HarnessTestHost.BindingOf(started);
        started.Dispose();

        // The callback arrives by the provider's cross-site redirect, which is a navigation; a subresource or script request is not.
        foreach (var (name, value) in new[] { ("Sec-Fetch-Mode", "cors"), ("Sec-Fetch-Mode", "no-cors"), ("Sec-Fetch-Dest", "image"), ("Sec-Fetch-Dest", "empty") })
        {
            using var refused = await harness.CallbackAsync(state, HarnessSentinels.Code, binding, adjust: request => request.Headers.TryAddWithoutValidation(name, value));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal(4, harness.Host.Diagnostics.Snapshot().OperatorRequests[(OperatorRoute.Callback, OperatorRefusal.NotANavigation)]);
        Assert.Empty(harness.Host.Diagnostics.Snapshot().AuthorizationCompletions);

        harness.ScriptGrant();
        using var completed = await harness.CallbackAsync(state, HarnessSentinels.Code, binding, adjust: request =>
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        });
        Assert.Equal(HttpStatusCode.SeeOther, completed.StatusCode);
        Assert.Equal(1, harness.Host.Diagnostics.Snapshot().AuthorizationCompletions[AuthorizationCompletionOutcome.Completed]);
    }

    [Fact]
    public async Task A_client_that_reports_no_fetch_metadata_is_held_by_the_capability_and_the_binding_alone()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var address = StartAddressOf(await harness.ConsoleAsync("authorize"));

        // Fetch metadata is a browser's own report, and an older browser or another client sends none. Its absence is
        // accepted, so it is never what protects a route. Without the capability such a start still opens nothing.
        await RefusedAsync(harness, "/operator/start", OperatorRoute.Start, OperatorRefusal.CapabilityMissing, AssertNoFetchMetadata);
        await RefusedAsync(harness, "/operator/start?c=" + new string('A', 43), OperatorRoute.Start, OperatorRefusal.CapabilityUnknown, AssertNoFetchMetadata);
        using var started = await harness.GetOperatorAsync(address, AssertNoFetchMetadata);
        Assert.Equal(HttpStatusCode.SeeOther, started.StatusCode);
        var state = HarnessTestHost.QueryValue(started.Headers.Location!, "state");
        var binding = HarnessTestHost.BindingOf(started);

        // And without the binding such a callback reaches nothing, whatever valid state it carries.
        using (var unbound = await harness.CallbackAsync(state, HarnessSentinels.Code, binding: null, adjust: AssertNoFetchMetadata))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode);
        }

        Assert.Equal(1, harness.Host.Diagnostics.Snapshot().OperatorRequests[(OperatorRoute.Callback, OperatorRefusal.BindingMissing)]);
        Assert.Empty(harness.Host.Diagnostics.Snapshot().AuthorizationCompletions);

        // With both, the same client completes.
        harness.ScriptGrant();
        using var completed = await harness.CallbackAsync(state, HarnessSentinels.Code, binding, adjust: AssertNoFetchMetadata);
        Assert.Equal(HttpStatusCode.SeeOther, completed.StatusCode);
        Assert.Equal(1, harness.Host.Diagnostics.Snapshot().AuthorizationCompletions[AuthorizationCompletionOutcome.Completed]);
    }

    [Fact]
    public async Task A_callback_without_the_browser_binding_never_reaches_the_authorizer_and_consumes_nothing()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var monday = Assert.IsType<FakeMonday>(harness.Monday);
        var started = await harness.StartAuthorizationAsync();

        // The valid state alone, as someone who obtained the callback address would present it: no binding, a malformed
        // one, a well-formed stranger, and the binding twice.
        using (var unbound = await harness.CallbackAsync(started.State, HarnessSentinels.Code, binding: null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode);
        }

        using (var malformed = await harness.CallbackAsync(started.State, HarnessSentinels.Code, "not-a-binding"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, malformed.StatusCode);
        }

        using (var stranger = await harness.CallbackAsync(started.State, HarnessSentinels.Code, new string('B', 43)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        }

        using (var twice = await harness.CallbackAsync(
                   started.State,
                   HarnessSentinels.Code,
                   started.Binding,
                   adjust: request => request.Headers.TryAddWithoutValidation("Cookie", HarnessTestHost.BindingCookie + "=" + new string('B', 43))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, twice.StatusCode);
        }

        // The right binding with another state, and a repeated state.
        using (var otherState = await harness.CallbackAsync(new string('0', 32) + "." + new string('C', 43), HarnessSentinels.Code, started.Binding))
        {
            Assert.Equal(HttpStatusCode.Forbidden, otherState.StatusCode);
        }

        using (var repeated = await harness.CallbackAsync(started.State, HarnessSentinels.Code, started.Binding, "&state=" + Uri.EscapeDataString(started.State)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, repeated.StatusCode);
        }

        var refusals = harness.Host.Diagnostics.Snapshot().OperatorRequests;
        Assert.Equal(3, refusals[(OperatorRoute.Callback, OperatorRefusal.BindingMissing)]);
        Assert.Equal(2, refusals[(OperatorRoute.Callback, OperatorRefusal.BindingMismatch)]);
        Assert.Equal(1, refusals[(OperatorRoute.Callback, OperatorRefusal.CallbackMalformed)]);

        // None of them reached the authorizer: nothing was exchanged, and the transaction is still open.
        Assert.Empty(harness.Host.Diagnostics.Snapshot().AuthorizationCompletions);
        Assert.Empty(monday.Requests);
        Assert.Equal(1L, (await harness.Host.Operator.ReadEvidenceAsync(Token)).Value.Transactions.Open);

        // So the browser that started the authorization can still complete it.
        harness.ScriptGrant();
        using var completed = await harness.CallbackAsync(started.State, HarnessSentinels.Code, started.Binding);
        Assert.Equal(HttpStatusCode.SeeOther, completed.StatusCode);
        Assert.Single(await harness.ConnectionsAsync());

        // The pending authorization is single use: the same callback again is refused before the authorizer.
        using var again = await harness.CallbackAsync(started.State, HarnessSentinels.Code, started.Binding);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(1, harness.Host.Diagnostics.Snapshot().AuthorizationCompletions.Values.Sum());
        Assert.Equal(1, monday.TokenCalls);
    }

    [Fact]
    public async Task A_browser_binding_does_not_outlive_the_authorization_codes_validity()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var started = await harness.StartAuthorizationAsync();

        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(10);
        using var late = await harness.CallbackAsync(started.State, HarnessSentinels.Code, started.Binding);

        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
        Assert.Empty(harness.Host.Diagnostics.Snapshot().AuthorizationCompletions);
    }

    [Fact]
    public async Task Start_and_callback_answers_are_never_cached_send_no_referrer_and_end_on_a_parameter_free_page()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var address = StartAddressOf(await harness.ConsoleAsync("authorize"));

        using var started = await harness.GetOperatorAsync(address);
        AssertSafetyHeaders(started);

        // The binding cookie: host-only by its prefix, never readable by script, never sent over plain HTTP, sent on the
        // provider's top-level redirect back, and gone when the code can no longer be exchanged.
        var cookie = Assert.Single(HarnessTestHost.CookiesOf(started));
        var attributes = cookie.Split(';', StringSplitOptions.TrimEntries).Skip(1).ToArray();
        Assert.StartsWith("__Host-", cookie, StringComparison.Ordinal);
        Assert.Equal(["Max-Age=600", "Path=/", "Secure", "HttpOnly", "SameSite=Lax"], attributes);
        var binding = HarnessTestHost.BindingOf(started);
        var state = HarnessTestHost.QueryValue(started.Headers.Location!, "state");

        // The provider address carries what the browser must carry, and nothing of the capability.
        Assert.DoesNotContain(new Uri(harness.Options.OperatorOrigin + address).Query[3..], started.Headers.Location!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("S256", HarnessTestHost.QueryValue(started.Headers.Location!, "code_challenge_method"));
        Assert.Equal(harness.Options.RedirectUri.AbsoluteUri, HarnessTestHost.QueryValue(started.Headers.Location!, "redirect_uri"));

        harness.ScriptGrant();
        using var callback = await harness.CallbackAsync(state, HarnessSentinels.Code, binding);
        AssertSafetyHeaders(callback);
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);

        // The redirect names the terminal page and carries no parameter; the binding is cleared.
        Assert.Equal("/operator/done", callback.Headers.Location!.OriginalString);
        Assert.StartsWith(HarnessTestHost.BindingCookie + "=; Max-Age=0;", Assert.Single(HarnessTestHost.CookiesOf(callback)), StringComparison.Ordinal);
        Assert.Equal(0, callback.Content.Headers.ContentLength ?? 0);

        using var done = await harness.GetOperatorAsync("/operator/done");
        AssertSafetyHeaders(done);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var page = await done.Content.ReadAsStringAsync(Token);
        Assert.Contains("Return to the operator console", page, StringComparison.Ordinal);
        foreach (var leaked in new[] { state, binding, HarnessSentinels.Code, "Completed", "connection" })
        {
            Assert.DoesNotContain(leaked, page, StringComparison.Ordinal);
        }

        // The terminal page takes no parameter at all, so nothing can be reflected or left in an address.
        await RefusedAsync(harness, "/operator/done?outcome=Completed", OperatorRoute.Done, OperatorRefusal.UnexpectedParameters);
        await RefusedAsync(harness, "/operator/done?", OperatorRoute.Done, OperatorRefusal.UnexpectedParameters);

        // A refusal carries the same headers and no body.
        using var refused = await harness.GetOperatorAsync("/operator/callback?state=x&code=y");
        AssertSafetyHeaders(refused);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Empty(await refused.Content.ReadAsByteArrayAsync(Token));
    }

    [Fact]
    public async Task Each_listener_serves_its_own_routes_only_and_both_bind_loopback()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var connection = await harness.AuthorizeAsync();
        var address = StartAddressOf(await harness.ConsoleAsync("authorize"));
        var webhookPath = MondayWebhookHost.PathOf(connection);

        Assert.Equal("127.0.0.1", harness.Host.WebhookAddress.Host);
        Assert.Equal(Uri.UriSchemeHttp, harness.Host.WebhookAddress.Scheme);
        Assert.NotEqual(harness.Options.OperatorPort, harness.Host.WebhookAddress.Port);

        // The operator routes do not exist on the webhook listener, whatever capability or binding a request carries.
        var operatorRequests = harness.Host.Diagnostics.Snapshot().OperatorRequests;
        foreach (var path in new[] { address, "/operator/start", "/operator/callback?state=x&code=y", "/operator/done" })
        {
            using var get = await harness.WebhookClient.GetAsync(path, Token);
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            using var post = await harness.WebhookClient.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"), Token);
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        }

        // None of them reached the operator listener's handlers: it counted nothing.
        Assert.Equal(operatorRequests, harness.Host.Diagnostics.Snapshot().OperatorRequests);

        // The webhook route does not exist on the operator listener: a delivery there is refused before any route, and a
        // GET finds nothing.
        var token = WebhookTestTokens.Mint(connection, HarnessTestHost.Account, harness.Clock.UtcNow);
        using (var delivery = new HttpRequestMessage(HttpMethod.Post, webhookPath))
        {
            delivery.Content = new StringContent(HarnessTestHost.WebhookBody("1772099001", "Done", WebhookData.NewTrigger(), "1771812698"), Encoding.UTF8, "application/json");
            delivery.Headers.TryAddWithoutValidation("Authorization", token);
            using var refused = await harness.OperatorClient.SendAsync(delivery, Token);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);
        }

        await RefusedAsync(harness, webhookPath, OperatorRoute.Other, OperatorRefusal.UnknownRoute, expected: HttpStatusCode.NotFound);
        Assert.Empty((await harness.Host.Operator.ReadEvidenceAsync(Token)).Value.Receipts);

        // The same delivery on its own listener is taken; only POST exists there.
        using (var taken = await harness.PostWebhookAsync(
                   connection, HarnessTestHost.WebhookBody("1772099001", "Done", WebhookData.NewTrigger(), "1771812698"), token))
        {
            Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        }

        using var getWebhook = await harness.WebhookClient.GetAsync(webhookPath, Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getWebhook.StatusCode);

        // The operator listener speaks TLS only: a plain HTTP request to its port gets no answer.
        using var plain = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{harness.Options.OperatorPort}"), Timeout = TimeSpan.FromSeconds(10) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => plain.GetAsync("/operator/done", Token));
    }

    [Fact]
    public async Task A_request_from_another_machine_or_not_over_tls_is_refused_behind_the_listeners_own_binding()
    {
        // The operator listener binds loopback with TLS only, so no socket of the harness can deliver such a request. The
        // request checks stand behind that binding all the same: here the listener's pipeline is built with no listener at
        // all, and is handed requests as another binding would have delivered them.
        var port = HarnessTestHost.FreePort();
        var options = HarnessTestHost.OptionsFor(fixture.ApplicationConnectionString, TenantId.New(), UserId.New(), port);
        var diagnostics = new HarnessDiagnostics();
        using var loggers = new HarnessLoggerFactory(new CapturingHarnessSink(), diagnostics, options.Tenant, new TenantContextAccessor());
        var listener = new OperatorListener(
            options, new OperatorAuthorizationGate(new MovingClock(DateTimeOffset.UtcNow)), new UnreachableAuthorization(), diagnostics, loggers);

        // A server is registered because an application cannot be built without one; it is never started.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.Services.AddRoutingCore();
        await using var application = builder.Build();
        listener.Configure(application);
        var pipeline = ((IApplicationBuilder)application).Build();

        async Task<HttpResponse> SendAsync(Action<DefaultHttpContext> change)
        {
            var context = new DefaultHttpContext { RequestServices = application.Services };
            context.Connection.LocalPort = port;
            context.Connection.LocalIpAddress = IPAddress.Loopback;
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("127.0.0.1", port);
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/operator/done";
            change(context);
            await pipeline(context);
            return context.Response;
        }

        // From another machine, to an address of this machine that is not loopback, on another port, and from no known address.
        foreach (var change in new Action<DefaultHttpContext>[]
                 {
                     context => context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9"),
                     context => context.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.10"),
                     context => context.Connection.LocalPort = port + 1,
                     context => context.Connection.RemoteIpAddress = null,
                 })
        {
            var refused = await SendAsync(change);
            Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
            Assert.Equal("no-store", refused.Headers.CacheControl.ToString());
        }

        Assert.Equal(4, diagnostics.Snapshot().OperatorRequests.GetValueOrDefault((OperatorRoute.Done, OperatorRefusal.NotLoopback)));

        // Over plain HTTP.
        var plain = await SendAsync(context => context.Request.Scheme = "http");
        Assert.Equal(StatusCodes.Status403Forbidden, plain.StatusCode);
        Assert.Equal(1, diagnostics.Snapshot().OperatorRequests.GetValueOrDefault((OperatorRoute.Done, OperatorRefusal.NotHttps)));

        // Positive control: the same request, local and over TLS, passes these checks and goes on to the routes, which
        // this bare pipeline does not run. Nothing more was refused.
        var passed = await SendAsync(_ => { });
        Assert.NotEqual(StatusCodes.Status403Forbidden, passed.StatusCode);
        Assert.Equal(5, diagnostics.Snapshot().OperatorRequests.Values.Sum());
    }

    [Fact]
    public async Task A_connection_to_restore_is_named_at_the_console_and_never_by_a_request()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var connection = await harness.AuthorizeAsync();
        var address = StartAddressOf(await harness.ConsoleAsync("authorize"));

        // A request cannot add a connection to a capability that was minted for a new one.
        await RefusedAsync(harness, address + $"&connection={connection:D}", OperatorRoute.Start, OperatorRefusal.UnexpectedParameters);
        await RefusedAsync(harness, address + $"&reauthorize={connection:D}", OperatorRoute.Start, OperatorRefusal.UnexpectedParameters);

        // Named at the console, an active connection is not restorable: the start opens nothing, by the product's own rule.
        var restore = StartAddressOf(await harness.ConsoleAsync($"reauthorize {connection:D}"));
        using var started = await harness.GetOperatorAsync(restore);
        Assert.Equal("/operator/done", started.Headers.Location!.OriginalString);
        Assert.Equal(1, harness.Host.Diagnostics.Snapshot().AuthorizationStarts[AuthorizationStartOutcome.NotReauthorizable]);
    }

    [Fact]
    public async Task A_route_that_fails_answers_a_closed_refusal_with_the_same_headers_and_logs_only_the_exceptions_type()
    {
        await using var harness = await HarnessTestHost.StartAsync(fixture);
        var started = await harness.StartAuthorizationAsync();

        // The fake provider was given no answer for the exchange, so it throws from inside the transport: a failure no
        // layer below the listener expects, with a text of its own.
        using var failed = await harness.CallbackAsync(started.State, HarnessSentinels.Code, started.Binding);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        AssertSafetyHeaders(failed);
        Assert.Empty(await failed.Content.ReadAsByteArrayAsync(Token));
        Assert.False(failed.Headers.Contains("Location"));

        var diagnostics = harness.Host.Diagnostics.Snapshot();
        Assert.Equal(1, diagnostics.OperatorRequests[(OperatorRoute.Callback, OperatorRefusal.Faulted)]);
        Assert.Equal(1, diagnostics.ProviderCalls[(ProviderEndpoint.Token, ProviderCallOutcome.NotSent)]);
        Assert.Empty(diagnostics.AuthorizationCompletions);

        // The log names the exception's type and nothing of its text.
        Assert.Contains(harness.Logs.Records, record => record.Category == "WorkContinuity.Harness.Monday.Operator" && record.Message == "Operator request failed with InvalidOperationException.");
        Assert.DoesNotContain("Unscripted", harness.Logs.Text, StringComparison.Ordinal);
        Assert.All(harness.Logs.Records, record => Assert.Null(record.ExceptionType));
        Assert.Empty(await harness.ConnectionsAsync());
    }

    // The authorization actions of a listener whose routes are never reached.
    private sealed class UnreachableAuthorization : IOperatorAuthorization
    {
        public Task<OperatorStart> StartAsync(Guid? reauthorizeConnectionId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No route is reached in this test.");

        public Task<AuthorizationCompletionOutcome> CompleteAsync(MondayAuthorizationCallback callback, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No route is reached in this test.");
    }

    private static string StartAddressOf(string consoleAnswer) =>
        new Uri(consoleAnswer.Split('\n', StringSplitOptions.TrimEntries).Single(line => line.StartsWith("https://", StringComparison.Ordinal))).PathAndQuery;

    // A header that is missing fails the comparison, like one with another value.
    private static void AssertSafetyHeaders(HttpResponseMessage response)
    {
        Assert.Equal("no-store", HarnessTestHost.HeaderOf(response, "Cache-Control"));
        Assert.Equal("no-cache", HarnessTestHost.HeaderOf(response, "Pragma"));
        Assert.Equal("no-referrer", HarnessTestHost.HeaderOf(response, "Referrer-Policy"));
        Assert.Equal("nosniff", HarnessTestHost.HeaderOf(response, "X-Content-Type-Options"));
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", HarnessTestHost.HeaderOf(response, "Content-Security-Policy"));
        Assert.False(response.Headers.Contains("Server"));
    }

    private static void AssertNoFetchMetadata(HttpRequestMessage request) =>
        Assert.DoesNotContain(request.Headers, header => header.Key.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase));

    private async Task RefusedAsync(
        HarnessTestHost harness,
        string pathAndQuery,
        OperatorRoute route,
        OperatorRefusal refusal,
        Action<HttpRequestMessage>? adjust = null,
        HttpStatusCode expected = HttpStatusCode.Forbidden)
    {
        var before = harness.Host.Diagnostics.Snapshot().OperatorRequests.GetValueOrDefault((route, refusal));
        using var response = await harness.GetOperatorAsync(pathAndQuery, adjust);

        Assert.Equal(expected, response.StatusCode);
        Assert.False(response.Headers.Contains("Location"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
        AssertSafetyHeaders(response);
        Assert.Equal(before + 1, harness.Host.Diagnostics.Snapshot().OperatorRequests.GetValueOrDefault((route, refusal)));
    }
}
