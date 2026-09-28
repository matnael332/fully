using WorkContinuity.Core.Events;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>The lifecycle state of a connection.</summary>
public enum ConnectionStatus
{
    /// <summary>Unspecified. Never persisted.</summary>
    None = 0,

    /// <summary>Deliveries through this connection are accepted.</summary>
    Active = 1,

    /// <summary>Deliveries through this connection are refused.</summary>
    Disabled = 2,
}

/// <summary>
/// A registered channel through which one source system delivers events for one tenant.
/// </summary>
/// <remarks>
/// <para>
/// The connection is how ingress attributes a delivery to a tenant server-side: the tenant comes from
/// this row, never from the delivery (invariants 1 and 45). Before a tenant is known, only the single
/// approved function <c>resolve_ingress_connection</c> may read it (Gate 2 owner decision 1); every other
/// access is tenant-scoped under forced row-level security like any tenant-owned table.
/// </para>
/// <para>
/// Deliberately minimal: no credential or verification secret is stored here, because secrets never
/// live in ordinary database fields (invariant 53).
/// </para>
/// </remarks>
public sealed class ConnectionRecord : ITenantOwned
{
    private ConnectionRecord()
    {
        // Materialization only.
    }

    private ConnectionRecord(
        Guid connectionId,
        TenantId tenantId,
        string sourceSystem,
        ConnectionStatus status,
        DateTimeOffset createdAtUtc)
    {
        ConnectionId = connectionId;
        TenantId = tenantId;
        SourceSystem = sourceSystem;
        Status = status;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Gets the connection identifier.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>Gets the owning tenant. Fixed at creation.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the source system token this connection serves, for example "monday".</summary>
    public string SourceSystem { get; private set; } = string.Empty;

    /// <summary>Gets the lifecycle state.</summary>
    public ConnectionStatus Status { get; private set; }

    /// <summary>Gets the UTC creation instant.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// The connection reference recorded on durable receipts and canonical event sources for a
    /// connection: its id in canonical form.
    /// </summary>
    public static string ReferenceFor(Guid connectionId) => connectionId.ToString("D");

    /// <summary>Registers a connection.</summary>
    /// <exception cref="ArgumentException">A component is missing or invalid.</exception>
    public static ConnectionRecord Register(
        Guid connectionId,
        TenantId tenantId,
        string sourceSystem,
        ConnectionStatus status,
        DateTimeOffset createdAtUtc)
    {
        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("A connection requires an id.", nameof(connectionId));
        }

        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("A connection requires a tenant.", nameof(tenantId));
        }

        if (!EventText.IsToken(sourceSystem))
        {
            throw new ArgumentException(
                "A connection's source system must be a token of 1-64 lowercase letters, digits, '-', '_' or '.'.",
                nameof(sourceSystem));
        }

        if (status == ConnectionStatus.None || !Enum.IsDefined(status))
        {
            throw new ArgumentException("A connection requires a defined status.", nameof(status));
        }

        return new ConnectionRecord(connectionId, tenantId, sourceSystem, status, createdAtUtc.ToUniversalTime());
    }
}
