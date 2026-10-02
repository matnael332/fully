using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Core.Reconciliation;

namespace WorkContinuity.Infrastructure.Reconciliation;

/// <summary>Registers reconciliation (Gate 3E).</summary>
/// <remarks>
/// Registration only: nothing here schedules a run. Reconciliation runs when a caller invokes it for one tenant and
/// connection; no host registers it in Gate 3E, and nothing activates it in Azure. Readers are registered by the
/// composition root that owns the connector, alongside <c>AddEventProcessing</c>, whose options it shares.
/// </remarks>
public static class ReconciliationRegistration
{
    /// <summary>Adds the source reconciler.</summary>
    public static IServiceCollection AddSourceReconciliation(
        this IServiceCollection services,
        Action<ReconciliationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ReconciliationOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddScoped<ISourceReconciler, SourceReconciler>();
        return services;
    }
}
