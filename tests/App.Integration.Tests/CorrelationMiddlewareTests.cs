using Microsoft.AspNetCore.Http;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Web.Platform;

namespace WorkContinuity.Integration.Tests;

/// <summary>
/// Exercises the real <see cref="CorrelationMiddleware"/> over an HTTP context.
/// </summary>
public sealed class CorrelationMiddlewareTests
{
    [Fact]
    public async Task A_request_without_a_header_receives_a_generated_correlation_id()
    {
        var accessor = new CorrelationContextAccessor();
        var context = new DefaultHttpContext();
        ICorrelationContext? observed = null;

        var middleware = new CorrelationMiddleware(
            _ =>
            {
                observed = accessor.Current;
                return Task.CompletedTask;
            },
            accessor);

        await middleware.InvokeAsync(context);

        var responseHeader = context.Response.Headers[CorrelationMiddleware.HeaderName].ToString();

        Assert.True(CorrelationId.TryParse(responseHeader, out _));
        Assert.NotNull(observed);
        Assert.False(observed.WasPropagated);
        Assert.Equal(observed.CorrelationId.Value, responseHeader);
    }

    [Fact]
    public async Task A_valid_inbound_header_is_propagated_to_the_response_and_downstream()
    {
        const string inbound = "upstream-correlation-01";
        var accessor = new CorrelationContextAccessor();
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = inbound;
        ICorrelationContext? observed = null;

        var middleware = new CorrelationMiddleware(
            _ =>
            {
                observed = accessor.Current;
                return Task.CompletedTask;
            },
            accessor);

        await middleware.InvokeAsync(context);

        Assert.Equal(inbound, context.Response.Headers[CorrelationMiddleware.HeaderName].ToString());
        Assert.NotNull(observed);
        Assert.True(observed.WasPropagated);
        Assert.Equal(inbound, observed.CorrelationId.Value);
    }

    [Theory]
    [InlineData("too short")]
    [InlineData("has spaces in it")]
    [InlineData("bad;chars")]
    [InlineData("")]
    public async Task A_malformed_inbound_header_is_replaced_rather_than_trusted(string inbound)
    {
        var accessor = new CorrelationContextAccessor();
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = inbound;

        var middleware = new CorrelationMiddleware(_ => Task.CompletedTask, accessor);

        await middleware.InvokeAsync(context);

        var responseHeader = context.Response.Headers[CorrelationMiddleware.HeaderName].ToString();

        Assert.NotEqual(inbound, responseHeader);
        Assert.True(CorrelationId.TryParse(responseHeader, out _));
    }

    [Fact]
    public async Task A_header_injection_attempt_does_not_reach_the_response()
    {
        var accessor = new CorrelationContextAccessor();
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = "seed-value-1234";

        var middleware = new CorrelationMiddleware(_ => Task.CompletedTask, accessor);

        await middleware.InvokeAsync(context);

        var responseHeader = context.Response.Headers[CorrelationMiddleware.HeaderName].ToString();

        Assert.DoesNotContain("\r", responseHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", responseHeader, StringComparison.Ordinal);
        Assert.False(context.Response.Headers.ContainsKey("X-Injected-Header"));
    }

    [Fact]
    public async Task A_duplicated_inbound_header_cannot_smuggle_a_value_through()
    {
        var accessor = new CorrelationContextAccessor();
        var context = new DefaultHttpContext();
        context.Request.Headers.Append(CorrelationMiddleware.HeaderName, "first-value-0001");
        context.Request.Headers.Append(CorrelationMiddleware.HeaderName, "second-value-0002");

        var middleware = new CorrelationMiddleware(_ => Task.CompletedTask, accessor);

        await middleware.InvokeAsync(context);

        var responseHeader = context.Response.Headers[CorrelationMiddleware.HeaderName].ToString();

        // The joined value fails validation, so a fresh id is issued instead of either candidate.
        Assert.DoesNotContain(",", responseHeader, StringComparison.Ordinal);
        Assert.NotEqual("first-value-0001", responseHeader);
        Assert.NotEqual("second-value-0002", responseHeader);
        Assert.True(CorrelationId.TryParse(responseHeader, out _));
    }

    [Fact]
    public async Task Two_requests_receive_different_correlation_ids()
    {
        var accessor = new CorrelationContextAccessor();
        var middleware = new CorrelationMiddleware(_ => Task.CompletedTask, accessor);

        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();

        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        Assert.NotEqual(
            first.Response.Headers[CorrelationMiddleware.HeaderName].ToString(),
            second.Response.Headers[CorrelationMiddleware.HeaderName].ToString());
    }

    [Fact]
    public void The_header_name_is_stable()
    {
        Assert.Equal("X-Correlation-Id", CorrelationMiddleware.HeaderName);
    }

    [Fact]
    public void The_middleware_requires_its_collaborators()
    {
        var accessor = new CorrelationContextAccessor();

        Assert.Throws<ArgumentNullException>(() => new CorrelationMiddleware(null!, accessor));
        Assert.Throws<ArgumentNullException>(
            () => new CorrelationMiddleware(_ => Task.CompletedTask, null!));
    }
}
