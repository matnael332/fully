using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Integration.Tests.Database;

/// <summary>
/// Connects the database tests to the real local PostgreSQL instance.
/// </summary>
/// <remarks>
/// These are genuine integration tests: there is no in-memory provider and no mock, because an
/// in-memory store cannot exhibit row-level security, a partial unique index or transaction-local
/// settings, which is exactly what is under test. If the database is unreachable the tests fail with
/// an explicit message rather than being skipped, so an unproven isolation guarantee can never be
/// mistaken for a passing one.
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private string _applicationConnectionString = string.Empty;
    private string _administrativeConnectionString = string.Empty;

    /// <summary>Gets the connection string for the non-owner runtime role.</summary>
    public string ApplicationConnectionString =>
        _applicationConnectionString.Length > 0
            ? _applicationConnectionString
            : throw new InvalidOperationException("The fixture has not been initialized.");

    /// <summary>Gets the connection string for the schema owner.</summary>
    public string AdministrativeConnectionString =>
        _administrativeConnectionString.Length > 0
            ? _administrativeConnectionString
            : throw new InvalidOperationException("The fixture has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        var settings = LocalEnvironment.Read();

        var databaseHost = settings.GetValueOrDefault("POSTGRES_TEST_HOST", "localhost");
        var port = settings.GetValueOrDefault("POSTGRES_PORT", "5432");
        var database = Require(settings, "POSTGRES_DB");
        var owner = Require(settings, "POSTGRES_USER");
        var ownerPassword = Require(settings, "POSTGRES_PASSWORD");
        var appUser = settings.GetValueOrDefault("POSTGRES_APP_USER", PersistenceOptions.RuntimeRoleName);
        var appPassword = Require(settings, "POSTGRES_APP_PASSWORD");

        _administrativeConnectionString =
            $"Host={databaseHost};Port={port};Database={database};Username={owner};Password={ownerPassword}";
        _applicationConnectionString =
            $"Host={databaseHost};Port={port};Database={database};Username={appUser};Password={appPassword}";

        await EnsureReachableAsync();

        // A fresh database must be able to migrate from zero, and re-running must be a no-op.
        var migrator = DatabaseMigrator.ForConnection(
            _administrativeConnectionString,
            NullLogger<DatabaseMigrator>.Instance);

        await migrator.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Creates a runtime context bound to the non-owner role, so RLS applies.</summary>
    public WorkContinuityDbContext CreateApplicationContext()
    {
        var options = new DbContextOptionsBuilder<WorkContinuityDbContext>()
            .UseNpgsql(ApplicationConnectionString)
            .Options;

        return new WorkContinuityDbContext(options);
    }

    /// <summary>Creates a tenant-scoped database over a fresh runtime context.</summary>
    public (ITenantScopedDatabase Database, WorkContinuityDbContext Context) CreateTenantScoped(
        ITenantContext tenantContext)
    {
        var context = CreateApplicationContext();
        var accessor = new StubTenantContextAccessor(tenantContext);

        var database = new TenantScopedDatabase(
            context,
            accessor,
            NullLogger<TenantScopedDatabase>.Instance);

        return (database, context);
    }

    /// <summary>Creates a tenant-scoped database for an already-trusted tenant.</summary>
    public (ITenantScopedDatabase Database, WorkContinuityDbContext Context) CreateTenantScoped(
        TenantId tenantId) =>
        CreateTenantScoped(
            TenantContext.Resolved(tenantId, TenantResolutionSource.AuthenticatedPrincipal));

    /// <summary>Opens a raw administrative connection for schema-level assertions.</summary>
    public async Task<NpgsqlConnection> OpenAdministrativeConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(AdministrativeConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Seeds a tenant row using the administrative connection.</summary>
    public async Task SeedTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO tenants (tenant_id, status, created_at_utc)
            VALUES (@tenant_id, 'Active', now())
            ON CONFLICT (tenant_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("tenant_id", tenantId.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureReachableAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(_administrativeConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException)
        {
            throw new InvalidOperationException(
                "The local PostgreSQL database is not reachable, so the Gate 1D database guarantees "
                + "cannot be proven. Start the local stack with './eng/dev.ps1 up' and re-run.",
                exception);
        }
    }

    private static string Require(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new InvalidOperationException(
                $"'{key}' is not configured. It is read from the environment or the untracked .env "
                + "at the repository root.");

    private sealed class StubTenantContextAccessor(ITenantContext current) : ITenantContextAccessor
    {
        public ITenantContext Current { get; } = current;

        public void SetResolved(ITenantContext tenantContext) =>
            throw new NotSupportedException("The test fixture supplies a fixed tenant context.");
    }
}

/// <summary>Shares one database fixture across the database test classes.</summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "postgres";
}
