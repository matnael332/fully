using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Features;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Health;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Web.Platform;

var builder = WebApplication.CreateBuilder(args);

// Core platform primitives. The accessors are flow-scoped internally, so a singleton
// registration still yields per-request isolation.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddSingleton<IFeatureFlagProvider>(_ => new InMemoryFeatureFlagProvider());

// Readiness proves the real dependencies under this process's own credentials: the runtime-role
// database connection and, for Service Bus, the Data Sender claim of the Web identity (a send link
// is opened; no message is sent).
builder.Services.AddDependencyProbe(builder.Configuration, ServiceBusReadinessMode.Send);

// Tenant-scoped persistence only. The administrative connection used for migrations is deliberately
// not registered here, so request handling cannot resolve an elevated database connection.
builder.Services.AddPersistence(builder.Configuration);

// OpenTelemetry baseline (Gate 1G): requests, dependencies (HTTP, PostgreSQL, Azure SDK), logs and
// runtime metrics to Application Insights. Enabled only when Azure supplies the connection string
// from Key Vault; locally and in CI smoke tests the endpoints run without any exporter. Nothing here
// captures request or response bodies, so no work content or credential can reach telemetry. The
// Event Backbone's metrics (Gate 2F), which carry bounded tags only, join the same pipeline.
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor()
        .ConfigureResource(resource => resource.AddService("work-continuity-web"))
        .WithTracing(tracing => tracing.AddSource("Npgsql"))
        .WithMetrics(metrics => metrics.AddMeter(EventBackboneMetrics.MeterName));
}

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
    gate = "1G",
    status = "foundation"
}));

// Liveness never touches a dependency: a database outage must not restart the process.
app.MapGet("/health/live", () => Results.Ok(new
{
    status = "live",
    service = "work-continuity-web"
}));

app.MapGet("/health/ready", async (DependencyProbe probe, CancellationToken cancellationToken) =>
{
    var report = await probe.CheckAsync(cancellationToken);

    return report.Ready
        ? Results.Ok(report)
        : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();
