namespace WorkContinuity.Core.Results;

/// <summary>
/// The kind of failure an operation produced. Callers map these onto transport-specific
/// responses; the categories themselves stay transport-agnostic.
/// </summary>
public enum ErrorCategory
{
    /// <summary>Unspecified. Never used by a real failure.</summary>
    None = 0,

    /// <summary>The request was structurally or semantically invalid.</summary>
    Validation = 1,

    /// <summary>The addressed resource does not exist, or is not visible to the caller.</summary>
    NotFound = 2,

    /// <summary>The operation conflicts with current state, ordering or a concurrent change.</summary>
    Conflict = 3,

    /// <summary>The caller is not authenticated.</summary>
    Unauthorized = 4,

    /// <summary>The caller is authenticated but not permitted.</summary>
    Forbidden = 5,

    /// <summary>A dependency or the platform itself failed.</summary>
    Infrastructure = 6,
}

/// <summary>
/// A safe, structured failure description.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately has no exception, stack trace, inner-error or free-form diagnostic
/// member, and there is no factory that accepts an <see cref="Exception"/>. That absence is the
/// control: code cannot accidentally pass internal detail into something that is returned to a
/// caller or written to a log. Exception detail belongs in server-side telemetry, keyed by
/// correlation id.
/// </para>
/// <para>
/// <see cref="Code"/> is a stable machine-readable token such as <c>tenant.not_resolved</c>.
/// <see cref="Message"/> is a short operator- or user-safe sentence.
/// </para>
/// </remarks>
public sealed record Error
{
    /// <summary>The maximum supported length of a message.</summary>
    public const int MaxMessageLength = 300;

    /// <summary>The maximum supported length of a code.</summary>
    public const int MaxCodeLength = 100;

    /// <summary>Creates an error.</summary>
    /// <exception cref="ArgumentException">The category, code or message is invalid.</exception>
    public Error(ErrorCategory category, string code, string message)
    {
        if (category == ErrorCategory.None)
        {
            throw new ArgumentException("An error requires a category.", nameof(category));
        }

        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength)
        {
            throw new ArgumentException(
                $"An error code is required and must be at most {MaxCodeLength} characters.",
                nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message) || message.Length > MaxMessageLength)
        {
            throw new ArgumentException(
                $"An error message is required and must be at most {MaxMessageLength} characters.",
                nameof(message));
        }

        foreach (var character in message)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    "An error message must not contain control characters.",
                    nameof(message));
            }
        }

        Category = category;
        Code = code;
        Message = message;
    }

    /// <summary>Gets the failure category.</summary>
    public ErrorCategory Category { get; }

    /// <summary>Gets the stable machine-readable code.</summary>
    public string Code { get; }

    /// <summary>Gets the safe human-readable message.</summary>
    public string Message { get; }

    /// <summary>Creates a validation error.</summary>
    public static Error Validation(string code, string message) =>
        new(ErrorCategory.Validation, code, message);

    /// <summary>Creates a not-found error.</summary>
    public static Error NotFound(string code, string message) =>
        new(ErrorCategory.NotFound, code, message);

    /// <summary>Creates a conflict error.</summary>
    public static Error Conflict(string code, string message) =>
        new(ErrorCategory.Conflict, code, message);

    /// <summary>Creates an unauthenticated error.</summary>
    public static Error Unauthorized(string code, string message) =>
        new(ErrorCategory.Unauthorized, code, message);

    /// <summary>Creates a not-permitted error.</summary>
    public static Error Forbidden(string code, string message) =>
        new(ErrorCategory.Forbidden, code, message);

    /// <summary>Creates an infrastructure error.</summary>
    public static Error Infrastructure(string code, string message) =>
        new(ErrorCategory.Infrastructure, code, message);

    public override string ToString() => $"{Category}:{Code}";
}
