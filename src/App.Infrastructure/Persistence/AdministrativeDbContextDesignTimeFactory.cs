using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WorkContinuity.Infrastructure.Persistence;

/// <summary>
/// Supplies an <see cref="AdministrativeDbContext"/> to the EF Core command-line tooling.
/// </summary>
/// <remarks>
/// Used only when creating or inspecting migrations. Generating a migration does not connect to a
/// database, so the fallback below is a provider-shaped placeholder with no credential in it; a real
/// run supplies <c>ConnectionStrings__PostgresAdmin</c> from the untracked local environment.
/// </remarks>
internal sealed class AdministrativeDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<AdministrativeDbContext>
{
    public AdministrativeDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__PostgresAdmin")
            ?? "Host=localhost;Port=5432;Database=workcontinuity";

        var options = new DbContextOptionsBuilder<AdministrativeDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AdministrativeDbContext(options);
    }
}
