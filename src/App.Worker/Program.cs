using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Operations;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Health;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Retention;

const string ServiceName = "work-continuity-worker";
const string ActivitySourceName = "WorkContinuity.Worker";

// Configuration comes from the environment only, the same keys Bicep and Compose supply. Nothing is
// compiled in, and the Worker deliberately carries no hosting or configuration framework.
static string? Env(string name) => Environment.GetEnvironmentVariable(name);

// The same Core primitives the Web host uses, composed by hand here to prove they carry no HTTP
// or hosting dependency.
IClock clock = new SystemClock();
ICorrelationContextAccessor correlationContextAccessor = new CorrelationContextAccessor();

// Each pass is its own logical operation, so it starts its own correlation scope. The tenant
// context stays unresolved: this process is platform housekeeping and acts for no tenant.
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

// A failure is reported by type and, for a server-side database error, by the server's own message.
// Never a stack trace, never a connection string.
static object DescribeFailure(Exception exception) => new
{
    type = exception.GetType().Name,
    inner = exception.InnerException?.GetType().Name,
    message = exception is RuntimeRoleBootstrapException || exception.GetType().Name == "PostgresException"
        ? exception.Message
        : null,
};

// Schema migration is an explicit, separately invoked operation, never a side effect of starting the
// worker loop. It is the only path given the administrative connection, and it applies pending
// migrations without ever dropping or recreating anything.
if (args.Contains("--migrate-database", StringComparer.OrdinalIgnoreCase))
{
    var administrativeConnectionString = Env("ConnectionStrings__PostgresAdmin");

    if (string.IsNullOrWhiteSpace(administrativeConnectionString))
    {
        Console.Error.WriteLine(
            "ConnectionStrings__PostgresAdmin is not set. Migration requires the administrative connection.");
        return 2;
    }

    var operation = StartOperation();

    try
    {
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
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            correlationId = operation.CorrelationId.Value,
            failedAt = operation.StartedAtUtc,
            operation = "migrate-database",
            failure = DescribeFailure(exception),
        }));

        return 1;
    }
}

// Runtime-role bootstrap: creates or updates the non-owner role the applications connect as. A job
// entry point rather than a migration, because a migration is committed and replayed everywhere and
// must never contain a credential. The password arrives from Key Vault through the job's secret
// reference and is never written anywhere by this process.
if (args.Contains("--bootstrap-runtime-role", StringComparer.OrdinalIgnoreCase))
{
    var administrativeConnectionString = Env("ConnectionStrings__PostgresAdmin");
    var runtimeRolePassword = Env("Postgres__RuntimeRolePassword");
    var runtimeRoleName = Env("Postgres__RuntimeRoleName") ?? PersistenceOptions.RuntimeRoleName;

    if (string.IsNullOrWhiteSpace(administrativeConnectionString))
    {
        Console.Error.WriteLine(
            "ConnectionStrings__PostgresAdmin is not set. Bootstrap requires the administrative connection.");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(runtimeRolePassword))
    {
        Console.Error.WriteLine(
            "Postgres__RuntimeRolePassword is not set. Bootstrap requires the runtime role password.");
        return 2;
    }

    var operation = StartOperation();

    try
    {
        var result = await RuntimeRoleBootstrap.ApplyAsync(
            administrativeConnectionString,
            runtimeRoleName,
            runtimeRolePassword,
            CancellationToken.None);

        var succeeded = result.CanLogin && result.RowLevelSecurityEnforced && result.LoginVerified;

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            correlationId = operation.CorrelationId.Value,
            bootstrappedAt = operation.StartedAtUtc,
            role = result.RoleName,
            created = result.Created,
            canLogin = result.CanLogin,
            rowLevelSecurityEnforced = result.RowLevelSecurityEnforced,
            tablesGranted = result.TablesGranted,
            loginVerified = result.LoginVerified,
            succeeded,
        }));

        return succeeded ? 0 : 1;
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            correlationId = operation.CorrelationId.Value,
            failedAt = operation.StartedAtUtc,
            operation = "bootstrap-runtime-role",
            failure = DescribeFailure(exception),
        }));

        return 1;
    }
}

// Real dependencies, checked under this process's own credentials: the runtime-role connection
// string and, for Service Bus, the Data Receiver claim of the Worker's identity (peek only).
var probeOptions = new DependencyProbeOptions
{
    PostgresConnectionString = Env("ConnectionStrings__Postgres") ?? string.Empty,
    ServiceBusFullyQualifiedNamespace = Env("ServiceBus__FullyQualifiedNamespace") ?? string.Empty,
    ServiceBusConnectionString = Env("ServiceBus__ConnectionString") ?? string.Empty,
    ServiceBusQueueName = Env("ServiceBus__ReadinessQueue") ?? DependencyProbeOptions.DefaultQueueName,
    ServiceBusMode = ServiceBusReadinessMode.Receive,
    ManagedIdentityClientId = Env(DependencyProbeOptions.ManagedIdentityClientIdVariable) ?? string.Empty,
};

await using var probe = new DependencyProbe(probeOptions);

if (args.Contains("--check-dependencies", StringComparer.OrdinalIgnoreCase))
{
    var operation = StartOperation();
    var report = await probe.CheckAsync();

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        correlationId = operation.CorrelationId.Value,
        checkedAt = operation.StartedAtUtc,
        tenantResolved = operation.Tenant.IsResolved,
        readiness = report,
    }));

    return report.Ready ? 0 : 1;
}

// OpenTelemetry baseline (Gate 1G). Exported to Application Insights only when the connection string
// is present, which Azure supplies from Key Vault; locally and in CI smoke tests nothing is exported.
// Console logging stays on in every case, so container logs keep flowing to Log Analytics.
var telemetryConnectionString = Env("APPLICATIONINSIGHTS_CONNECTION_STRING");
var exportTelemetry = !string.IsNullOrWhiteSpace(telemetryConnectionString);
var resource = ResourceBuilder.CreateDefault().AddService(ServiceName);

using var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.UseUtcTimestamp = true;
        console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    });

    if (exportTelemetry)
    {
        logging.AddOpenTelemetry(telemetry =>
        {
            telemetry.SetResourceBuilder(resource);
            telemetry.AddAzureMonitorLogExporter(exporter => exporter.ConnectionString = telemetryConnectionString);
        });
    }
});

using var tracerProvider = exportTelemetry
    ? Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(resource)
        .AddSource(ActivitySourceName)
        .AddSource("Npgsql")
        .AddSource("Azure.*")
        .AddAzureMonitorTraceExporter(exporter => exporter.ConnectionString = telemetryConnectionString)
        .Build()
    : null;

// The Event Backbone's metrics (Gate 2F) join the same exporter. They carry bounded tags only, never an id.
using var meterProvider = exportTelemetry
    ? Sdk.CreateMeterProviderBuilder()
        .SetResourceBuilder(resource)
        .AddMeter(EventBackboneMetrics.MeterName)
        .AddAzureMonitorMetricExporter(exporter => exporter.ConnectionString = telemetryConnectionString)
        .Build()
    : null;

var logger = loggerFactory.CreateLogger("WorkContinuity.Worker");
using var activitySource = new ActivitySource(ActivitySourceName);

// SIGTERM is what Container Apps sends when a revision is replaced; a clean stop flushes telemetry.
using var shutdown = new CancellationTokenSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    shutdown.Cancel();
});
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

// Raw-content retention (Gate 2F): one bounded, idempotent purge of the raw content that is due (28 days old by
// the database's clock, two days before the 30-day limit), then exit. This process's clock only times the run and
// stamps the summary; it decides nothing. It runs only when invoked explicitly; nothing in this process schedules
// it, and it is safe to rerun, to retry after a failure and to run alongside another run. It connects as the
// runtime role, so each tenant's rows are purged under that tenant's own row-level security. The summary carries
// counts, ages by the database's clock and statuses only. Exit codes: 0 done, nothing overdue left; 1 a step
// failed; 2 not configured; 3 overdue or limit-exceeded raw content still remains after the run.
if (args.Contains("--purge-raw-content", StringComparer.OrdinalIgnoreCase))
{
    var runtimeConnectionString = Env("ConnectionStrings__Postgres");

    if (string.IsNullOrWhiteSpace(runtimeConnectionString))
    {
        Console.Error.WriteLine(
            "ConnectionStrings__Postgres is not set. The purge requires the runtime-role connection.");
        return 2;
    }

    var operation = StartOperation();
    using var metrics = new EventBackboneMetrics();

    try
    {
        var run = await RawContentPurgeEntryPoint.RunOnceAsync(
            runtimeConnectionString,
            loggerFactory,
            metrics,
            cancellationToken: shutdown.Token);
        meterProvider?.ForceFlush(10_000);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            correlationId = operation.CorrelationId.Value,
            purgedAt = operation.StartedAtUtc,
            outcome = run.Outcome.ToString(),
            tenantsVisited = run.TenantsVisited,
            tenantFailures = run.TenantFailures,
            discoveryFailed = run.DiscoveryFailed,
            receiptsPurged = run.ReceiptsPurged,
            processedPurged = run.ProcessedPurged,
            quarantinedPurged = run.QuarantinedPurged,
            unprocessedPurged = run.UnprocessedPurged,
            dueContentRemaining = run.DueContentRemaining,
            oldestPurgedAgeSeconds = run.OldestPurgedAge?.TotalSeconds,
            oldestRemainingAgeSeconds = run.OldestRemainingAge?.TotalSeconds,
            purgedContentStatus = run.PurgedStatus.ToString(),
            remainingContentStatus = run.RemainingStatus.ToString(),
            durationMilliseconds = (long)run.Duration.TotalMilliseconds,
        }));

        // A partial run within the limit is not a failure, and neither is a run that cleared overdue content: only
        // overdue content still left behind needs attention.
        return RawContentPurgeEntryPoint.ExitCodeFor(run);
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        meterProvider?.ForceFlush(10_000);
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            correlationId = operation.CorrelationId.Value,
            failedAt = operation.StartedAtUtc,
            operation = "purge-raw-content",
            failure = DescribeFailure(exception),
        }));

        return 1;
    }
}

logger.LogInformation(
    "Work Continuity Worker started; environment={Environment} telemetry={Telemetry}",
    Env("WORKCONTINUITY_ENVIRONMENT") ?? "local",
    exportTelemetry ? "application-insights" : "console-only");

while (!shutdown.IsCancellationRequested)
{
    var operation = StartOperation();

    using (var activity = activitySource.StartActivity("worker.readiness-check", ActivityKind.Internal))
    {
        activity?.SetTag("wce.correlation_id", operation.CorrelationId.Value);

        var report = await probe.CheckAsync(shutdown.Token);

        activity?.SetTag("wce.readiness", report.Ready ? "ready" : "degraded");
        foreach (var dependency in report.Dependencies)
        {
            activity?.SetTag($"wce.dependency.{dependency.Name}", dependency.Healthy ? "healthy" : dependency.Detail ?? "unhealthy");
        }

        logger.LogInformation(
            "correlation={CorrelationId} tenant={Tenant} readiness={Readiness} dependencies={Dependencies}",
            operation.CorrelationId.Value,
            operation.Tenant.IsResolved ? "resolved" : "unresolved",
            report.Ready ? "ready" : "degraded",
            string.Join(
                ",",
                report.Dependencies.Select(dependency =>
                    $"{dependency.Name}={(dependency.Healthy ? "healthy" : dependency.Detail ?? "unhealthy")}")));
    }

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(30), shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        // Shutdown requested; fall through to the exit path.
    }
}

logger.LogInformation("Work Continuity Worker stopping");
return 0;
