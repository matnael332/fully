using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorkContinuity.Core.Connectors;
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
            "field_ordering_states", "snapshot_objects", "snapshot_fields", "connector_authorizations",
            "connector_authorization_transactions", "reconciliation_targets", "connector_source_scopes", "connection_syncs",
            "connection_sync_scopes",
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

            // The key tenant-consistent foreign keys point at (Gate 3B): a reference to a user can never cross a tenant.
            entity.HasAlternateKey(record => new { record.TenantId, record.UserId })
                .HasName("ak_users_tenant_user");
        });

        modelBuilder.Entity<RawReceiptRecord>(entity =>
        {
            // Raw content is purged as a whole: the payload and its fingerprint are present or cleared together.
            entity.ToTable("raw_receipts", table => table.HasCheckConstraint(
                "ck_raw_receipts_content",
                "(payload IS NULL) = (payload_fingerprint IS NULL)"));
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
            // The receipt time is the database's own and the retention age basis: it defaults to now(), a trigger
            // stamps it on every insert whatever is supplied and refuses any later change, and the application never
            // writes it; the stored value is read back once the receipt is saved.
            var receivedAt = entity.Property(record => record.ReceivedAtUtc)
                .HasColumnName("received_at_utc")
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("now()")
                .ValueGeneratedOnAdd()
                .IsRequired();
            receivedAt.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            receivedAt.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

            // The local ingress sequence (Gate 3F, decision D3F-2): an identity the database generates at insert and a
            // trigger keeps from ever changing. It only fences initial sync's held receipts; it never orders business state.
            var ingressSequence = entity.Property(record => record.IngressSequence)
                .HasColumnName("ingress_sequence")
                .UseIdentityAlwaysColumn()
                .IsRequired();
            ingressSequence.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            ingressSequence.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(record => record.Payload)
                .HasColumnName("payload")
                .HasColumnType("jsonb");
            entity.Property(record => record.PayloadFingerprint)
                .HasColumnName("payload_fingerprint")
                .HasMaxLength(128);
            entity.Property(record => record.ProcessingState)
                .HasColumnName("processing_state")
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            entity.Ignore(record => record.HasContent);

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

            // Supports the per-tenant purge of expired content, oldest first; purged rows leave the index.
            entity.HasIndex(record => new { record.TenantId, record.ReceivedAtUtc })
                .HasDatabaseName("ix_raw_receipts_tenant_content_received")
                .HasFilter("payload IS NOT NULL");

            // Supports initial sync's fences and replay: a connection's receipts by ingress position.
            entity.HasIndex(record => new { record.TenantId, record.ConnectionReference, record.IngressSequence })
                .HasDatabaseName("ix_raw_receipts_tenant_connection_ingress")
                .IsUnique();
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

            // The key tenant-consistent foreign keys point at (Gate 3B): a reference to a connection can never cross a
            // tenant.
            entity.HasAlternateKey(record => new { record.TenantId, record.ConnectionId })
                .HasName("ak_connections_tenant_connection");
        });

        ConfigureEventLedger(modelBuilder, tenantIdConverter, correlationIdConverter);
        ConfigureSnapshot(modelBuilder, tenantIdConverter);
        ConfigureConnectorAuthorizations(modelBuilder, tenantIdConverter, userIdConverter);
        ConfigureReconciliation(modelBuilder, tenantIdConverter);
        ConfigureInitialSync(modelBuilder, tenantIdConverter);
    }

    // The Gate 3E reconciliation targets (decision D3E-11): identifiers, the source scope and the lifecycle only. No column
    // can hold a value, a label, a provider response, a credential or display text; every foreign key includes the tenant.
    private static void ConfigureReconciliation(ModelBuilder modelBuilder, ValueConverter<TenantId, Guid> tenantIdConverter)
    {
        modelBuilder.Entity<ReconciliationTargetRecord>(entity =>
        {
            entity.ToTable("reconciliation_targets", table =>
            {
                table.HasCheckConstraint("ck_reconciliation_targets_state", "state IN ('Pending', 'Resolved', 'Held')");
                table.HasCheckConstraint("ck_reconciliation_targets_request_kind", "request_kind IN ('Event', 'Sweep', 'Sync')");
                table.HasCheckConstraint(
                    "ck_reconciliation_targets_requester",
                    "(request_kind = 'Event') = (requested_by_event_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_reconciliation_targets_generation",
                    "generation >= 1 AND (resolved_generation IS NULL OR resolved_generation BETWEEN 1 AND generation)");
                table.HasCheckConstraint(
                    "ck_reconciliation_targets_resolved",
                    "state <> 'Resolved' OR resolved_generation = generation");
                table.HasCheckConstraint(
                    "ck_reconciliation_targets_claim",
                    "(claim_id IS NULL) = (claimed_generation IS NULL) AND (claim_id IS NULL) = (claimed_until_utc IS NULL) "
                    + "AND (claimed_generation IS NULL OR claimed_generation BETWEEN 1 AND generation)");
                table.HasCheckConstraint("ck_reconciliation_targets_attempts", "attempts >= 0");
                table.HasCheckConstraint("ck_reconciliation_targets_scope", "source_scope IS NULL OR source_scope <> ''");
                table.HasCheckConstraint("ck_reconciliation_targets_awaiting_baseline", "awaiting_baseline_version IS NULL OR awaiting_baseline_version >= 1");
                table.HasCheckConstraint("ck_reconciliation_targets_data_type", $"data_type IN ({DefinedNames<FactDataType>()})");
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
            entity.Property(record => record.SourceScope).HasColumnName("source_scope").HasMaxLength(EventText.MaxIdentifierLength);
            entity.Property(record => record.AwaitingBaselineVersion).HasColumnName("awaiting_baseline_version");
            entity.Property(record => record.State).HasColumnName("state").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.Generation).HasColumnName("generation").IsRequired();
            entity.Property(record => record.ResolvedGeneration).HasColumnName("resolved_generation");
            entity.Property(record => record.RequestKind).HasColumnName("request_kind").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.RequestedAtUtc).HasColumnName("requested_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.RequestedByEventId).HasColumnName("requested_by_event_id");
            entity.Property(record => record.ClaimId).HasColumnName("claim_id");
            entity.Property(record => record.ClaimedGeneration).HasColumnName("claimed_generation");
            entity.Property(record => record.ClaimedUntilUtc).HasColumnName("claimed_until_utc").HasColumnType("timestamptz");
            entity.Property(record => record.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(record => record.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.LastAttemptedAtUtc).HasColumnName("last_attempted_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.LastOutcome).HasColumnName("last_outcome").HasMaxLength(ReconciliationTargetRecord.MaxOutcomeLength);
            entity.Property(record => record.ResolvedAtUtc).HasColumnName("resolved_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.ResolvedByEventId).HasColumnName("resolved_by_event_id");
            entity.Property(record => record.VerifiedAtUtc).HasColumnName("verified_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.HasOne<EventLedgerRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.RequestedByEventId })
                .HasPrincipalKey(ledger => new { ledger.TenantId, ledger.EventId })
                .HasConstraintName("fk_reconciliation_targets_requested_by_event")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EventLedgerRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.ResolvedByEventId })
                .HasPrincipalKey(ledger => new { ledger.TenantId, ledger.EventId })
                .HasConstraintName("fk_reconciliation_targets_resolved_by_event")
                .OnDelete(DeleteBehavior.Restrict);

            // Claiming finds a connection's due targets; a sweep finds its least recently verified resolved ones.
            entity.HasIndex(record => new { record.TenantId, record.ObjectSystem, record.ObjectConnection, record.State, record.NextAttemptAtUtc })
                .HasDatabaseName("ix_reconciliation_targets_due");
            entity.HasIndex(record => new { record.TenantId, record.ObjectSystem, record.ObjectConnection, record.State, record.VerifiedAtUtc })
                .HasDatabaseName("ix_reconciliation_targets_verified");
            entity.HasIndex(record => new { record.TenantId, record.RequestedByEventId })
                .HasDatabaseName("ix_reconciliation_targets_tenant_requested_by");
            entity.HasIndex(record => new { record.TenantId, record.ResolvedByEventId })
                .HasDatabaseName("ix_reconciliation_targets_tenant_resolved_by");
        });
    }

    // Gate 3F initial sync: configured source scopes, each connection's sync state and each generation's scope progress.
    // Identifiers, flags, fences, counts and outcome codes only: no column can hold a value, a label, a continuation, a
    // credential or a provider response. Every foreign key includes the tenant.
    private static void ConfigureInitialSync(ModelBuilder modelBuilder, ValueConverter<TenantId, Guid> tenantIdConverter)
    {
        modelBuilder.Entity<ConnectorSourceScopeRecord>(entity =>
        {
            entity.ToTable("connector_source_scopes", table =>
            {
                table.HasCheckConstraint("ck_connector_source_scopes_source", "source_system <> ''");
                table.HasCheckConstraint("ck_connector_source_scopes_resource", "resource_id <> ''");

                // A disabled scope never has a current baseline.
                table.HasCheckConstraint(
                    "ck_connector_source_scopes_baseline",
                    "baseline_generation IS NULL OR (enabled AND baseline_generation >= 1)");
            });
            entity.HasKey(record => new { record.TenantId, record.ConnectionId, record.ResourceId });
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter);
            entity.Property(record => record.ConnectionId).HasColumnName("connection_id");
            entity.Property(record => record.SourceSystem).HasColumnName("source_system").HasMaxLength(64).IsRequired();
            entity.Property(record => record.ResourceId).HasColumnName("resource_id").HasMaxLength(EventText.MaxIdentifierLength);
            entity.Property(record => record.Enabled).HasColumnName("enabled").IsRequired();
            entity.Property(record => record.BaselineGeneration).HasColumnName("baseline_generation");
            entity.Property(record => record.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.HasOne<ConnectionRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.ConnectionId })
                .HasPrincipalKey(connection => new { connection.TenantId, connection.ConnectionId })
                .HasConstraintName("fk_connector_source_scopes_connection")
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConnectionSyncRecord>(entity =>
        {
            entity.ToTable("connection_syncs", table =>
            {
                table.HasCheckConstraint(
                    "ck_connection_syncs_state",
                    "state IN ('NotStarted', 'Buffering', 'Reading', 'Replaying', 'Live', 'Failed', 'RequiresRestart')");
                table.HasCheckConstraint("ck_connection_syncs_generation", "generation >= 0 AND (state = 'NotStarted') = (generation = 0)");

                // A connection not yet started has no generation, so no authority, start or scope-set version of one.
                table.HasCheckConstraint(
                    "ck_connection_syncs_not_started",
                    "(state = 'NotStarted') = (provider_subject IS NULL) AND (state = 'NotStarted') = (authorized_at_utc IS NULL) "
                    + "AND (state = 'NotStarted') = (started_at_utc IS NULL) AND (state = 'NotStarted') = (generation_scope_version IS NULL)");

                // A generation reads one version of the scope set, and is Live only while that version is current.
                table.HasCheckConstraint(
                    "ck_connection_syncs_scope_version",
                    "scope_version >= 1 AND (generation_scope_version IS NULL OR generation_scope_version BETWEEN 1 AND scope_version) "
                    + "AND (state <> 'Live' OR generation_scope_version = scope_version)");
                table.HasCheckConstraint(
                    "ck_connection_syncs_fences",
                    "replay_floor >= 0 AND replay_floor <= buffer_start_fence "
                    + "AND (replay_fence IS NULL OR replay_fence >= replay_floor) "
                    + "AND (cutover_fence IS NULL OR cutover_fence >= COALESCE(replay_fence, replay_floor))");
                table.HasCheckConstraint(
                    "ck_connection_syncs_live",
                    "(state = 'Live') = (cutover_fence IS NOT NULL AND live_at_utc IS NOT NULL)");
                table.HasCheckConstraint("ck_connection_syncs_claim", "(claim_id IS NULL) = (claimed_until_utc IS NULL)");
                table.HasCheckConstraint("ck_connection_syncs_attempts", "attempts >= 0");
                table.HasCheckConstraint("ck_connection_syncs_subject", "provider_subject <> ''");
            });
            entity.HasKey(record => new { record.TenantId, record.ConnectionId });
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter);
            entity.Property(record => record.ConnectionId).HasColumnName("connection_id");
            entity.Property(record => record.SourceSystem).HasColumnName("source_system").HasMaxLength(64).IsRequired();
            entity.Property(record => record.Generation).HasColumnName("generation").IsRequired();
            entity.Property(record => record.State).HasColumnName("state").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.ProviderSubject).HasColumnName("provider_subject").HasMaxLength(EventText.MaxIdentifierLength);
            entity.Property(record => record.AuthorizedAtUtc).HasColumnName("authorized_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.ScopeVersion).HasColumnName("scope_version").IsRequired();
            entity.Property(record => record.GenerationScopeVersion).HasColumnName("generation_scope_version");
            entity.Property(record => record.ReplayFloor).HasColumnName("replay_floor").IsRequired();
            entity.Property(record => record.BufferStartFence).HasColumnName("buffer_start_fence").IsRequired();
            entity.Property(record => record.ReplayFence).HasColumnName("replay_fence");
            entity.Property(record => record.CutoverFence).HasColumnName("cutover_fence");
            entity.Property(record => record.ClaimId).HasColumnName("claim_id");
            entity.Property(record => record.ClaimedUntilUtc).HasColumnName("claimed_until_utc").HasColumnType("timestamptz");
            entity.Property(record => record.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(record => record.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.LastOutcome).HasColumnName("last_outcome").HasMaxLength(ConnectionSyncRecord.MaxOutcomeLength);
            entity.Property(record => record.StartedAtUtc).HasColumnName("started_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.LiveAtUtc).HasColumnName("live_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.HasOne<ConnectionRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.ConnectionId })
                .HasPrincipalKey(connection => new { connection.TenantId, connection.ConnectionId })
                .HasConstraintName("fk_connection_syncs_connection")
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConnectionSyncScopeRecord>(entity =>
        {
            entity.ToTable("connection_sync_scopes", table =>
            {
                table.HasCheckConstraint("ck_connection_sync_scopes_state", "state IN ('Pending', 'Completed', 'Held')");
                table.HasCheckConstraint("ck_connection_sync_scopes_generation", "generation >= 1");
                table.HasCheckConstraint(
                    "ck_connection_sync_scopes_counts",
                    "attempts >= 0 AND pages_read >= 0 AND objects_read >= 0 AND fields_applied >= 0");
                table.HasCheckConstraint(
                    "ck_connection_sync_scopes_completed",
                    "(state = 'Completed') = (completed_at_utc IS NOT NULL)");
            });
            entity.HasKey(record => new { record.TenantId, record.ConnectionId, record.Generation, record.ResourceId });
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter);
            entity.Property(record => record.ConnectionId).HasColumnName("connection_id");
            entity.Property(record => record.Generation).HasColumnName("generation");
            entity.Property(record => record.ResourceId).HasColumnName("resource_id").HasMaxLength(EventText.MaxIdentifierLength);
            entity.Property(record => record.State).HasColumnName("state").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(record => record.LastOutcome).HasColumnName("last_outcome").HasMaxLength(ConnectionSyncRecord.MaxOutcomeLength);
            entity.Property(record => record.PagesRead).HasColumnName("pages_read").IsRequired();
            entity.Property(record => record.ObjectsRead).HasColumnName("objects_read").IsRequired();
            entity.Property(record => record.FieldsApplied).HasColumnName("fields_applied").IsRequired();
            entity.Property(record => record.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("timestamptz");

            // A generation's scopes are its connection's configured scopes, and belong to its sync row.
            entity.HasIndex(record => new { record.TenantId, record.ConnectionId, record.ResourceId })
                .HasDatabaseName("ix_connection_sync_scopes_scope");
            entity.HasOne<ConnectorSourceScopeRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.ConnectionId, record.ResourceId })
                .HasPrincipalKey(scope => new { scope.TenantId, scope.ConnectionId, scope.ResourceId })
                .HasConstraintName("fk_connection_sync_scopes_scope")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ConnectionSyncRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.ConnectionId })
                .HasPrincipalKey(sync => new { sync.TenantId, sync.ConnectionId })
                .HasConstraintName("fk_connection_sync_scopes_sync")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    // The Gate 3B authorization lifecycle: delegation metadata only. No column can hold a token, a client secret or a
    // PKCE verifier (decision D3B-2); hashes, ids, times, scope names, states and an opaque material version only. Every
    // foreign key includes the tenant, so neither table can name another tenant's user or connection.
    private static void ConfigureConnectorAuthorizations(
        ModelBuilder modelBuilder,
        ValueConverter<TenantId, Guid> tenantIdConverter,
        ValueConverter<UserId, Guid> userIdConverter)
    {
        modelBuilder.Entity<ConnectorAuthorizationRecord>(entity =>
        {
            entity.ToTable("connector_authorizations", table =>
            {
                table.HasCheckConstraint(
                    "ck_connector_authorizations_state",
                    "state IN ('Active', 'ReauthorizationRequired', 'Revoked')");
                table.HasCheckConstraint("ck_connector_authorizations_generation", "generation >= 1");
                table.HasCheckConstraint(
                    "ck_connector_authorizations_identifiers",
                    "provider_subject <> '' AND provider_account <> '' AND granted_scopes <> ''");
                table.HasCheckConstraint(
                    "ck_connector_authorizations_claim",
                    "(refresh_claim_id IS NULL) = (refresh_claimed_at_utc IS NULL)");
                table.HasCheckConstraint(
                    "ck_connector_authorizations_revoked",
                    "(state = 'Revoked') = (revoked_at_utc IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_connector_authorizations_reauthorization",
                    "state <> 'ReauthorizationRequired' OR reauthorization_required_at_utc IS NOT NULL");
                table.HasCheckConstraint(
                    "ck_connector_authorizations_revocation_confirmed",
                    "provider_revocation_confirmed IS NULL OR state = 'Revoked'");
            });
            entity.HasKey(record => record.ConnectionId);
            entity.Property(record => record.ConnectionId).HasColumnName("connection_id").ValueGeneratedNever();
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter).IsRequired();
            entity.Property(record => record.CanonicalUserId).HasColumnName("canonical_user_id").HasConversion(userIdConverter).IsRequired();
            entity.Property(record => record.State).HasColumnName("state").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.ProviderSubject).HasColumnName("provider_subject").HasMaxLength(ConnectorAuthorizationRecord.MaxProviderIdentifierLength).IsRequired();
            entity.Property(record => record.ProviderAccount).HasColumnName("provider_account").HasMaxLength(ConnectorAuthorizationRecord.MaxProviderIdentifierLength).IsRequired();
            entity.Property(record => record.Generation).HasColumnName("generation").IsRequired();
            entity.Property(record => record.MaterialVersion).HasColumnName("material_version").IsRequired();
            entity.Property(record => record.AccessExpiresAtUtc).HasColumnName("access_expires_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.GrantedScopes).HasColumnName("granted_scopes").HasMaxLength(ConnectorScopes.MaxLength).IsRequired();
            entity.Property(record => record.RefreshClaimId).HasColumnName("refresh_claim_id");
            entity.Property(record => record.RefreshClaimedAtUtc).HasColumnName("refresh_claimed_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.AuthorizedAtUtc).HasColumnName("authorized_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.RefreshedAtUtc).HasColumnName("refreshed_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.ReauthorizationRequiredAtUtc).HasColumnName("reauthorization_required_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.RevokedAtUtc).HasColumnName("revoked_at_utc").HasColumnType("timestamptz");
            entity.Property(record => record.ProviderRevocationConfirmed).HasColumnName("provider_revocation_confirmed");
            entity.HasOne<ConnectionRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.ConnectionId })
                .HasPrincipalKey(connection => new { connection.TenantId, connection.ConnectionId })
                .HasConstraintName("fk_connector_authorizations_connection")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UserRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.CanonicalUserId })
                .HasPrincipalKey(user => new { user.TenantId, user.UserId })
                .HasConstraintName("fk_connector_authorizations_user")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(record => new { record.TenantId, record.ConnectionId })
                .HasDatabaseName("ix_connector_authorizations_tenant_connection");
            entity.HasIndex(record => new { record.TenantId, record.CanonicalUserId })
                .HasDatabaseName("ix_connector_authorizations_tenant_user");
        });

        modelBuilder.Entity<ConnectorAuthorizationTransactionRecord>(entity =>
        {
            entity.ToTable("connector_authorization_transactions", table =>
            {
                table.HasCheckConstraint(
                    "ck_connector_authorization_transactions_purpose",
                    "purpose IN ('Connect', 'Reauthorize')");
                table.HasCheckConstraint(
                    "ck_connector_authorization_transactions_hashes",
                    "octet_length(state_hash) = 32 AND octet_length(redirect_hash) = 32");
                table.HasCheckConstraint(
                    "ck_connector_authorization_transactions_lifetime",
                    "expires_at_utc > created_at_utc AND expires_at_utc <= created_at_utc + interval '10 minutes'");
                table.HasCheckConstraint(
                    "ck_connector_authorization_transactions_consumed",
                    "consumed_at_utc IS NULL OR consumed_at_utc >= created_at_utc");
                table.HasCheckConstraint("ck_connector_authorization_transactions_scopes", "requested_scopes <> ''");
            });
            entity.HasKey(record => record.TransactionId);
            entity.Property(record => record.TransactionId).HasColumnName("transaction_id").ValueGeneratedNever();
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasConversion(tenantIdConverter).IsRequired();
            entity.Property(record => record.CanonicalUserId).HasColumnName("canonical_user_id").HasConversion(userIdConverter).IsRequired();
            entity.Property(record => record.ConnectionId).HasColumnName("connection_id").IsRequired();
            entity.Property(record => record.SourceSystem).HasColumnName("source_system").HasMaxLength(64).IsRequired();
            entity.Property(record => record.Purpose).HasColumnName("purpose").HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(record => record.StateHash).HasColumnName("state_hash").IsRequired();
            entity.Property(record => record.RedirectHash).HasColumnName("redirect_hash").IsRequired();
            entity.Property(record => record.RequestedScopes).HasColumnName("requested_scopes").HasMaxLength(ConnectorScopes.MaxLength).IsRequired();
            entity.Property(record => record.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.ExpiresAtUtc).HasColumnName("expires_at_utc").HasColumnType("timestamptz").IsRequired();
            entity.Property(record => record.ConsumedAtUtc).HasColumnName("consumed_at_utc").HasColumnType("timestamptz");
            entity.HasOne<UserRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.TenantId, record.CanonicalUserId })
                .HasPrincipalKey(user => new { user.TenantId, user.UserId })
                .HasConstraintName("fk_connector_authorization_transactions_user")
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(record => new { record.TenantId, record.CanonicalUserId })
                .HasDatabaseName("ix_connector_authorization_transactions_tenant_user");

            // Finds a tenant's expired, still-open transactions for closing.
            entity.HasIndex(record => new { record.TenantId, record.ExpiresAtUtc })
                .HasDatabaseName("ix_connector_authorization_transactions_open")
                .HasFilter("consumed_at_utc IS NULL");
        });
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
