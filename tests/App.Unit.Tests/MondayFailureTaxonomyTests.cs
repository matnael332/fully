using System.Net;
using System.Net.Http.Headers;
using WorkContinuity.Core.Connectors;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The Gate 3A translation of monday's documented errors onto the connector taxonomy, and the provider-aware retry:
/// permanent failures once, transient ones up to the attempt limit, never sooner than monday asks, and never sleeping
/// through a wait longer than the cap.
/// </summary>
public sealed class MondayFailureTaxonomyTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly int RetriedToTheLimit = ConnectorRetryPolicy.DefaultRead.MaxAttempts;

    public static TheoryData<string, int, string, ConnectorFailureKind, string, int> DocumentedErrors => new()
    {
        { "401 without a body", 401, string.Empty, ConnectorFailureKind.AuthenticationFailed, "connector.provider.authentication_failed", 1 },
        { "401 Unauthorized", 401, MondayJson.Error("Unauthorized", statusCode: 401), ConnectorFailureKind.AuthenticationFailed, "connector.provider.authentication_failed", 1 },
        { "401 ip restricted", 401, """{"errors":[{"message":"Your ip is restricted"}]}""", ConnectorFailureKind.AuthorizationFailed, "connector.provider.authorization_failed", 1 },
        { "403 user access denied", 403, MondayJson.Error("USER_ACCESS_DENIED", statusCode: 403), ConnectorFailureKind.AuthorizationFailed, "connector.provider.authorization_failed", 1 },
        { "403 user unauthorized", 403, MondayJson.Error("UserUnauthorizedException", statusCode: 403), ConnectorFailureKind.AuthorizationFailed, "connector.provider.authorization_failed", 1 },
        { "200 missing scope", 200, MondayJson.Error("missingRequiredPermissions"), ConnectorFailureKind.AuthorizationFailed, "connector.provider.authorization_failed", 1 },
        { "400 JSON parse", 400, MondayJson.Error("JsonParseException", statusCode: 400), ConnectorFailureKind.ProviderValidation, "connector.provider.request_rejected", 1 },
        { "200 invalid argument", 200, MondayJson.Error("InvalidArgumentException"), ConnectorFailureKind.ProviderValidation, "connector.provider.request_rejected", 1 },
        { "200 parse error message", 200, """{"errors":[{"message":"Parse error on \"}\" (RCURLY) at [1, 2]"}]}""", ConnectorFailureKind.ProviderValidation, "connector.provider.request_rejected", 1 },
        { "200 invalid version", 200, MondayJson.Error("InvalidVersionException"), ConnectorFailureKind.ProviderValidation, "connector.provider.api_version_rejected", 1 },
        { "404 not found", 404, MondayJson.Error("ResourceNotFoundException", statusCode: 404), ConnectorFailureKind.ResourceNotFound, "connector.provider.resource_not_found", 1 },
        { "200 not found", 200, MondayJson.Error("ResourceNotFoundException"), ConnectorFailureKind.ResourceNotFound, "connector.provider.resource_not_found", 1 },
        { "429 concurrency", 429, MondayJson.Error("maxConcurrencyExceeded", statusCode: 429), ConnectorFailureKind.RateLimited, "connector.provider.rate_limited", RetriedToTheLimit },
        { "429 complexity budget", 429, MondayJson.Error("COMPLEXITY_BUDGET_EXHAUSTED", statusCode: 429), ConnectorFailureKind.RateLimited, "connector.provider.rate_limited", RetriedToTheLimit },
        { "429 ip limit", 429, MondayJson.Error("IP_RATE_LIMIT_EXCEEDED", statusCode: 429), ConnectorFailureKind.RateLimited, "connector.provider.rate_limited", RetriedToTheLimit },
        { "429 without a body", 429, string.Empty, ConnectorFailureKind.RateLimited, "connector.provider.rate_limited", RetriedToTheLimit },
        { "200 complexity", 200, MondayJson.Error("ComplexityException"), ConnectorFailureKind.RateLimited, "connector.provider.rate_limited", RetriedToTheLimit },
        { "200 minute limit message", 200, """{"errors":[{"message":"Minute limit rate exceeded"}]}""", ConnectorFailureKind.RateLimited, "connector.provider.rate_limited", RetriedToTheLimit },
        { "200 temporarily blocked", 200, MondayJson.Error("API_TEMPORARILY_BLOCKED"), ConnectorFailureKind.ProviderTransient, "connector.provider.unavailable", RetriedToTheLimit },
        { "423 locked", 423, """{"errors":[{"message":"Resource is currently locked, please try again later"}]}""", ConnectorFailureKind.ProviderTransient, "connector.provider.unavailable", RetriedToTheLimit },
        { "409 idempotency conflict", 409, MondayJson.Error("IDEMPOTENCY_CONFLICT", statusCode: 409), ConnectorFailureKind.ProviderTransient, "connector.provider.unavailable", RetriedToTheLimit },
        { "500 internal error", 500, MondayJson.Error("Internal Server Error", statusCode: 500), ConnectorFailureKind.ProviderTransient, "connector.provider.unavailable", RetriedToTheLimit },
        { "502 HTML page", 502, "<html>bad gateway</html>", ConnectorFailureKind.ProviderTransient, "connector.provider.unavailable", RetriedToTheLimit },
        { "503 without a body", 503, string.Empty, ConnectorFailureKind.ProviderTransient, "connector.provider.unavailable", RetriedToTheLimit },
        { "200 unknown code", 200, MondayJson.Error("SomethingNobodyDocumented"), ConnectorFailureKind.UnsupportedResponse, "connector.provider.unsupported_response", 1 },
        { "200 unknown code with status 429", 200, MondayJson.Error("SomethingNobodyDocumented", statusCode: 429), ConnectorFailureKind.RateLimited, "connector.provider.rate_limited", RetriedToTheLimit },
        { "418 unknown client error", 418, string.Empty, ConnectorFailureKind.ProviderValidation, "connector.provider.request_rejected", 1 },
        { "302 redirect", 302, string.Empty, ConnectorFailureKind.UnsupportedResponse, "connector.provider.unsupported_response", 1 },
        { "legacy error shape", 200, """{"error_code":"ComplexityException","status_code":429,"error_message":"Complexity budget exhausted"}""", ConnectorFailureKind.RateLimited, "connector.provider.rate_limited", RetriedToTheLimit },
    };

    [Theory]
    [MemberData(nameof(DocumentedErrors))]
    public async Task Each_documented_error_maps_to_its_stable_kind_and_is_retried_only_when_transient(
        string label,
        int status,
        string body,
        ConnectorFailureKind kind,
        string code,
        int attempts)
    {
        var harness = new MondayHarness();
        harness.Server.Always((HttpStatusCode)status, body);

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.False(result.IsSuccess, label);
        Assert.Equal(kind, result.Failure.Kind);
        Assert.Equal(code, result.Failure.Error.Code);
        Assert.Equal(attempts, result.Attempts);
        Assert.Equal(attempts, harness.Server.Requests.Count);
        Assert.Equal(attempts - 1, harness.Delay.Waits.Count);
    }

    [Theory]
    [InlineData("ComplexityException", "InvalidArgumentException", ConnectorFailureKind.ProviderValidation)]
    [InlineData("InvalidArgumentException", "ComplexityException", ConnectorFailureKind.ProviderValidation)]
    [InlineData("API_TEMPORARILY_BLOCKED", "ComplexityException", ConnectorFailureKind.RateLimited)]
    [InlineData("ComplexityException", "API_TEMPORARILY_BLOCKED", ConnectorFailureKind.RateLimited)]
    [InlineData("InvalidArgumentException", "USER_ACCESS_DENIED", ConnectorFailureKind.AuthorizationFailed)]
    [InlineData("USER_ACCESS_DENIED", "InvalidArgumentException", ConnectorFailureKind.AuthorizationFailed)]
    public async Task When_errors_disagree_the_more_permanent_one_decides_in_either_order(
        string first,
        string second,
        ConnectorFailureKind expected)
    {
        var harness = new MondayHarness();
        harness.Server.Always(HttpStatusCode.OK,
            "{\"errors\":[{\"message\":\"x\",\"extensions\":{\"code\":\"" + first
            + "\"}},{\"message\":\"y\",\"extensions\":{\"code\":\"" + second + "\"}}]}");

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(expected, result.Failure.Kind);
        Assert.Equal(expected == ConnectorFailureKind.RateLimited ? RetriedToTheLimit : 1, result.Attempts);
    }

    [Fact]
    public async Task Partial_data_with_an_error_is_a_failure_not_a_partial_success()
    {
        var harness = new MondayHarness();
        harness.Server.Always(HttpStatusCode.OK,
            """{"data":{"items":[]},"errors":[{"message":"x","extensions":{"code":"InvalidArgumentException"}}]}""");

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorFailureKind.ProviderValidation, result.Failure.Kind);
    }

    [Fact]
    public async Task Retry_After_in_seconds_is_honoured()
    {
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.TooManyRequests, MondayJson.Error("Rate Limit Exceeded", statusCode: 429),
                response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)))
            .Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, harness.Delay.Waits);
    }

    [Fact]
    public async Task Retry_After_as_a_date_is_measured_against_the_clock()
    {
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.TooManyRequests, string.Empty,
                response => response.Headers.RetryAfter = new RetryConditionHeaderValue(MondayHarness.Now.AddSeconds(20)))
            .Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { TimeSpan.FromSeconds(20) }, harness.Delay.Waits);
    }

    [Theory]
    [InlineData(""","retry_in_seconds":12""")]
    [InlineData(""","error_data":{"retry_in_seconds":12}""")]
    public async Task The_retry_in_seconds_signal_is_honoured_wherever_it_appears(string extra)
    {
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.OK, MondayJson.Error("ComplexityException", extra: extra))
            .Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { TimeSpan.FromSeconds(12) }, harness.Delay.Waits);
    }

    [Fact]
    public async Task An_exhausted_RateLimit_policy_reset_is_honoured_and_one_with_quota_left_is_not()
    {
        var exhausted = new MondayHarness();
        exhausted.Server
            .Then(HttpStatusCode.TooManyRequests, string.Empty,
                response => response.Headers.TryAddWithoutValidation("RateLimit", "\"minuteRate\";r=0;t=38, \"concurrency\";r=249, \"complexityMinute\";r=4950000;t=45"))
            .Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));
        var available = new MondayHarness();
        available.Server
            .Then(HttpStatusCode.ServiceUnavailable, string.Empty,
                response => response.Headers.TryAddWithoutValidation("RateLimit", "\"minuteRate\";r=90, \"concurrency\";r=35, \"complexityMinute\";r=800000;t=45"))
            .Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        await exhausted.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);
        await available.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(new[] { TimeSpan.FromSeconds(38) }, exhausted.Delay.Waits);
        Assert.Equal(new[] { ConnectorRetryPolicy.DefaultRead.BackoffAfter(1, 0.5) }, available.Delay.Waits);
    }

    [Fact]
    public async Task The_longest_provider_signal_wins()
    {
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.OK, MondayJson.Error("ComplexityException", extra: ",\"retry_in_seconds\":11"),
                response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)))
            .Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(new[] { TimeSpan.FromSeconds(11) }, harness.Delay.Waits);
    }

    [Fact]
    public async Task A_wait_beyond_the_cap_is_handed_back_to_the_caller_instead_of_slept()
    {
        var harness = new MondayHarness();
        harness.Server.Always(HttpStatusCode.OK, MondayJson.Error("DAILY_LIMIT_EXCEEDED", extra: ",\"retry_in_seconds\":3600"));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorFailureKind.RateLimited, result.Failure.Kind);
        Assert.Equal(TimeSpan.FromHours(1), result.Failure.RetryAfter);
        Assert.Equal(1, result.Attempts);
        Assert.Empty(harness.Delay.Waits);
    }

    [Fact]
    public async Task Without_a_signal_backoff_grows_and_stops_at_the_attempt_limit()
    {
        var harness = new MondayHarness();
        harness.Server.Always(HttpStatusCode.ServiceUnavailable, string.Empty);

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        var policy = ConnectorRetryPolicy.DefaultRead;
        Assert.Equal(policy.MaxAttempts, result.Attempts);
        Assert.Equal(
            Enumerable.Range(1, policy.MaxAttempts - 1).Select(attempt => policy.BackoffAfter(attempt, 0.5)),
            harness.Delay.Waits);
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3) },
            harness.Delay.Waits);
        Assert.Null(result.Failure.RetryAfter);
    }

    [Fact]
    public async Task A_permanent_failure_carries_no_wait_even_when_the_provider_sent_one()
    {
        var harness = new MondayHarness();
        harness.Server.Always(HttpStatusCode.Unauthorized, string.Empty,
            response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorFailureKind.AuthenticationFailed, result.Failure.Kind);
        Assert.Null(result.Failure.RetryAfter);
        Assert.Empty(harness.Delay.Waits);
    }
}
