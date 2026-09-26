using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Operations;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Health;
using WorkContinuity.Infrastructure.Persistence;

var postgresHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
var postgresPort = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var configuredPort)
    ? configuredPort
    : 5432;
var serviceBusHealthUrl = Environment.GetEnvironmentVariable("SERVICEBUS_HEALTH_URL")
    ?? "http://localhost:5300/health";

// The same Core primitives the Web host uses, composed by hand here to prove they carry no HTTP
// or hosting dependency.
IClock clock = new SystemClock();
ICorrelationContextAccessor correlationContextAccessor = new CorrelationContextAccessor();

var probe = new DependencyProbe();

// Each pass is its own logical operation, so it starts its own correlation scope. The tenant
// context stays unresolved: this loop is platform housekeeping and acts for no tenant.
IOperationContext StartOperation()
{
    var correlationContext = CorrelationContext.Generate();
    correlationContextAccessor.Start(correlationContext);

    return OperationContext.Start(
        TenantContext.Unresolved,
        Principal.Anonymous,
        correlationContext,
        clock);
}

// Schema migration is an explicit, separately invoked operation, never a side effect of starting the
// worker loop. It is the only path given the administrative connection, and it applies pending
// migrations without ever dropping or recreating anything.
if (args.Contains("--migrate-database", StringComparer.OrdinalIgnoreCase))
{
    var administrativeConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__PostgresAdmin");

    if (string.IsNullOrWhiteSpace(administrativeConnectionString))
    {
        Console.Error.WriteLine(
            "ConnectionStrings__PostgresAdmin is not set. Migration requires the administrative connection.");
        return 2;
    }

    var operation = StartOperation();
    var migrator = DatabaseMigrator.ForConnection(
        administrativeConnectionString,
        NullLogger<DatabaseMigrator>.Instance);

    var status = await migrator.MigrateAsync();

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        correlationId = operation.CorrelationId.Value,
        migratedAt = operation.StartedAtUtc,
        appliedMigrations = status.Applied,
        pendingMigrations = status.Pending,
    }));

    return 0;
}

if (args.Contains("--check-dependencies", StringComparer.OrdinalIgnoreCase))
{
    var operation = StartOperation();
    var report = await probe.CheckAsync(postgresHost, postgresPort, new Uri(serviceBusHealthUrl));

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        correlationId = operation.CorrelationId.Value,
        checkedAt = operation.StartedAtUtc,
        tenantResolved = operation.Tenant.IsResolved,
        readiness = report,
    }));

    return report.Ready ? 0 : 1;
}

Console.WriteLine("Work Continuity Worker — Gate 1D foundation started");

while (true)
{
    var operation = StartOperation();
    var report = await probe.CheckAsync(postgresHost, postgresPort, new Uri(serviceBusHealthUrl));

    Console.WriteLine(
        $"{operation.StartedAtUtc:O} correlation={operation.CorrelationId} " +
        $"tenant={(operation.Tenant.IsResolved ? "resolved" : "unresolved")} " +
        $"readiness={(report.Ready ? "ready" : "degraded")}");

    await Task.Delay(TimeSpan.FromSeconds(30));
}
