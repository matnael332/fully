using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Web.Platform;

namespace WorkContinuity.Integration.Tests;

/// <summary>
/// Exercises the real error pipeline: correlation outermost, then the exception boundary, exactly
/// as <c>Program.cs</c> composes them.
/// </summary>
public sealed class PlatformExceptionMiddlewareTests
{
    private const string SensitiveDetail = "Server=db;Password=SUPER_SECRET_VALUE_42";

    private static async Task<(HttpContext Context, string Body)> RunPipelineAsync(
        Func<HttpContext, Task> terminal)
    {
        var accessor = new CorrelationContextAccessor();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var exceptionMiddleware = new PlatformExceptionMiddleware(
            new RequestDelegate(terminal),
            accessor,
            NullLogger<PlatformExceptionMiddleware>.Instance);

        var correlationMiddleware = new CorrelationMiddleware(
            exceptionMiddleware.InvokeAsync,
            accessor);

        await correlationMiddleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        return (context, body);
    }

    [Fact]
    public async Task An_unhandled_exception_becomes_a_500_with_a_structured_body()
    {
        var (context, body) = await RunPipelineAsync(
            _ => throw new InvalidOperationException(SensitiveDetail));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType!, StringComparison.Ordinal);
        Assert.Contains("platform.unhandled_error", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_response_leaks_no_exception_detail()
    {
        var (_, body) = await RunPipelineAsync(
            _ => throw new InvalidOperationException(SensitiveDetail));

        Assert.DoesNotContain("SUPER_SECRET_VALUE_42", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(PlatformExceptionMiddleware), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_failure_response_carries_the_correlation_id_of_the_request()
    {
        var (context, body) = await RunPipelineAsync(
            _ => throw new InvalidOperationException(SensitiveDetail));

        var responseHeader = context.Response.Headers[CorrelationMiddleware.HeaderName].ToString();

        Assert.False(string.IsNullOrWhiteSpace(responseHeader));
        Assert.Contains(responseHeader, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inbound_correlation_id_survives_a_failure()
    {
        const string inbound = "caller-correlation-77";
        var accessor = new CorrelationContextAccessor();
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = inbound;
        context.Response.Body = new MemoryStream();

        var exceptionMiddleware = new PlatformExceptionMiddleware(
            _ => throw new InvalidOperationException(SensitiveDetail),
            accessor,
            NullLogger<PlatformExceptionMiddleware>.Instance);

        var correlationMiddleware = new CorrelationMiddleware(
            exceptionMiddleware.InvokeAsync,
            accessor);

        await correlationMiddleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal(inbound, context.Response.Headers[CorrelationMiddleware.HeaderName].ToString());
        Assert.Contains(inbound, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_request_is_left_untouched()
    {
        var (context, body) = await RunPipelineAsync(async httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            await httpContext.Response.WriteAsync(
                "{\"status\":\"live\"}",
                TestContext.Current.CancellationToken);
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("{\"status\":\"live\"}", body);
        Assert.False(string.IsNullOrWhiteSpace(
            context.Response.Headers[CorrelationMiddleware.HeaderName].ToString()));
    }

    [Fact]
    public void The_canonical_unhandled_error_message_names_no_internal_detail()
    {
        var error = PlatformErrors.UnhandledFailure;

        Assert.Equal("platform.unhandled_error", error.Code);
        Assert.DoesNotContain("Exception", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_wire_shape_exposes_only_a_code_a_message_and_a_correlation_id()
    {
        var propertyNames = typeof(PlatformErrorResponse)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => name != "EqualityContract")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Code", "CorrelationId", "Message"], propertyNames);
    }

    [Fact]
    public void The_middleware_requires_its_collaborators()
    {
        var accessor = new CorrelationContextAccessor();

        Assert.Throws<ArgumentNullException>(() => new PlatformExceptionMiddleware(
            null!, accessor, NullLogger<PlatformExceptionMiddleware>.Instance));

        Assert.Throws<ArgumentNullException>(() => new PlatformExceptionMiddleware(
            _ => Task.CompletedTask, null!, NullLogger<PlatformExceptionMiddleware>.Instance));

        Assert.Throws<ArgumentNullException>(() => new PlatformExceptionMiddleware(
            _ => Task.CompletedTask, accessor, null!));
    }
}
