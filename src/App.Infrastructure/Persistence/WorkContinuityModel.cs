using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.SourceOfTruth;
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
        [
            "tenants", "users", "raw_receipts", "outbox_messages", "connections", "event_ledger", "event_ledger_fields",
            "field_ordering_states", "snapshot_objects", "snapshot_fields",
        ];

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

            // Supports flagging duplicate candidates among deliveries that carry no provider event id.
            entity.HasIndex(record => new { record.TenantId, record.ConnectionReference, record.PayloadFingerprint })
                .HasDatabaseName("ix_raw_receipts_tenant_connection_fingerprint")
                .HasFilter("source_event_id IS NULL");
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

        ConfigureEventLedger(modelBuilder, tenantIdConverter, correlationIdConverter);
        ConfigureSnapshot(modelBuilder, tenantIdConverter);
    }

    // The Gate 2D ledger: identity, provenance, ordering and deduplication metadata only. No column can hold a
    // value, an actor, a reference, a payload or a fingerprint; raw content stays in raw_receipts and expires.
    private static void ConfigureEventLedger(
        ModelBuilder modelBuilder,
        ValueConverter<TenantId, Guid> tenantIdConverter,
        ValueConverter<CorrelationId, string> correlationIdConverter)
    {
        modelBuilder.Entity<EventLedgerRecord>(entity =>
        {
            entity.ToTable("event_ledger", table =>
            {
                table.HasCheckConstraint(
                    "ck_event_ledger_outcome",
                    "outcome IN ('AcceptedForProjection', 'Stale', 'ReconciliationRequired', 'Quarantined')");
                table.HasCheckConstraint(
                    "ck_event_ledger_quarantine_reason",
                    "(outcome = 'Quarantined') = (quarantine_reason IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_event_ledger_reason_code",
                    "(outcome IN ('Quarantined', 'ReconciliationRequired')) = (reason_code IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_event_ledger_normalized",
                    "outcome = 'Quarantined' OR (event_type IS NOT NULL AND schema_version IS NOT NULL "
                    + "AND object_system IS NOT NULL AND object_connection IS NOT NULL "
                    + "AND object_type IS NOT NULL AND object_id IS NOT NULL)");
            });
            entity.HasKey(record => record.EventId);
            entity.Property(record => record.EventId).HasColumnName("event_id").ValueGeneratedNever();
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter).IsRequired();
            entity.Property(record => record.ReceiptId).HasColumnName("receipt_id").IsRequired();
            entity.Property(record => record.SourceKind).HasColumnName("source_kind").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.SourceSystem).HasColumnName("source_system").HasMaxLength(64).IsRequired();
            entity.Property(record => record.ConnectionReference).HasColumnName("connection_reference").HasMaxLength(EventLedgerRecord.MaxTextLength).IsRequired();
            entity.Property(record => record.SourceEventId).HasColumnName("source_event_id").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.EventType).HasColumnName("event_type").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.SchemaVersion).HasColumnName("schema_version").HasMaxLength(64);
            entity.Property(record => record.ObjectSystem).HasColumnName("object_system").HasMaxLength(64);
            entity.Property(record => record.ObjectConnection).HasColumnName("object_connection").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.ObjectType).HasColumnName("object_type").HasMaxLength(64);
            entity.Property(record => record.ObjectId).HasColumnName("object_id").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.SourceRevision).HasColumnName("source_revision");
            entity.Property(record => record.SourceUpdatedAtUtc).HasColumnName("source_updated_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.OccurredAtUtc).HasColumnName("occurred_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.ReceivedAtUtc).HasColumnName("received_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.ProcessedAtUtc).HasColumnName("processed_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.CorrelationId).HasColumnName("correlation_id").HasConversion(correlationIdConverter).HasMaxLength(CorrelationId.MaxLength).IsRequired();
            entity.Property(record => record.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.QuarantineReason).HasColumnName("quarantine_reason").HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.ReasonCode).HasColumnName("reason_code").HasMaxLength(Core.Results.Error.MaxCodeLength);
            entity.Property(record => record.DuplicateCandidateOf).HasColumnName("duplicate_candidate_of");

            // The key tenant-consistent foreign keys point at: a reference can never cross a tenant, even though
            // PostgreSQL checks foreign keys without row-level security.
            entity.HasAlternateKey(record => new { record.TenantId, record.EventId })
                .HasName("ak_event_ledger_tenant_event");

            // Persistent idempotency: one ledger entry per receipt, whatever the broker delivers.
            entity.HasIndex(record => new { record.TenantId, record.ReceiptId })
                .HasDatabaseName("ux_event_ledger_tenant_receipt")
                .IsUnique();

            // The provider event id is the authoritative duplicate key within a tenant and connection.
            entity.HasIndex(record => new { record.TenantId, record.ConnectionReference, record.SourceEventId })
                .HasDatabaseName("ux_event_ledger_tenant_connection_source_event")
                .IsUnique()
                .HasFilter("source_event_id IS NOT NULL");
        });

        modelBuilder.Entity<EventLedgerFieldRecord>(entity =>
        {
            entity.ToTable("event_ledger_fields", table => table.HasCheckConstraint(
                "ck_event_ledger_fields_decision",
                "decision IN ('Advance', 'Stale', 'Ambiguous', 'Impossible', 'Withheld')"));
            entity.HasKey(record => new { record.EventId, record.Field });
            entity.Property(record => record.EventId).HasColumnName("event_id");
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter).IsRequired();
            entity.Property(record => record.Field).HasColumnName("field").HasMaxLength(64);
            entity.Property(record => record.DataType).HasColumnName("data_type").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.Decision).HasColumnName("decision").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.HasOne<EventLedgerRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.EventId })
                .HasPrincipalKey(ledger => new { ledger.TenantId, ledger.EventId })
                .HasConstraintName("fk_event_ledger_fields_event")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(record => new { record.TenantId, record.EventId })
                .HasDatabaseName("ix_event_ledger_fields_tenant_event");
        });

        modelBuilder.Entity<FieldOrderingStateRecord>(entity =>
        {
            entity.ToTable("field_ordering_states");
            entity.HasKey(record => new
            {
                record.TenantId,
                record.ObjectSystem,
                record.ObjectConnection,
                record.ObjectType,
                record.ObjectId,
                record.Field,
            });
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter);
            entity.Property(record => record.ObjectSystem).HasColumnName("object_system").HasMaxLength(64);
            entity.Property(record => record.ObjectConnection).HasColumnName("object_connection").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.ObjectType).HasColumnName("object_type").HasMaxLength(64);
            entity.Property(record => record.ObjectId).HasColumnName("object_id").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.Field).HasColumnName("field").HasMaxLength(64);
            entity.Property(record => record.LastEventId).HasColumnName("last_event_id").IsRequired();
            entity.Property(record => record.SourceRevision).HasColumnName("source_revision");
            entity.Property(record => record.SourceUpdatedAtUtc).HasColumnName("source_updated_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.OccurredAtUtc).HasColumnName("occurred_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.SourceEventId).HasColumnName("source_event_id").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.AcceptedAtUtc).HasColumnName("accepted_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Ignore(record => record.Evidence);
            entity.HasOne<EventLedgerRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.LastEventId })
                .HasPrincipalKey(ledger => new { ledger.TenantId, ledger.EventId })
                .HasConstraintName("fk_field_ordering_states_last_event")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(record => new { record.TenantId, record.LastEventId })
                .HasDatabaseName("ix_field_ordering_states_tenant_last_event");
        });
    }

    // The Gate 2E Snapshot: the current facts of each object with their provenance. Only the value column holds
    // work content, and only the fact itself: nothing can hold a before value, an actor, a reference, a payload
    // or a fingerprint. Every foreign key includes the tenant, so provenance can never point across tenants.
    private static void ConfigureSnapshot(ModelBuilder modelBuilder, ValueConverter<TenantId, Guid> tenantIdConverter)
    {
        modelBuilder.Entity<SnapshotObjectRecord>(entity =>
        {
            entity.ToTable("snapshot_objects", table => table.HasCheckConstraint("ck_snapshot_objects_version", "version >= 1"));
            entity.HasKey(record => new
            {
                record.TenantId,
                record.ObjectSystem,
                record.ObjectConnection,
                record.ObjectType,
                record.ObjectId,
            });
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter);
            entity.Property(record => record.ObjectSystem).HasColumnName("object_system").HasMaxLength(64);
            entity.Property(record => record.ObjectConnection).HasColumnName("object_connection").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.ObjectType).HasColumnName("object_type").HasMaxLength(64);
            entity.Property(record => record.ObjectId).HasColumnName("object_id").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.Version).HasColumnName("version").IsRequired();
            entity.Property(record => record.LastEventId).HasColumnName("last_event_id").IsRequired();
            entity.Property(record => record.ProcessedAtUtc).HasColumnName("processed_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.HasOne<EventLedgerRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.LastEventId })
                .HasPrincipalKey(ledger => new { ledger.TenantId, ledger.EventId })
                .HasConstraintName("fk_snapshot_objects_last_event")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(record => new { record.TenantId, record.LastEventId })
                .HasDatabaseName("ix_snapshot_objects_tenant_last_event");
        });

        modelBuilder.Entity<SnapshotFieldRecord>(entity =>
        {
            entity.ToTable("snapshot_fields", table =>
            {
                table.HasCheckConstraint("ck_snapshot_fields_versions", "field_version >= 1 AND object_version >= 1");
                table.HasCheckConstraint("ck_snapshot_fields_data_type", $"data_type IN ({DefinedNames<FactDataType>()})");
                table.HasCheckConstraint("ck_snapshot_fields_authority", $"authority IN ({DefinedNames<AuthorityBasis>()})");
                table.HasCheckConstraint("ck_snapshot_fields_source_kind", $"source_kind IN ({DefinedNames<EventSourceKind>()})");
                table.HasCheckConstraint("ck_snapshot_fields_value", "value IS NULL OR value <> ''");
            });
            entity.HasKey(record => new
            {
                record.TenantId,
                record.ObjectSystem,
                record.ObjectConnection,
                record.ObjectType,
                record.ObjectId,
                record.Field,
            });
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter);
            entity.Property(record => record.ObjectSystem).HasColumnName("object_system").HasMaxLength(64);
            entity.Property(record => record.ObjectConnection).HasColumnName("object_connection").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.ObjectType).HasColumnName("object_type").HasMaxLength(64);
            entity.Property(record => record.ObjectId).HasColumnName("object_id").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.Field).HasColumnName("field").HasMaxLength(64);
            entity.Property(record => record.DataType).HasColumnName("data_type").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.Value).HasColumnName("value").HasMaxLength(EventText.MaxValueLength);
            entity.Property(record => record.Authority).HasColumnName("authority").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.SourceKind).HasColumnName("source_kind").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.SourceSystem).HasColumnName("source_system").HasMaxLength(64).IsRequired();
            entity.Property(record => record.ConnectionReference).HasColumnName("connection_reference").HasMaxLength(EventLedgerRecord.MaxTextLength).IsRequired();
            entity.Property(record => record.LastEventId).HasColumnName("last_event_id").IsRequired();
            entity.Property(record => record.SourceEventId).HasColumnName("source_event_id").HasMaxLength(EventLedgerRecord.MaxTextLength);
            entity.Property(record => record.SourceRevision).HasColumnName("source_revision");
            entity.Property(record => record.SourceUpdatedAtUtc).HasColumnName("source_updated_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.OccurredAtUtc).HasColumnName("occurred_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.ObservedAtUtc).HasColumnName("observed_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.ProcessedAtUtc).HasColumnName("processed_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.FieldVersion).HasColumnName("field_version").IsRequired();
            entity.Property(record => record.ObjectVersion).HasColumnName("object_version").IsRequired();
            entity.HasOne<SnapshotObjectRecord>()
                .WithMany()
                .HasForeignKey(record => new
                {
                    record.TenantId,
                    record.ObjectSystem,
                    record.ObjectConnection,
                    record.ObjectType,
                    record.ObjectId,
                })
                .HasConstraintName("fk_snapshot_fields_object")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EventLedgerRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.LastEventId })
                .HasPrincipalKey(ledger => new { ledger.TenantId, ledger.EventId })
                .HasConstraintName("fk_snapshot_fields_last_event")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(record => new { record.TenantId, record.LastEventId })
                .HasDatabaseName("ix_snapshot_fields_tenant_last_event");
        });
    }

    // The defined members of a closed enumeration, quoted for a check constraint; "None" is never valid.
    private static string DefinedNames<TEnum>()
        where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetNames<TEnum>().Where(name => name != "None").Select(name => $"'{name}'"));

    private static CorrelationId ParseCorrelationId(string value) =>
        CorrelationId.TryParse(value, out var correlationId)
            ? correlationId
            : throw new InvalidOperationException(
                "A stored correlation id failed validation. The row is malformed.");
}
