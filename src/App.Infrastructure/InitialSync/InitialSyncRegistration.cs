using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Core.InitialSync;

namespace WorkContinuity.Infrastructure.InitialSync;

/// <summary>Registers initial sync and source-scope configuration (Gate 3F).</summary>
/// <remarks>
/// Registration only: nothing here schedules a run or starts a sync. Initial sync runs when a caller invokes it for one
/// tenant and connection; no host registers it in Gate 3F, and nothing activates it in Azure. Readers are registered by the
/// composition root that owns the connector, alongside <c>AddEventProcessing</c>, whose processor and options it uses.
/// </remarks>
public static class InitialSyncRegistration
{
    /// <summary>Adds initial sync and source-scope configuration.</summary>
    public static IServiceCollection AddInitialSync(
        this IServiceCollection services,
        Action<InitialSyncOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new InitialSyncOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddScoped<IInitialSync, InitialSyncEngine>();
        services.AddScoped<IConnectorSourceScopes, ConnectorSourceScopes>();
        return services;
    }
}
