using System.Diagnostics;
using WorkContinuity.Core.Correlation;

namespace WorkContinuity.Web.Platform;

/// <summary>
/// Establishes the correlation context for every HTTP request and echoes it back on the response.
/// </summary>
/// <remarks>
/// An inbound header value is untrusted input. It is accepted only when it passes
/// <see cref="CorrelationId.TryParse"/>; anything else is silently replaced with a freshly
/// generated id. Because the response header is written from the parsed value rather than the raw
/// header, a caller cannot use this path to inject additional headers or forge log lines.
/// </remarks>
public sealed class CorrelationMiddleware
{
    /// <summary>The stable request and response header name.</summary>
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;
    private readonly ICorrelationContextAccessor _correlationContextAccessor;

    /// <summary>Creates the middleware.</summary>
    public CorrelationMiddleware(
        RequestDelegate next,
        ICorrelationContextAccessor correlationContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(correlationContextAccessor);

        _next = next;
        _correlationContextAccessor = correlationContextAccessor;
    }

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // StringValues.ToString() joins repeated headers with ", ", which fails validation and so
        // yields a generated id. A duplicated header therefore cannot smuggle a value through.
        var inboundHeader = context.Request.Headers[HeaderName].ToString();
        var correlationContext = CorrelationContext.FromInbound(inboundHeader);

        _correlationContextAccessor.Start(correlationContext);
        context.Response.Headers[HeaderName] = correlationContext.CorrelationId.Value;

        // The request span carries the same id, so a trace in Application Insights and a log line or
        // response header can be joined on it.
        Activity.Current?.SetTag("wce.correlation_id", correlationContext.CorrelationId.Value);

        await _next(context);
    }
}
