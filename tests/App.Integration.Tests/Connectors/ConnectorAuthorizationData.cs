using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Connectors;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Connectors;

/// <summary>Seeds and reaches the Gate 3B authorization tables as the non-owner runtime role.</summary>
internal static class ConnectorAuthorizationData
{
    internal const string Scopes = "boards:read me:read";

    /// <summary>A tenant-scoped authorization store over a fresh runtime context, bound to one trusted tenant.</summary>
    internal static ScopedStore Store(DatabaseFixture fixture, TenantId tenant)
    {
        var context = fixture.CreateApplicationContext();
        var accessor = new FixedTenantContextAccessor(tenant);
        var database = new TenantScopedDatabase(context, accessor, NullLogger<TenantScopedDatabase>.Instance);
        return new ScopedStore(new ConnectorAuthorizationStore(database, accessor), accessor, context);
    }

    /// <summary>A new active tenant.</summary>
    internal static async Task<TenantId> NewTenantAsync(DatabaseFixture fixture, CancellationToken cancellationToken)
    {
        var tenant = TenantId.New();
        await IngressData.SeedTenantAsync(fixture, tenant, "Active", cancellationToken);
        return tenant;
    }

    /// <summary>A canonical user of the tenant, written by the schema owner.</summary>
    internal static async Task<UserId> SeedUserAsync(
        DatabaseFixture fixture,
        TenantId tenant,
        CancellationToken cancellationToken,
        bool isActive = true)
    {
        var user = UserId.New();
        await using var connection = await fixture.OpenAdministrativeConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users (user_id, tenant_id, identity_provider, provider_subject, is_active, created_at_utc)
            VALUES (@user, @tenant, 'entra-id', @subject, @active, now());
            """;
        command.Parameters.AddWithValue("user", user.Value);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("subject", "subject-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("active", isActive);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return user;
    }

    /// <summary>A transaction for a new connection, with the SHA-256 of the given state secret.</summary>
    internal static ConnectorAuthorizationTransaction Transaction(
        UserId user,
        string stateSecret,
        Guid? connectionId = null,
        ConnectorAuthorizationPurpose purpose = ConnectorAuthorizationPurpose.Connect) =>
        new(
            Guid.CreateVersion7(),
            user,
            connectionId ?? Guid.CreateVersion7(),
            "monday",
            purpose,
            Hash(stateSecret),
            Hash("https://app.example.test/connectors/monday/callback"),
            Scopes);

    /// <summary>A grant for a connection of the user, with a fresh material version.</summary>
    internal static ConnectorAuthorizationGrant Grant(
        UserId user,
        Guid? connectionId = null,
        string subject = "58201774",
        string account = "9001",
        Guid? materialVersion = null) =>
        new(
            connectionId ?? Guid.CreateVersion7(),
            "monday",
            user,
            subject,
            account,
            materialVersion ?? Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddHours(1),
            Scopes);

    /// <summary>An active authorization of a new connection of a new user, through the real store.</summary>
    internal static async Task<(UserId User, ConnectorAuthorizationGrant Grant)> SeedActiveAsync(
        DatabaseFixture fixture,
        TenantId tenant,
        CancellationToken cancellationToken)
    {
        var user = await SeedUserAsync(fixture, tenant, cancellationToken);
        var grant = Grant(user);
        await using var scoped = Store(fixture, tenant);
        var activated = await scoped.Store.ActivateAsync(grant, cancellationToken);
        Assert.True(activated.IsSuccess && activated.Value, activated.IsSuccess ? "not activated" : activated.Error.Code);
        return (user, grant);
    }

    /// <summary>
    /// Rows of the tenant in both Gate 3B tables, written through the real store: an active authorization of a new
    /// connection and an open transaction. Returns the active authorization's grant.
    /// </summary>
    internal static async Task<ConnectorAuthorizationGrant> SeedAuthorizationRowsAsync(DatabaseFixture fixture, TenantId tenant, CancellationToken cancellationToken)
    {
        var (user, grant) = await SeedActiveAsync(fixture, tenant, cancellationToken);
        await using var scoped = Store(fixture, tenant);
        var opened = await scoped.Store.OpenTransactionAsync(
            Transaction(user, "state-secret-" + Guid.NewGuid().ToString("N")),
            ConnectorAuthorizationTransaction.MaxLifetime,
            cancellationToken);
        Assert.True(opened.IsSuccess && opened.Value, opened.IsSuccess ? "not opened" : opened.Error.Code);
        return grant;
    }

    internal static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>A store and the context it owns.</summary>
    internal sealed class ScopedStore(ConnectorAuthorizationStore store, FixedTenantContextAccessor accessor, WorkContinuityDbContext context)
        : IAsyncDisposable
    {
        internal ConnectorAuthorizationStore Store { get; } = store;

        internal FixedTenantContextAccessor Accessor { get; } = accessor;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    /// <summary>A trusted tenant context fixed for the scope.</summary>
    internal sealed class FixedTenantContextAccessor(TenantId tenant) : ITenantContextAccessor
    {
        public ITenantContext Current { get; } = TenantContext.Resolved(tenant, TenantResolutionSource.AuthenticatedPrincipal);

        public void SetResolved(ITenantContext tenantContext) =>
            throw new NotSupportedException("The test fixes the tenant context.");
    }
}
