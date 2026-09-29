using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// Translates monday's HTTP statuses, GraphQL error codes and retry signals onto the stable connector taxonomy.
/// </summary>
/// <remarks>
/// <para>
/// monday reports application errors with HTTP 200 and an <c>errors</c> array whose entries carry
/// <c>extensions.code</c> and <c>extensions.status_code</c>, and transport errors with 4xx and 5xx statuses. Only the
/// codes and messages the official error and rate-limit references document are recognized; nothing is inferred from
/// other sources. Provider messages are matched only to recognize a documented condition; they are never kept,
/// returned or logged.
/// </para>
/// <para>
/// When a response carries several errors, the most permanent one decides, so a request with a validation or
/// permission problem is never retried because another entry looked transient. An error that is not recognized is
/// decided by its status; with no telling status it is an unsupported response, never a guess.
/// </para>
/// </remarks>
internal static class MondayResponseClassifier
{
    private const string InvalidVersionCode = "InvalidVersionException";

    /// <summary>The longest wait a provider signal is trusted to state; anything above is treated as this.</summary>
    private static readonly TimeSpan LongestSignal = TimeSpan.FromDays(1);

    /// <summary>The documented codes and code-like messages, by the condition they report.</summary>
    private static readonly Dictionary<string, ConnectorFailureKind> DocumentedCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // 401
            ["Unauthorized"] = ConnectorFailureKind.AuthenticationFailed,

            // 403, and the 200-level scope failure
            ["UserUnauthorizedException"] = ConnectorFailureKind.AuthorizationFailed,
            ["USER_ACCESS_DENIED"] = ConnectorFailureKind.AuthorizationFailed,
            ["missingRequiredPermissions"] = ConnectorFailureKind.AuthorizationFailed,

            // 200, 400, 409 and 422 request problems
            ["InvalidArgumentException"] = ConnectorFailureKind.ProviderValidation,
            ["InvalidBoardIdException"] = ConnectorFailureKind.ProviderValidation,
            ["InvalidColumnIdException"] = ConnectorFailureKind.ProviderValidation,
            ["InvalidUserIdException"] = ConnectorFailureKind.ProviderValidation,
            ["ColumnValueException"] = ConnectorFailureKind.ProviderValidation,
            ["CorrectedValueException"] = ConnectorFailureKind.ProviderValidation,
            ["CreateBoardException"] = ConnectorFailureKind.ProviderValidation,
            ["ItemNameTooLongException"] = ConnectorFailureKind.ProviderValidation,
            ["ItemsLimitationException"] = ConnectorFailureKind.ProviderValidation,
            ["JsonParseException"] = ConnectorFailureKind.ProviderValidation,
            ["Bad request"] = ConnectorFailureKind.ProviderValidation,
            ["DeleteLastGroupException"] = ConnectorFailureKind.ProviderValidation,
            ["RecordInvalidException"] = ConnectorFailureKind.ProviderValidation,
            [InvalidVersionCode] = ConnectorFailureKind.ProviderValidation,

            // 200 and 404
            ["ResourceNotFoundException"] = ConnectorFailureKind.ResourceNotFound,

            // 429 and the rate-limit reference
            ["ComplexityException"] = ConnectorFailureKind.RateLimited,
            ["COMPLEXITY_BUDGET_EXHAUSTED"] = ConnectorFailureKind.RateLimited,
            ["maxConcurrencyExceeded"] = ConnectorFailureKind.RateLimited,
            ["Concurrency limit exceeded"] = ConnectorFailureKind.RateLimited,
            ["Rate Limit Exceeded"] = ConnectorFailureKind.RateLimited,
            ["IP_RATE_LIMIT_EXCEEDED"] = ConnectorFailureKind.RateLimited,
            ["DAILY_LIMIT_EXCEEDED"] = ConnectorFailureKind.RateLimited,

            // Documented as retryable
            ["API_TEMPORARILY_BLOCKED"] = ConnectorFailureKind.ProviderTransient,
            ["IDEMPOTENCY_CONFLICT"] = ConnectorFailureKind.ProviderTransient,
            ["Internal Server Error"] = ConnectorFailureKind.ProviderTransient,
        };

    /// <summary>The documented errors whose condition is carried in the message text.</summary>
    private static readonly (string Fragment, ConnectorFailureKind Kind)[] DocumentedMessages =
    [
        ("Your ip is restricted", ConnectorFailureKind.AuthorizationFailed),
        ("Parse error on", ConnectorFailureKind.ProviderValidation),
        ("Minute limit rate exceeded", ConnectorFailureKind.RateLimited),
        ("Concurrency limit exceeded", ConnectorFailureKind.RateLimited),
        ("Resource is currently locked", ConnectorFailureKind.ProviderTransient),
    ];

    /// <summary>Decides between failures: the earlier, the more permanent, and the more permanent wins.</summary>
    private static readonly ConnectorFailureKind[] Precedence =
    [
        ConnectorFailureKind.AuthenticationFailed,
        ConnectorFailureKind.AuthorizationFailed,
        ConnectorFailureKind.ProviderValidation,
        ConnectorFailureKind.ResourceNotFound,
        ConnectorFailureKind.UnsupportedResponse,
        ConnectorFailureKind.RateLimited,
        ConnectorFailureKind.ProviderTransient,
    ];

    /// <summary>
    /// Classifies a response. Returns null only for a 2xx response that reports no error, which the operation's parser
    /// then reads.
    /// </summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="root">The parsed JSON body, when it parsed.</param>
    /// <param name="headerWait">The wait the response headers asked for, when they did.</param>
    internal static ConnectorFailure? Classify(int status, JsonElement? root, TimeSpan? headerWait)
    {
        var errors = root is { ValueKind: JsonValueKind.Object } body ? ReadErrors(body) : [];
        var transportSuccess = status is >= 200 and < 300;

        if (transportSuccess && errors.Count == 0)
        {
            return null;
        }

        var decided = errors.Count == 0 ? KindOfStatus(status) : ConnectorFailureKind.Unspecified;
        var versionRejected = false;
        TimeSpan? bodyWait = null;

        foreach (var error in errors)
        {
            versionRejected |= string.Equals(error.Code, InvalidVersionCode, StringComparison.OrdinalIgnoreCase);
            decided = MorePermanent(decided, KindOf(error, status, transportSuccess));
            bodyWait = Longest(bodyWait, error.RetryAfter);
        }

        var transient = decided is ConnectorFailureKind.RateLimited or ConnectorFailureKind.ProviderTransient;
        return new ConnectorFailure(
            decided,
            ErrorFor(decided, versionRejected),
            transient ? Longest(headerWait, bodyWait) : null);
    }

    /// <summary>The wait the response headers ask for: <c>Retry-After</c>, or an exhausted <c>RateLimit</c> policy's reset.</summary>
    internal static TimeSpan? WaitFromHeaders(HttpResponseHeaders headers, DateTimeOffset now)
    {
        TimeSpan? wait = null;

        if (headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                wait = Longest(wait, Bounded(delta));
            }
            else if (retryAfter.Date is { } date)
            {
                wait = Longest(wait, Bounded(date - now));
            }
        }

        if (headers.TryGetValues("RateLimit", out var values))
        {
            foreach (var value in values)
            {
                wait = Longest(wait, ExhaustedPolicyReset(value));
            }
        }

        return wait;
    }

    private static ConnectorFailureKind KindOf(ProviderError error, int httpStatus, bool transportSuccess)
    {
        if (error.Code is not null && DocumentedCodes.TryGetValue(error.Code, out var byCode))
        {
            return byCode;
        }

        if (error.Message is not null)
        {
            foreach (var (fragment, kind) in DocumentedMessages)
            {
                if (error.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return kind;
                }
            }
        }

        var status = error.StatusCode ?? httpStatus;

        // An unrecognized error with no telling status is never guessed at.
        return transportSuccess && status is >= 200 and < 300
            ? ConnectorFailureKind.UnsupportedResponse
            : KindOfStatus(status);
    }

    private static ConnectorFailureKind KindOfStatus(int status) => status switch
    {
        401 => ConnectorFailureKind.AuthenticationFailed,
        403 => ConnectorFailureKind.AuthorizationFailed,
        404 => ConnectorFailureKind.ResourceNotFound,
        429 => ConnectorFailureKind.RateLimited,
        408 or 409 or 423 or 425 => ConnectorFailureKind.ProviderTransient,
        >= 500 and < 600 => ConnectorFailureKind.ProviderTransient,
        >= 400 and < 500 => ConnectorFailureKind.ProviderValidation,
        _ => ConnectorFailureKind.UnsupportedResponse,
    };

    private static ConnectorFailureKind MorePermanent(ConnectorFailureKind current, ConnectorFailureKind candidate) =>
        current == ConnectorFailureKind.Unspecified
        || Array.IndexOf(Precedence, candidate) < Array.IndexOf(Precedence, current)
            ? candidate
            : current;

    private static Error ErrorFor(ConnectorFailureKind kind, bool versionRejected) => kind switch
    {
        ConnectorFailureKind.AuthenticationFailed => ConnectorErrors.AuthenticationFailed,
        ConnectorFailureKind.AuthorizationFailed => ConnectorErrors.AuthorizationFailed,
        ConnectorFailureKind.ProviderValidation => versionRejected
            ? ConnectorErrors.ApiVersionRejected
            : ConnectorErrors.ProviderValidation,
        ConnectorFailureKind.ResourceNotFound => ConnectorErrors.ResourceNotFound,
        ConnectorFailureKind.RateLimited => ConnectorErrors.RateLimited,
        ConnectorFailureKind.ProviderTransient => ConnectorErrors.ProviderUnavailable,
        _ => ConnectorErrors.UnsupportedResponse,
    };

    private static List<ProviderError> ReadErrors(JsonElement body)
    {
        var errors = new List<ProviderError>();

        if (body.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new ProviderError(null, null, null, null));
                    continue;
                }

                JsonElement? extensions = entry.TryGetProperty("extensions", out var found)
                    && found.ValueKind == JsonValueKind.Object ? found : null;

                errors.Add(new ProviderError(
                    StringOf(extensions, "code") ?? StringOf(entry, "code"),
                    StringOf(entry, "message"),
                    IntOf(extensions, "status_code"),
                    Longest(SecondsOf(extensions, "retry_in_seconds"), SecondsOf(ErrorData(extensions), "retry_in_seconds"))));
            }
        }

        // The error shape of older API versions, tolerated in case a response still uses it.
        if (StringOf(body, "error_code") is { } legacyCode)
        {
            errors.Add(new ProviderError(
                legacyCode,
                StringOf(body, "error_message"),
                IntOf(body, "status_code"),
                Longest(SecondsOf(body, "retry_in_seconds"), SecondsOf(ErrorData(body), "retry_in_seconds"))));
        }

        return errors;
    }

    private static JsonElement? ErrorData(JsonElement? parent) =>
        parent is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty("error_data", out var data)
        && data.ValueKind == JsonValueKind.Object
            ? data
            : null;

    private static string? StringOf(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? IntOf(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out var number)
            ? number
            : null;

    private static TimeSpan? SecondsOf(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var seconds) => SecondsToWait(seconds),
            JsonValueKind.String when double.TryParse(
                property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) => SecondsToWait(seconds),
            _ => null,
        };
    }

    private static TimeSpan? ExhaustedPolicyReset(string header)
    {
        TimeSpan? wait = null;

        foreach (var item in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            double? remaining = null;
            double? reset = null;

            foreach (var parameter in item.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var separator = parameter.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0
                    || !double.TryParse(parameter[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    continue;
                }

                switch (parameter[..separator])
                {
                    case "r":
                        remaining = number;
                        break;
                    case "t":
                        reset = number;
                        break;
                }
            }

            if (remaining is <= 0 && reset is { } seconds)
            {
                wait = Longest(wait, SecondsToWait(seconds));
            }
        }

        return wait;
    }

    private static TimeSpan? SecondsToWait(double seconds) =>
        double.IsFinite(seconds)
            ? TimeSpan.FromSeconds(Math.Min(Math.Max(seconds, 0), LongestSignal.TotalSeconds))
            : null;

    private static TimeSpan Bounded(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero : wait > LongestSignal ? LongestSignal : wait;

    private static TimeSpan? Longest(TimeSpan? first, TimeSpan? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private sealed record ProviderError(string? Code, string? Message, int? StatusCode, TimeSpan? RetryAfter);
}
