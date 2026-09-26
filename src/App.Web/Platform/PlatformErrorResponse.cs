using WorkContinuity.Core.Results;

namespace WorkContinuity.Web.Platform;

/// <summary>
/// Canonical platform-level errors returned to callers.
/// </summary>
public static class PlatformErrors
{
    /// <summary>
    /// The single response used for any unhandled failure. The text is fixed, so no detail from
    /// the underlying exception can reach a caller.
    /// </summary>
    public static Error UnhandledFailure { get; } = Error.Infrastructure(
        "platform.unhandled_error",
        "An unexpected error occurred. Quote the correlation id when reporting this.");
}

/// <summary>
/// The wire shape for a failed request.
/// </summary>
/// <remarks>
/// Only a stable code, a safe message and the correlation id are exposed. There is no member for
/// an exception, a stack trace or inner detail, so an operator correlates a caller-visible failure
/// with server-side telemetry using the correlation id rather than by being handed internals.
/// </remarks>
public sealed record PlatformErrorResponse
{
    private PlatformErrorResponse(string code, string message, string correlationId)
    {
        Code = code;
        Message = message;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the stable machine-readable error code.</summary>
    public string Code { get; }

    /// <summary>Gets the safe human-readable message.</summary>
    public string Message { get; }

    /// <summary>Gets the correlation id for this request.</summary>
    public string CorrelationId { get; }

    /// <summary>Projects an <see cref="Error"/> onto the wire shape.</summary>
    public static PlatformErrorResponse From(Error error, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new PlatformErrorResponse(error.Code, error.Message, correlationId ?? string.Empty);
    }
}
