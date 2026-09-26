using WorkContinuity.Infrastructure.Health;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DependencyProbe>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "work-continuity-web",
    gate = "1B",
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
