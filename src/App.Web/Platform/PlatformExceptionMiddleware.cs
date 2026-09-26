using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using WorkContinuity.Core.Correlation;

namespace WorkContinuity.Web.Platform;

/// <summary>
/// Converts any unhandled exception into a structured, safe JSON response.
/// </summary>
/// <remarks>
/// Exception detail is written to server-side logs only. The response body is built from the fixed
/// <see cref="PlatformErrors.UnhandledFailure"/> plus the correlation id, so exception types,
/// messages, stack traces and connection strings never reach the caller.
/// </remarks>
public sealed class PlatformExceptionMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ICorrelationContextAccessor _correlationContextAccessor;
    private readonly ILogger<PlatformExceptionMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    public PlatformExceptionMiddleware(
        RequestDelegate next,
        ICorrelationContextAccessor correlationContextAccessor,
        ILogger<PlatformExceptionMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(correlationContextAccessor);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _correlationContextAccessor = correlationContextAccessor;
        _logger = logger;
    }

    /// <summary>Runs the middleware.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This is the outermost application boundary. Any escaping exception must " +
            "become a safe response rather than leak detail or an unstyled server error page.")]
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            var correlationId = _correlationContextAccessor.Current?.CorrelationId.ToString()
                ?? string.Empty;

            _logger.LogError(
                exception,
                "Unhandled platform failure for {Method} {Path}. CorrelationId={CorrelationId}",
                context.Request.Method,
                context.Request.Path,
                correlationId);

            if (context.Response.HasStarted)
            {
                // The caller is already receiving a body; there is nothing safe left to rewrite.
                throw;
            }

            // The correlation header set upstream is deliberately preserved; only the status,
            // content type and length are reset so a partially prepared body cannot bleed through.
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength = null;

            var payload = PlatformErrorResponse.From(PlatformErrors.UnhandledFailure, correlationId);

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(payload, SerializerOptions));
        }
    }
}
