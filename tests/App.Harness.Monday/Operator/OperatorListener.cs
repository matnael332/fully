using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Harness.Monday.Configuration;
using WorkContinuity.Harness.Monday.Diagnostics;

namespace WorkContinuity.Harness.Monday.Operator;

/// <summary>What an authorization start produced, as the listener needs it.</summary>
/// <param name="Outcome">How the start ended.</param>
/// <param name="AuthorizationAddress">Where to send the browser, when the start opened an authorization.</param>
public sealed record OperatorStart(AuthorizationStartOutcome Outcome, Uri? AuthorizationAddress)
{
    /// <summary>Names the outcome only; the address carries the state and is never rendered.</summary>
    public override string ToString() => $"OperatorStart({Outcome})";
}

/// <summary>
/// The authorization actions as the operator listener reaches them.
/// </summary>
/// <remarks>
/// Nothing here takes or returns an identity: whose authority an authorization runs under is decided behind this
/// interface, from the harness's configuration, and a request handler has no way to name one.
/// </remarks>
public interface IOperatorAuthorization
{
    /// <summary>Starts authorizing a new connection, or restoring the one named when the capability was minted.</summary>
    Task<OperatorStart> StartAsync(Guid? reauthorizeConnectionId, CancellationToken cancellationToken);

    /// <summary>Completes an authorization from the provider's callback.</summary>
    Task<AuthorizationCompletionOutcome> CompleteAsync(MondayAuthorizationCallback callback, CancellationToken cancellationToken);
}

/// <summary>
/// The operator listener's three routes and the checks every request passes first (owner decision D3G-5).
/// </summary>
/// <remarks>
/// <para>
/// The listener serves the authorization start, the provider's callback and a terminal page, and nothing else; the
/// webhook route is mapped on a separate listener with a separate route table. Every request must arrive from this
/// machine, over TLS, for exactly the configured Host, with the GET method, and without a foreign origin. Every answer
/// forbids caching and sends no referrer, so neither the start capability nor a callback's parameters outlive the
/// request in a cache or travel onward in a <c>Referer</c> header.
/// </para>
/// <para>
/// The start needs the single-use capability minted at the console and must be a navigation the operator made, not one
/// a site initiated. It answers with a redirect to the provider, so the state is never printed, and sets the browser
/// binding. The callback must be a top-level navigation and must present that binding together with the pending
/// state; otherwise it is refused before the authorizer sees it and consumes nothing. Whatever the authorizer answers,
/// the browser is sent to the same static page, which takes no parameter: the outcome is the operator console's to
/// show, as a closed value.
/// </para>
/// <para>
/// The handlers read a capability, a binding cookie and the provider's callback parameters, and nothing about whose
/// authorization this is. Logs and diagnostics carry closed values only.
/// </para>
/// </remarks>
internal sealed class OperatorListener
{
    /// <summary>The start route.</summary>
    internal const string StartPath = "/operator/start";

    /// <summary>The callback route, which is the registered redirect address's path.</summary>
    internal const string CallbackPath = HarnessOptions.CallbackPath;

    /// <summary>The terminal page.</summary>
    internal const string DonePath = "/operator/done";

    /// <summary>The start route's only parameter: the capability.</summary>
    internal const string CapabilityParameter = "c";

    /// <summary>The browser binding's cookie. The prefix makes browsers keep it host-only, Secure and on the root path.</summary>
    internal const string BindingCookie = "__Host-wce-harness-binding";

    private const string LogCategory = "WorkContinuity.Harness.Monday.Operator";
    private const string DonePage =
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Authorization step finished</title></head>"
        + "<body><p>The authorization step has finished. Return to the operator console for its outcome. You may close this tab.</p></body></html>";

    private readonly string _authority;
    private readonly string _origin;
    private readonly int _port;
    private readonly OperatorAuthorizationGate _gate;
    private readonly IOperatorAuthorization _authorization;
    private readonly HarnessDiagnostics _diagnostics;
    private readonly ILogger _logger;

    internal OperatorListener(
        HarnessOptions options,
        OperatorAuthorizationGate gate,
        IOperatorAuthorization authorization,
        HarnessDiagnostics diagnostics,
        ILoggerFactory loggers)
    {
        _authority = options.OperatorAuthority;
        _origin = options.OperatorOrigin;
        _port = options.OperatorPort;
        _gate = gate;
        _authorization = authorization;
        _diagnostics = diagnostics;
        _logger = loggers.CreateLogger(LogCategory);
    }

    /// <summary>The address the operator opens to start an authorization with a minted capability.</summary>
    internal string StartAddress(string capability) => _origin + StartPath + "?" + CapabilityParameter + "=" + capability;

    /// <summary>Adds the checks and the three routes to the operator application.</summary>
    internal void Configure(WebApplication application)
    {
        application.Use(CheckAsync);
        application.MapGet(StartPath, (RequestDelegate)StartAsync);
        application.MapGet(CallbackPath, (RequestDelegate)CallbackAsync);
        application.MapGet(DonePath, (RequestDelegate)DoneAsync);
        application.MapFallback((RequestDelegate)(context => RefuseAsync(context, OperatorRefusal.UnknownRoute)));
    }

    // Runs before every route: where the request came from and what it claims to be for.
    private async Task CheckAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var connection = context.Connection;
        SetSafetyHeaders(context.Response);

        if (connection.LocalPort != _port
            || connection.RemoteIpAddress is not { } remote
            || !IPAddress.IsLoopback(remote)
            || connection.LocalIpAddress is not { } local
            || !IPAddress.IsLoopback(local))
        {
            await RefuseAsync(context, OperatorRefusal.NotLoopback);
            return;
        }

        if (!request.IsHttps)
        {
            await RefuseAsync(context, OperatorRefusal.NotHttps);
            return;
        }

        // The exact configured authority: a page that reached this listener under another name is refused.
        if (!string.Equals(request.Host.Value, _authority, StringComparison.OrdinalIgnoreCase))
        {
            await RefuseAsync(context, OperatorRefusal.HostMismatch);
            return;
        }

        if (!HttpMethods.IsGet(request.Method))
        {
            await RefuseAsync(context, OperatorRefusal.MethodNotAllowed);
            return;
        }

        var origin = request.Headers.Origin;
        if (origin.Count > 1 || (origin.Count == 1 && !string.Equals(origin[0], _origin, StringComparison.OrdinalIgnoreCase)))
        {
            await RefuseAsync(context, OperatorRefusal.ForeignOrigin);
            return;
        }

        try
        {
            await next(context);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A route failed. The answer is the same closed refusal with the same headers, and only the exception's type
            // is logged: its text could quote what the route was working on.
            _logger.LogError("Operator request failed with {ExceptionType}.", exception.GetType().Name);
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                await RefuseAsync(context, OperatorRefusal.Faulted);
            }
        }
    }

    private async Task StartAsync(HttpContext context)
    {
        var request = context.Request;

        // The operator opens the start address directly. A navigation that any site initiated, this one included, is
        // not the operator's own act; browsers that report it are believed.
        var site = request.Headers["Sec-Fetch-Site"];
        if (site.Count > 1 || (site.Count == 1 && !string.Equals(site[0], "none", StringComparison.Ordinal)))
        {
            await RefuseAsync(context, OperatorRefusal.SiteInitiatedStart);
            return;
        }

        if (!IsNavigation(request))
        {
            await RefuseAsync(context, OperatorRefusal.NotANavigation);
            return;
        }

        var query = request.Query;
        if (query.Count == 0)
        {
            await RefuseAsync(context, OperatorRefusal.CapabilityMissing);
            return;
        }

        if (query.Count != 1 || !query.TryGetValue(CapabilityParameter, out var presented) || presented.Count != 1)
        {
            await RefuseAsync(context, OperatorRefusal.UnexpectedParameters);
            return;
        }

        var redemption = _gate.Redeem(presented[0]);
        if (redemption.Refusal != OperatorRefusal.None)
        {
            await RefuseAsync(context, redemption.Refusal);
            return;
        }

        var started = await _authorization.StartAsync(redemption.ReauthorizeConnectionId, context.RequestAborted);
        if (started.AuthorizationAddress is not { } address || StateOf(address) is not { } state)
        {
            // Nothing was opened. The capability is spent; the console shows why, as a closed value.
            _gate.Abandon();
            Accept(context, OperatorRoute.Start);
            Redirect(context.Response, DonePath);
            return;
        }

        var binding = _gate.BindBrowser(state);
        context.Response.Headers.SetCookie = string.Create(
            CultureInfo.InvariantCulture,
            $"{BindingCookie}={binding}; Max-Age={(int)OperatorAuthorizationGate.BindingLifetime.TotalSeconds}; Path=/; Secure; HttpOnly; SameSite=Lax");
        Accept(context, OperatorRoute.Start);
        Redirect(context.Response, address.AbsoluteUri);
    }

    private async Task CallbackAsync(HttpContext context)
    {
        var request = context.Request;

        // The provider returns the browser by a top-level navigation. A request a page made for a resource or by
        // script is not that.
        if (!IsNavigation(request))
        {
            await RefuseAsync(context, OperatorRefusal.NotANavigation);
            return;
        }

        string? state = null, code = null, error = null, issuer = null;
        var repeated = false;
        foreach (var (name, values) in request.Query)
        {
            var parameter = ParameterOf(name);
            _diagnostics.CallbackParameterSeen(parameter);
            if (parameter == CallbackParameter.Other)
            {
                continue;
            }

            repeated |= values.Count != 1;
            var value = values.Count > 0 ? values[0] : null;
            switch (parameter)
            {
                case CallbackParameter.State:
                    state = value;
                    break;
                case CallbackParameter.Code:
                    code = value;
                    break;
                case CallbackParameter.Error:
                    error = value;
                    break;
                case CallbackParameter.Issuer:
                    issuer = value;
                    break;
            }
        }

        if (repeated)
        {
            await RefuseAsync(context, OperatorRefusal.CallbackMalformed);
            return;
        }

        // Before the authorizer: a callback this browser did not start consumes nothing.
        var refusal = _gate.MatchCallback(state, BindingOf(request));
        if (refusal != OperatorRefusal.None)
        {
            await RefuseAsync(context, refusal);
            return;
        }

        _diagnostics.CallbackIssuer(
            issuer is null ? IssuerRelation.Absent
            : string.Equals(issuer, MondayEndpoints.Issuer, StringComparison.Ordinal) ? IssuerRelation.Equal
            : IssuerRelation.Different);

        _ = await _authorization.CompleteAsync(new MondayAuthorizationCallback(state, code, error, issuer), context.RequestAborted);

        context.Response.Headers.SetCookie = BindingCookie + "=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax";
        Accept(context, OperatorRoute.Callback);
        Redirect(context.Response, DonePath);
    }

    private Task DoneAsync(HttpContext context)
    {
        // The page takes no parameter, so nothing a request carries can be reflected, cached or left in an address.
        if (context.Request.QueryString.HasValue)
        {
            return RefuseAsync(context, OperatorRefusal.UnexpectedParameters);
        }

        Accept(context, OperatorRoute.Done);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(DonePage, context.RequestAborted);
    }

    private void Accept(HttpContext context, OperatorRoute route)
    {
        _diagnostics.OperatorRequest(route, OperatorRefusal.None);
        _logger.LogInformation("Operator request served: {Route}.", route);
    }

    private Task RefuseAsync(HttpContext context, OperatorRefusal refusal)
    {
        var route = RouteOf(context.Request.Path);
        _diagnostics.OperatorRequest(route, refusal);
        _logger.LogWarning("Operator request refused: {Route} {Refusal}.", route, refusal);

        SetSafetyHeaders(context.Response);
        context.Response.StatusCode = refusal switch
        {
            OperatorRefusal.MethodNotAllowed => StatusCodes.Status405MethodNotAllowed,
            OperatorRefusal.UnknownRoute => StatusCodes.Status404NotFound,
            OperatorRefusal.Faulted => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status403Forbidden,
        };
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private static OperatorRoute RouteOf(PathString path) =>
        path.Equals(StartPath, StringComparison.Ordinal) ? OperatorRoute.Start
        : path.Equals(CallbackPath, StringComparison.Ordinal) ? OperatorRoute.Callback
        : path.Equals(DonePath, StringComparison.Ordinal) ? OperatorRoute.Done
        : OperatorRoute.Other;

    private static CallbackParameter ParameterOf(string name) => name switch
    {
        "state" => CallbackParameter.State,
        "code" => CallbackParameter.Code,
        "error" => CallbackParameter.Error,
        "error_description" => CallbackParameter.ErrorDescription,
        "iss" => CallbackParameter.Issuer,
        "scope" => CallbackParameter.Scope,
        _ => CallbackParameter.Other,
    };

    // A browser that reports how a request was made must report a top-level document navigation.
    private static bool IsNavigation(HttpRequest request) =>
        IsAbsentOr(request.Headers["Sec-Fetch-Mode"], "navigate") && IsAbsentOr(request.Headers["Sec-Fetch-Dest"], "document");

    private static bool IsAbsentOr(StringValues values, string expected) =>
        values.Count == 0 || (values.Count == 1 && string.Equals(values[0], expected, StringComparison.Ordinal));

    // The binding cookie's value when the request carries it exactly once.
    private static string? BindingOf(HttpRequest request)
    {
        string? found = null;
        var count = 0;
        foreach (var header in request.Headers.Cookie)
        {
            if (header is null)
            {
                continue;
            }

            foreach (var part in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith(BindingCookie + "=", StringComparison.Ordinal))
                {
                    found = part[(BindingCookie.Length + 1)..];
                    count++;
                }
            }
        }

        return count == 1 ? found : null;
    }

    // The state of an authorization address, which the binding is tied to. Never logged or rendered.
    private static string? StateOf(Uri authorizationAddress)
    {
        foreach (var pair in authorizationAddress.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("state=", StringComparison.Ordinal))
            {
                var state = Uri.UnescapeDataString(pair["state=".Length..]);
                return state.Length > 0 ? state : null;
            }
        }

        return null;
    }

    private static void Redirect(HttpResponse response, string location)
    {
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = location;
        response.ContentLength = 0;
    }

    private static void SetSafetyHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    }
}
