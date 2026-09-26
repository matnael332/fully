using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Features;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Health;
using WorkContinuity.Web.Platform;

var builder = WebApplication.CreateBuilder(args);

// Core platform primitives. The accessors are flow-scoped internally, so a singleton
// registration still yields per-request isolation.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddSingleton<IFeatureFlagProvider>(_ => new InMemoryFeatureFlagProvider());
builder.Services.AddSingleton<DependencyProbe>();

var app = builder.Build();

// Correlation runs outermost so the value it assigns flows into every inner frame, including the
// exception middleware, and so the response header is set even when a request fails.
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<PlatformExceptionMiddleware>();

// No middleware resolves a tenant. ITenantContextAccessor.Current therefore stays
// TenantContext.Unresolved for every request: a tenant id supplied in a query string, header,
// route or body is never promoted into the security context. Server-side tenant resolution from a
// validated authenticated principal arrives with authentication in a later Gate.

app.MapGet("/", () => Results.Ok(new
{
    service = "work-continuity-web",
    gate = "1C",
    status = "foundation"
}));

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "live",
    service = "work-continuity-web"
}));

app.MapGet("/health/ready", async (
    IConfiguration configuration,
    DependencyProbe probe,
    CancellationToken cancellationToken) =>
{
    var postgresHost = configuration["Dependencies:Postgres:Host"] ?? "localhost";
    var postgresPort = int.TryParse(configuration["Dependencies:Postgres:Port"], out var configuredPort)
        ? configuredPort
        : 5432;
    var serviceBusHealthUrl = configuration["Dependencies:ServiceBusEmulator:HealthUrl"]
        ?? "http://localhost:5300/health";

    var report = await probe.CheckAsync(
        postgresHost,
        postgresPort,
        new Uri(serviceBusHealthUrl),
        cancellationToken);

    return report.Ready
        ? Results.Ok(report)
        : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();
