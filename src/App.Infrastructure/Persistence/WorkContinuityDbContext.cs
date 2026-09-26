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

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        WorkContinuityModel.Configure(modelBuilder);
}
