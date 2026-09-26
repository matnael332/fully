using System.Text.Json;
using WorkContinuity.Infrastructure.Health;

var postgresHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
var postgresPort = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var configuredPort)
    ? configuredPort
    : 5432;
var serviceBusHealthUrl = Environment.GetEnvironmentVariable("SERVICEBUS_HEALTH_URL")
    ?? "http://localhost:5300/health";

var probe = new DependencyProbe();

if (args.Contains("--check-dependencies", StringComparer.OrdinalIgnoreCase))
{
    var report = await probe.CheckAsync(postgresHost, postgresPort, new Uri(serviceBusHealthUrl));
    Console.WriteLine(JsonSerializer.Serialize(report));
    return report.Ready ? 0 : 1;
}

Console.WriteLine("Work Continuity Worker — Gate 1B foundation started");

while (true)
{
    var report = await probe.CheckAsync(postgresHost, postgresPort, new Uri(serviceBusHealthUrl));
    Console.WriteLine($"{DateTimeOffset.UtcNow:O} readiness={(report.Ready ? "ready" : "degraded")}");
    await Task.Delay(TimeSpan.FromSeconds(30));
}
