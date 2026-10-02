using Microsoft.EntityFrameworkCore;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Infrastructure.Persistence;

/// <summary>
/// The operational context used by tenant-scoped runtime access.
/// </summary>
/// <remarks>
/// This context is bound to the non-owner application role, so every query it issues is subject to
/// row-level security. It is reached only through <see cref="ITenantScopedDatabase"/>, which opens a
/// transaction and sets the trusted tenant first.
/// </remarks>
public sealed class WorkContinuityDbContext(DbContextOptions<WorkContinuityDbContext> options)
    : DbContext(options)
{
    /// <summary>The PostgreSQL session variable carrying the trusted tenant for row-level security.</summary>
    public const string TenantSessionVariable = WorkContinuityModel.TenantSessionVariable;

    /// <summary>Gets the tenant records.</summary>
    public DbSet<TenantRecord> Tenants => Set<TenantRecord>();

    /// <summary>Gets the canonical identity records.</summary>
    public DbSet<UserRecord> Users => Set<UserRecord>();

    /// <summary>Gets the durable inbound receipts.</summary>
    public DbSet<RawReceiptRecord> RawReceipts => Set<RawReceiptRecord>();

    /// <summary>Gets the transactional outbox.</summary>
    public DbSet<OutboxMessageRecord> OutboxMessages => Set<OutboxMessageRecord>();

    /// <summary>Gets the registered source connections of the current tenant.</summary>
    public DbSet<ConnectionRecord> Connections => Set<ConnectionRecord>();

    /// <summary>Gets the durable event ledger: one entry per processed receipt.</summary>
    public DbSet<EventLedgerRecord> EventLedger => Set<EventLedgerRecord>();

    /// <summary>Gets each ledger event's changed fields and their decisions.</summary>
    public DbSet<EventLedgerFieldRecord> EventLedgerFields => Set<EventLedgerFieldRecord>();

    /// <summary>Gets the ordering evidence of the last accepted change of each object field.</summary>
    public DbSet<FieldOrderingStateRecord> FieldOrderingStates => Set<FieldOrderingStateRecord>();

    /// <summary>Gets the Snapshot's objects and their versions.</summary>
    public DbSet<SnapshotObjectRecord> SnapshotObjects => Set<SnapshotObjectRecord>();

    /// <summary>Gets the Snapshot's current field facts and their provenance.</summary>
    public DbSet<SnapshotFieldRecord> SnapshotFields => Set<SnapshotFieldRecord>();

    /// <summary>Gets the connections' delegated authorizations (Gate 3B). Written only by the authorization store.</summary>
    public DbSet<ConnectorAuthorizationRecord> ConnectorAuthorizations => Set<ConnectorAuthorizationRecord>();

    /// <summary>Gets the authorizations being started (Gate 3B). Written only by the authorization store.</summary>
    public DbSet<ConnectorAuthorizationTransactionRecord> ConnectorAuthorizationTransactions =>
        Set<ConnectorAuthorizationTransactionRecord>();

    /// <summary>Gets the reconciliation targets (Gate 3E): identifiers, scope and lifecycle, never a value.</summary>
    public DbSet<ReconciliationTargetRecord> ReconciliationTargets => Set<ReconciliationTargetRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        WorkContinuityModel.Configure(modelBuilder);
}
