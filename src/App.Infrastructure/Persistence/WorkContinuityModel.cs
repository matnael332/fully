using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence.Entities;

namespace WorkContinuity.Infrastructure.Persistence;

/// <summary>
/// The single definition of the operational schema.
/// </summary>
/// <remarks>
/// Both the runtime context and the administrative context build their model from here, so the
/// schema migrations describe and the schema runtime code queries can never drift apart.
/// Canonical value objects are converted explicitly at the storage boundary: application code never
/// degrades a <see cref="TenantId"/>, <see cref="UserId"/> or <see cref="CorrelationId"/> into a
/// bare primitive. Instants are stored as <c>timestamptz</c> and read back in UTC.
/// </remarks>
public static class WorkContinuityModel
{
    /// <summary>The PostgreSQL session variable carrying the trusted tenant for row-level security.</summary>
    public const string TenantSessionVariable = "app.tenant_id";

    /// <summary>The tenant-owned tables, every one of which carries a <c>tenant_id</c> column.</summary>
    public static IReadOnlyList<string> TenantOwnedTables { get; } =
        ["tenants", "users", "raw_receipts", "outbox_messages", "connections"];

    /// <summary>Applies the schema to a model builder.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var tenantIdConverter = new ValueConverter<TenantId, Guid>(
            id => id.Value,
            value => TenantId.From(value));

        var userIdConverter = new ValueConverter<UserId, Guid>(
            id => id.Value,
            value => UserId.From(value));

        var providerConverter = new ValueConverter<IdentityProviderId, string>(
            provider => provider.Value,
            value => IdentityProviderId.From(value));

        var correlationIdConverter = new ValueConverter<CorrelationId, string>(
            id => id.Value,
            value => ParseCorrelationId(value));

        modelBuilder.Entity<TenantRecord>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(record => record.TenantId);
            entity.Property(record => record.TenantId)
                .HasColumnName("tenant_id")
                .HasConversion(tenantIdConverter)
                .ValueGeneratedNever();
            entity.Property(record => record.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(record => record.CreatedAtUtc)
                .HasColumnName("created_at_utc")
                .HasColumnType("timestamptz")
                .IsRequired();
        });

        modelBuilder.Entity<UserRecord>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(record => record.UserId);
            entity.Property(record => record.UserId)
                .HasColumnName("user_id")
                .HasConversion(userIdConverter)
                .ValueGeneratedNever();
            entity.Property(record => record.TenantId)
                .HasColumnName("tenant_id")
                .HasConversion(tenantIdConverter)
                .IsRequired();
            entity.Property(record => record.Provider)
                .HasColumnName("identity_provider")
                .HasConversion(providerConverter)
                .HasMaxLength(IdentityProviderId.MaxLength)
                .IsRequired();
            entity.Property(record => record.ProviderSubject)
                .HasColumnName("provider_subject")
                .HasMaxLength(UserRecord.MaxProviderSubjectLength)
                .IsRequired();
            entity.Property(record => record.IsActive)
                .HasColumnName("is_active")
                .IsRequired();
            entity.Property(record => record.CreatedAtUtc)
                .HasColumnName("created_at_utc")
                .HasColumnType("timestamptz")
                .IsRequired();

            // One provider identity maps to at most one canonical user within a tenant, so a
            // duplicate or wrong identity mapping cannot create two users for one person.
            entity.HasIndex(
                    record => new { record.TenantId, record.Provider, record.ProviderSubject })
                .HasDatabaseName("ux_users_tenant_provider_subject")
                .IsUnique();
        });

        modelBuilder.Entity<RawReceiptRecord>(entity =>
        {
            entity.ToTable("raw_receipts");
            entity.HasKey(record => record.ReceiptId);
            entity.Property(record => record.ReceiptId)
                .HasColumnName("receipt_id")
                .ValueGeneratedNever();
            entity.Property(record => record.TenantId)
                .HasColumnName("tenant_id")
                .HasConversion(tenantIdConverter)
                .IsRequired();
            entity.Property(record => record.ConnectionReference)
                .HasColumnName("connection_reference")
                .HasMaxLength(RawReceiptRecord.MaxReferenceLength)
                .IsRequired();
            entity.Property(record => record.SourceSystem)
                .HasColumnName("source_system")
                .HasMaxLength(RawReceiptRecord.MaxReferenceLength)
                .IsRequired();
            entity.Property(record => record.SourceEventId)
                .HasColumnName("source_event_id")
                .HasMaxLength(RawReceiptRecord.MaxReferenceLength);
            entity.Property(record => record.SchemaVersion)
                .HasColumnName("schema_version")
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(record => record.ReceivedAtUtc)
                .HasColumnName("received_at_utc")
                .HasColumnType("timestamptz")
                .IsRequired();
            entity.Property(record => record.Payload)
                .HasColumnName("payload")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(record => record.PayloadFingerprint)
                .HasColumnName("payload_fingerprint")
                .HasMaxLength(128)
                .IsRequired();
            entity.Property(record => record.ProcessingState)
                .HasColumnName("processing_state")
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            // Partial unique index: a provider event id is unique per tenant and connection, while
            // rows without one stay unconstrained. PostgreSQL treats NULLs as distinct, so an
            // unfiltered unique index would silently allow unlimited duplicates among them; the
            // filter makes that behaviour deliberate rather than accidental.
            entity.HasIndex(
                    record => new { record.TenantId, record.ConnectionReference, record.SourceEventId })
                .HasDatabaseName("ux_raw_receipts_tenant_connection_source_event")
                .IsUnique()
                .HasFilter("source_event_id IS NOT NULL");

            // Supports the rolling 30-day purge of raw work payloads.
            entity.HasIndex(record => record.ReceivedAtUtc)
                .HasDatabaseName("ix_raw_receipts_received_at_utc");
        });

        modelBuilder.Entity<OutboxMessageRecord>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(record => record.OutboxMessageId);
            entity.Property(record => record.OutboxMessageId)
                .HasColumnName("outbox_message_id")
                .ValueGeneratedNever();
            entity.Property(record => record.TenantId)
                .HasColumnName("tenant_id")
                .HasConversion(tenantIdConverter)
                .IsRequired();
            entity.Property(record => record.MessageType)
                .HasColumnName("message_type")
                .HasMaxLength(OutboxMessageRecord.MaxMessageTypeLength)
                .IsRequired();
            entity.Property(record => record.Payload)
                .HasColumnName("payload")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(record => record.OccurredAtUtc)
                .HasColumnName("occurred_at_utc")
                .HasColumnType("timestamptz")
                .IsRequired();
            entity.Property(record => record.CorrelationId)
                .HasColumnName("correlation_id")
                .HasConversion(correlationIdConverter)
                .HasMaxLength(CorrelationId.MaxLength)
                .IsRequired();
            entity.Property(record => record.PublishedAtUtc)
                .HasColumnName("published_at_utc")
                .HasColumnType("timestamptz");

            // Lets a dispatcher find pending messages without scanning published history.
            entity.HasIndex(record => new { record.PublishedAtUtc, record.OccurredAtUtc })
                .HasDatabaseName("ix_outbox_messages_pending")
                .HasFilter("published_at_utc IS NULL");
        });

        modelBuilder.Entity<ConnectionRecord>(entity =>
        {
            entity.ToTable("connections");
            entity.HasKey(record => record.ConnectionId);
            entity.Property(record => record.ConnectionId)
                .HasColumnName("connection_id")
                .ValueGeneratedNever();
            entity.Property(record => record.TenantId)
                .HasColumnName("tenant_id")
                .HasConversion(tenantIdConverter)
                .IsRequired();
            entity.Property(record => record.SourceSystem)
                .HasColumnName("source_system")
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(record => record.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(record => record.CreatedAtUtc)
                .HasColumnName("created_at_utc")
                .HasColumnType("timestamptz")
                .IsRequired();

            // Supports listing a tenant's connections under its own row-level security scope.
            entity.HasIndex(record => record.TenantId)
                .HasDatabaseName("ix_connections_tenant_id");
        });
    }

    private static CorrelationId ParseCorrelationId(string value) =>
        CorrelationId.TryParse(value, out var correlationId)
            ? correlationId
            : throw new InvalidOperationException(
                "A stored correlation id failed validation. The row is malformed.");
}
