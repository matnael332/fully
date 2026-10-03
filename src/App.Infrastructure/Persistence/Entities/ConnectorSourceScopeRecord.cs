using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>
/// One provider resource a connection is configured to sync (Gate 3F, decision D3F-1), by the provider's own id.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers, a flag and a generation only. The tenant, connection, source and resource never change once written,
/// which the runtime role's column-level grants enforce: it may update only whether the scope is enabled and which
/// generation established its baseline. Rows are never deleted.
/// </para>
/// <para>
/// The scope's state is current work only while the scope is enabled and its baseline is established: set in the
/// transition to Live of a generation that read the scope in full, cleared in the transaction that disables it, and not
/// restored by enabling it again, so a scope enabled again is current only once a new generation has read it.
/// </para>
/// </remarks>
public sealed class ConnectorSourceScopeRecord : ITenantOwned
{
    private ConnectorSourceScopeRecord()
    {
        // Materialization only.
    }

    /// <summary>Gets the owning tenant.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the connection the scope belongs to.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>Gets the source system.</summary>
    public string SourceSystem { get; private set; } = string.Empty;

    /// <summary>Gets the provider's resource id.</summary>
    public string ResourceId { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether initial sync reads the scope.</summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// Gets the sync generation whose transition to Live established the scope's current baseline, or null while it has
    /// none: never read by a generation that reached Live, disabled since, or enabled again and not yet read.
    /// </summary>
    public long? BaselineGeneration { get; private set; }

    /// <summary>Gets when the scope was configured.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets when the scope last changed.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>A new scope.</summary>
    public static ConnectorSourceScopeRecord Configure(
        TenantId tenant,
        Guid connectionId,
        string sourceSystem,
        string resourceId,
        bool enabled,
        DateTimeOffset now) =>
        new()
        {
            TenantId = tenant,
            ConnectionId = connectionId,
            SourceSystem = sourceSystem,
            ResourceId = resourceId,
            Enabled = enabled,
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime(),
        };

    /// <summary>Enables or disables the scope. A disabled scope has no baseline, and enabling it again restores none.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool SetEnabled(bool enabled, DateTimeOffset now)
    {
        if (Enabled == enabled)
        {
            return false;
        }

        Enabled = enabled;
        if (!enabled)
        {
            BaselineGeneration = null;
        }

        UpdatedAtUtc = now.ToUniversalTime();
        return true;
    }

    /// <summary>
    /// The generation entering Live read the scope in full: its state is current from now on. Called in that transition's
    /// transaction only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scope is disabled, or the generation is not a started one.</exception>
    public void EstablishBaseline(long generation, DateTimeOffset now)
    {
        if (!Enabled || generation < 1)
        {
            throw new InvalidOperationException("Only an enabled scope establishes a baseline, in a started generation.");
        }

        BaselineGeneration = generation;
        UpdatedAtUtc = now.ToUniversalTime();
    }
}
