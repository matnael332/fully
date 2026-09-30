using WorkContinuity.Core.Ingress;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Ingress;

/// <summary>
/// The Gate 3C connection check against the real local PostgreSQL, through the runtime role: through the approved
/// resolver only, it tells whether a connection id names an active connection of one source system, reports every other
/// case the same way, never sets a tenant, and stores nothing.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class IngressConnectionCheckTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_active_connection_of_the_source_is_active_and_nothing_else_is()
    {
        var (_, active) = await IngressData.NewConnectionAsync(fixture, Token, sourceSystem: "monday");
        var (_, disabled) = await IngressData.NewConnectionAsync(fixture, Token, connectionStatus: ConnectionStatus.Disabled, sourceSystem: "monday");
        var (_, suspendedTenant) = await IngressData.NewConnectionAsync(fixture, Token, tenantStatus: "Suspended", sourceSystem: "monday");
        await using var host = new IngressHost(fixture);

        Assert.Equal(IngressConnectionStatus.Active, await host.CheckAsync(active, "monday"));

        // Unknown, inactive and another source's connection are indistinguishable.
        Assert.Equal(IngressConnectionStatus.Unknown, await host.CheckAsync(Guid.CreateVersion7(), "monday"));
        Assert.Equal(IngressConnectionStatus.Unknown, await host.CheckAsync(disabled, "monday"));
        Assert.Equal(IngressConnectionStatus.Unknown, await host.CheckAsync(active, "synthetic"));
        Assert.Equal(IngressConnectionStatus.Unknown, await host.CheckAsync(Guid.Empty, "monday"));
        Assert.Equal(IngressConnectionStatus.Unknown, await host.CheckAsync(active, "Monday"));
        Assert.Equal(IngressConnectionStatus.Unknown, await host.CheckAsync(active, string.Empty));

        // The check answers for the connection only; its tenant's status is decided when a delivery is stored.
        Assert.Equal(IngressConnectionStatus.Active, await host.CheckAsync(suspendedTenant, "monday"));
    }

    [Fact]
    public async Task The_check_never_sets_a_tenant_and_stores_nothing()
    {
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token, sourceSystem: "monday");
        await using var host = new IngressHost(fixture);

        Assert.Equal(IngressConnectionStatus.Active, await host.CheckAsync(connection, "monday"));

        Assert.False(host.TenantContextAccessor.Current.IsResolved);
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, tenant, Token));
        Assert.Empty(await IngressData.MarkersAsync(fixture, tenant, Token));
    }

    [Fact]
    public async Task An_unreachable_database_makes_the_check_unavailable_and_is_logged_with_the_connection_id_only()
    {
        await using var host = IngressHost.Unreachable();
        var connection = Guid.CreateVersion7();

        Assert.Equal(IngressConnectionStatus.Unavailable, await host.CheckAsync(connection, "monday"));
        Assert.Contains(host.Logs.Entries, entry => entry.Message.Contains(connection.ToString("D"), StringComparison.Ordinal));
    }
}
