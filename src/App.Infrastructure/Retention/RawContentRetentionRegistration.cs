using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Retention;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Observability;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Infrastructure.Retention;

/// <summary>
/// Limits for one raw-content purge run. The retention ages are not among them: the purge age, the overdue age and
/// the 30-day limit are fixed in <see cref="RawContentRetention"/>, so nothing can be configured to purge early or
/// keep content longer.
/// </summary>
public sealed class RawContentPurgeOptions
{
    /// <summary>
    /// The most tenants one run visits, oldest due content first. The default is the discovery function's own
    /// cap.
    /// </summary>
    public int TenantsPerRun { get; set; } = 100;

    /// <summary>The most receipts one purge transaction clears.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>The most batches one tenant gets per run, so every tenant found is served in each run.</summary>
    public int MaxBatchesPerTenant { get; set; } = 20;

    /// <summary>Checks every value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(TenantsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TenantsPerRun, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BatchSize, 5000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchesPerTenant, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchesPerTenant, 1000);
    }
}

/// <summary>Composes raw-content retention.</summary>
public static class RawContentRetentionRegistration
{
    /// <summary>Registers the raw-content purge.</summary>
    /// <remarks>
    /// Requires <c>AddPersistence</c> and the flow-scoped singleton <c>ITenantContextAccessor</c> the hosts register.
    /// The purge reads no application clock: the database's clock decides what is due and how late it is. Nothing
    /// schedules the purge: a host runs it once per explicit invocation.
    /// </remarks>
    public static IServiceCollection AddRawContentRetention(
        this IServiceCollection services,
        Action<RawContentPurgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RawContentPurgeOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton<EventBackboneMetrics>();
        services.AddScoped<RetentionTenantDiscovery>();
        services.AddSingleton<IRawContentPurge, RawContentPurge>();
        return services;
    }
}

/// <summary>Runs one raw-content purge for a host that composes no dependency-injection container of its own.</summary>
public static class RawContentPurgeEntryPoint
{
    /// <summary>
    /// Composes persistence bound to the runtime role, a flow-scoped tenant accessor and the purge, and runs one
    /// bounded purge. The caller supplies the logging and the metrics, so its own telemetry pipeline sees them, and no
    /// clock: the database's clock alone decides what is due.
    /// </summary>
    /// <exception cref="ArgumentException">The runtime connection string is missing.</exception>
    public static async Task<RawContentPurgeRun> RunOnceAsync(
        string runtimeConnectionString,
        ILoggerFactory loggerFactory,
        EventBackboneMetrics metrics,
        Action<RawContentPurgeOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeConnectionString);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(metrics);

        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(metrics);
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.AddPersistence(new PersistenceOptions { ApplicationConnectionString = runtimeConnectionString });
        services.AddRawContentRetention(configure);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        return await provider.GetRequiredService<IRawContentPurge>().PurgeExpiredAsync(cancellationToken);
    }

    /// <summary>
    /// The process exit code for a run: 1 when a step failed; otherwise 3 when overdue or limit-exceeded raw content
    /// still remains after the run; otherwise 0. Overdue content the run purged is reported, but a run that cleared
    /// it succeeded. A host that is not configured exits with 2 before any run.
    /// </summary>
    public static int ExitCodeFor(RawContentPurgeRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Outcome == RawContentPurgeOutcome.Failed)
        {
            return 1;
        }

        return run.RemainingStatus is RawContentRetentionStatus.Overdue or RawContentRetentionStatus.LimitExceeded ? 3 : 0;
    }
}
