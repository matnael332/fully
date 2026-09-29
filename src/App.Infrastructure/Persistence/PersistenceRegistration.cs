using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WorkContinuity.Infrastructure.Persistence;

/// <summary>
/// Composition-root registration for persistence.
/// </summary>
public static class PersistenceRegistration
{
    /// <summary>
    /// Registers tenant-scoped persistence for normal Web and Worker code.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ITenantScopedDatabase"/> is exposed, bound to the non-owner application role.
    /// The administrative connection is deliberately not registered here, so request-handling code
    /// cannot resolve an elevated connection even by asking for it.
    /// </remarks>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddPersistence(ReadOptions(configuration));
    }

    /// <summary>
    /// Registers tenant-scoped persistence from options the host composed itself, for a host without a
    /// configuration framework such as the Worker's explicit entry points.
    /// </summary>
    /// <remarks>
    /// Exactly what the configuration overload registers: only <see cref="ITenantScopedDatabase"/>, bound to the
    /// application connection.
    /// </remarks>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        PersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApplicationConnectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'Postgres' is not configured. Set ConnectionStrings__Postgres.");
        }

        services.AddSingleton(options);

        services.AddDbContext<WorkContinuityDbContext>(builder =>
            builder.UseNpgsql(options.ApplicationConnectionString));

        services.AddScoped<ITenantScopedDatabase, TenantScopedDatabase>();

        return services;
    }

    /// <summary>
    /// Registers the isolated administrative path used to apply migrations.
    /// </summary>
    /// <remarks>
    /// Intended for a migration entry point or a maintenance job, never for request handling. It
    /// grants schema access, not tenant data access.
    /// </remarks>
    public static IServiceCollection AddDatabaseMigrator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadOptions(configuration);

        if (string.IsNullOrWhiteSpace(options.AdministrativeConnectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'PostgresAdmin' is not configured. "
                + "Set ConnectionStrings__PostgresAdmin for the migration path only.");
        }

        services.AddSingleton(serviceProvider => DatabaseMigrator.ForConnection(
            options.AdministrativeConnectionString,
            serviceProvider.GetRequiredService<ILogger<DatabaseMigrator>>()));

        return services;
    }

    /// <summary>Reads and validates the persistence options from configuration.</summary>
    /// <exception cref="InvalidOperationException">A required connection string is missing.</exception>
    public static PersistenceOptions ReadOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PersistenceOptions.SectionName);

        var options = new PersistenceOptions
        {
            // Connection strings come from configuration or environment only; nothing is compiled in.
            ApplicationConnectionString = configuration.GetConnectionString("Postgres") ?? string.Empty,
            AdministrativeConnectionString =
                configuration.GetConnectionString("PostgresAdmin") ?? string.Empty,
            // Parsed directly rather than via the configuration binder, which would add a
            // package Infrastructure does not otherwise need. Absent or unparseable means false.
            AutoMigrateOnStartup =
                bool.TryParse(section["AutoMigrateOnStartup"], out var autoMigrate) && autoMigrate,
        };

        return options;
    }
}
