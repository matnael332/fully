using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WorkContinuity.Infrastructure.Persistence;

/// <summary>
/// Applies schema migrations using the administrative connection.
/// </summary>
/// <remarks>
/// <para>
/// This is the isolated elevated path. It is deliberately not a general cross-tenant escape hatch:
/// it exposes only "bring the schema up to date" and "report migration status", and it offers no way
/// to read or write tenant rows. There is no method that disables row-level security.
/// </para>
/// <para>
/// It is registered separately from <see cref="ITenantScopedDatabase"/>, so ordinary Web and Worker
/// request handling cannot resolve the administrative connection even by asking for it.
/// </para>
/// </remarks>
public sealed class DatabaseMigrator(
    DbContextOptions<AdministrativeDbContext> options,
    ILogger<DatabaseMigrator> logger)
{
    /// <summary>Builds a migrator for a connection string, without a dependency injection container.</summary>
    public static DatabaseMigrator ForConnection(
        string administrativeConnectionString,
        ILogger<DatabaseMigrator> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administrativeConnectionString);

        var options = new DbContextOptionsBuilder<AdministrativeDbContext>()
            .UseNpgsql(administrativeConnectionString)
            .Options;

        return new DatabaseMigrator(options, logger);
    }

    /// <summary>Applies every pending migration. Safe to run repeatedly and never destructive.</summary>
    public async Task<MigrationStatus> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = new AdministrativeDbContext(options);

        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        if (pending.Count > 0)
        {
            logger.LogInformation(
                "Applying {PendingCount} pending migration(s): {Migrations}",
                pending.Count,
                string.Join(", ", pending));

            await context.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            logger.LogInformation("Database schema is already current; nothing to apply.");
        }

        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();

        return new MigrationStatus(applied, []);
    }

    /// <summary>Reports applied and pending migrations without changing anything.</summary>
    public async Task<MigrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var context = new AdministrativeDbContext(options);

        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        return new MigrationStatus(applied, pending);
    }
}

/// <summary>The applied and pending migrations of a database.</summary>
public sealed record MigrationStatus(IReadOnlyList<string> Applied, IReadOnlyList<string> Pending);

/// <summary>
/// The context used only by the administrative path.
/// </summary>
/// <remarks>
/// It builds the same model as <see cref="WorkContinuityDbContext"/>, so migrations and runtime
/// queries describe one schema, but it is a distinct type bound to the administrative connection.
/// That separation is what keeps runtime dependency injection from handing an elevated connection to
/// request-handling code.
/// </remarks>
public sealed class AdministrativeDbContext(DbContextOptions<AdministrativeDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        WorkContinuityModel.Configure(modelBuilder);
}
