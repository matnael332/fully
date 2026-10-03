using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Gate 3F initial sync: the local ingress sequence (decision D3F-2), configured source scopes (D3F-1), each
    /// connection's sync state and each generation's scope progress (D3F-4), and the <c>Sync</c> request kind of
    /// reconciliation targets (D3F-8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive. <c>raw_receipts.ingress_sequence</c> is an identity the database generates at insert, existing rows
    /// included, and a trigger refuses every later change; it fences which receipts initial sync holds and replays and
    /// never orders business state. The new tables hold identifiers, flags, fences, counts and outcome codes only: no
    /// value, label, continuation, credential or provider response. Every foreign key includes the tenant.
    /// </para>
    /// <para>
    /// Row-level security is enabled and forced on each new table with the standard tenant policy, and a restrictive policy
    /// refuses every delete. The runtime role holds exactly what initial sync needs: every privilege is revoked, then read
    /// and insert are granted back, and update only on the columns that may change, so a source scope can never move to
    /// another connection or resource, and a sync row never changes tenant, connection or source or advances its replay
    /// floor, which is set once, when the connection's first generation starts. The global bootstrap grants DML on every
    /// table each time it runs, so a rerun after this migration re-grants DELETE and full UPDATE; the restrictive policies
    /// keep the no-delete guarantee regardless, and the Azure rollout re-asserts these privileges in the same approved step,
    /// as for the Gate 3B and 3E tables (R3B-2).
    /// </para>
    /// </remarks>
    public partial class InitialSync : Migration
    {
        private const string TenantPredicate =
            "tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid";

        private static readonly string[] NewTables = ["connector_source_scopes", "connection_syncs", "connection_sync_scopes"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliation_targets_request_kind",
                table: "reconciliation_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliation_targets_requester",
                table: "reconciliation_targets");

            // The identity numbers existing receipts too, so every receipt has a position.
            migrationBuilder.AddColumn<long>(
                name: "ingress_sequence",
                table: "raw_receipts",
                type: "bigint",
                nullable: false)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            migrationBuilder.CreateTable(
                name: "connection_syncs",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    authorized_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    replay_floor = table.Column<long>(type: "bigint", nullable: false),
                    buffer_start_fence = table.Column<long>(type: "bigint", nullable: false),
                    replay_fence = table.Column<long>(type: "bigint", nullable: true),
                    cutover_fence = table.Column<long>(type: "bigint", nullable: true),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_until_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    live_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_syncs", x => new { x.tenant_id, x.connection_id });
                    table.CheckConstraint("ck_connection_syncs_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_connection_syncs_claim", "(claim_id IS NULL) = (claimed_until_utc IS NULL)");
                    table.CheckConstraint("ck_connection_syncs_fences", "replay_floor >= 0 AND replay_floor <= buffer_start_fence AND (replay_fence IS NULL OR replay_fence >= replay_floor) AND (cutover_fence IS NULL OR cutover_fence >= COALESCE(replay_fence, replay_floor))");
                    table.CheckConstraint("ck_connection_syncs_generation", "generation >= 1");
                    table.CheckConstraint("ck_connection_syncs_live", "(state = 'Live') = (cutover_fence IS NOT NULL AND live_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_connection_syncs_state", "state IN ('Buffering', 'Reading', 'Replaying', 'Live', 'Failed', 'RequiresRestart')");
                    table.CheckConstraint("ck_connection_syncs_subject", "provider_subject <> ''");
                    table.ForeignKey(
                        name: "fk_connection_syncs_connection",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "connector_source_scopes",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_source_scopes", x => new { x.tenant_id, x.connection_id, x.resource_id });
                    table.CheckConstraint("ck_connector_source_scopes_resource", "resource_id <> ''");
                    table.CheckConstraint("ck_connector_source_scopes_source", "source_system <> ''");
                    table.ForeignKey(
                        name: "fk_connector_source_scopes_connection",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "connection_sync_scopes",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    resource_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    pages_read = table.Column<int>(type: "integer", nullable: false),
                    objects_read = table.Column<int>(type: "integer", nullable: false),
                    fields_applied = table.Column<int>(type: "integer", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_sync_scopes", x => new { x.tenant_id, x.connection_id, x.generation, x.resource_id });
                    table.CheckConstraint("ck_connection_sync_scopes_completed", "(state = 'Completed') = (completed_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_connection_sync_scopes_counts", "attempts >= 0 AND pages_read >= 0 AND objects_read >= 0 AND fields_applied >= 0");
                    table.CheckConstraint("ck_connection_sync_scopes_generation", "generation >= 1");
                    table.CheckConstraint("ck_connection_sync_scopes_state", "state IN ('Pending', 'Completed', 'Held')");
                    table.ForeignKey(
                        name: "fk_connection_sync_scopes_scope",
                        columns: x => new { x.tenant_id, x.connection_id, x.resource_id },
                        principalTable: "connector_source_scopes",
                        principalColumns: new[] { "tenant_id", "connection_id", "resource_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_connection_sync_scopes_sync",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connection_syncs",
                        principalColumns: new[] { "tenant_id", "connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliation_targets_request_kind",
                table: "reconciliation_targets",
                sql: "request_kind IN ('Event', 'Sweep', 'Sync')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliation_targets_requester",
                table: "reconciliation_targets",
                sql: "(request_kind = 'Event') = (requested_by_event_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_raw_receipts_tenant_connection_ingress",
                table: "raw_receipts",
                columns: new[] { "tenant_id", "connection_reference", "ingress_sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_connection_sync_scopes_scope",
                table: "connection_sync_scopes",
                columns: new[] { "tenant_id", "connection_id", "resource_id" });

            // An ingress position is given once, at insert, and never changes: not even to a new identity value.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.keep_ingress_sequence()
                    RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path = pg_catalog, pg_temp
                AS $function$
                BEGIN
                    IF NEW.ingress_sequence IS DISTINCT FROM OLD.ingress_sequence THEN
                        RAISE EXCEPTION 'An ingress position is given by the database once and never changes.'
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'keep_ingress_sequence_raw_receipts';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION public.keep_ingress_sequence() FROM PUBLIC;");
            migrationBuilder.Sql(
                """
                CREATE TRIGGER keep_ingress_sequence_raw_receipts
                    BEFORE UPDATE OF ingress_sequence ON public.raw_receipts
                    FOR EACH ROW
                    EXECUTE FUNCTION public.keep_ingress_sequence();
                """);

            foreach (var table in NewTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"""
                    CREATE POLICY tenant_isolation_{table} ON {table}
                        USING ({TenantPredicate})
                        WITH CHECK ({TenantPredicate});
                    """);
                migrationBuilder.Sql($"CREATE POLICY no_delete_{table} ON {table} AS RESTRICTIVE FOR DELETE USING (false);");
            }

            // Exactly what initial sync needs, whatever was granted before: every privilege of the runtime role is revoked,
            // DELETE and TRUNCATE included, then read and insert are granted back, and update only on the columns that may
            // change. Guarded like the earlier grants: the runtime role is created by bootstrap, never by a migration.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        REVOKE ALL PRIVILEGES ON connector_source_scopes FROM workcontinuity_app;
                        GRANT SELECT, INSERT ON connector_source_scopes TO workcontinuity_app;
                        GRANT UPDATE (enabled, updated_at_utc) ON connector_source_scopes TO workcontinuity_app;

                        REVOKE ALL PRIVILEGES ON connection_syncs FROM workcontinuity_app;
                        GRANT SELECT, INSERT ON connection_syncs TO workcontinuity_app;
                        GRANT UPDATE (generation, state, provider_subject, authorized_at_utc, buffer_start_fence,
                            replay_fence, cutover_fence, claim_id, claimed_until_utc, attempts, next_attempt_at_utc, last_outcome,
                            started_at_utc, live_at_utc, updated_at_utc) ON connection_syncs TO workcontinuity_app;

                        REVOKE ALL PRIVILEGES ON connection_sync_scopes FROM workcontinuity_app;
                        GRANT SELECT, INSERT ON connection_sync_scopes TO workcontinuity_app;
                        GRANT UPDATE (state, attempts, last_outcome, pages_read, objects_read, fields_applied, completed_at_utc)
                            ON connection_sync_scopes TO workcontinuity_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Dropping the tables removes their policies, indexes and grants, and every sync state and configured scope with
            // them: a rollback past this migration loses which connections were synced, though no Snapshot value or ledger
            // entry. The reconciliation-target constraints can be restored only once no target has the Sync kind; a forward
            // fix is preferred.
            migrationBuilder.Sql("DROP TRIGGER keep_ingress_sequence_raw_receipts ON public.raw_receipts;");
            migrationBuilder.Sql("DROP FUNCTION public.keep_ingress_sequence();");

            migrationBuilder.DropTable(
                name: "connection_sync_scopes");

            migrationBuilder.DropTable(
                name: "connector_source_scopes");

            migrationBuilder.DropTable(
                name: "connection_syncs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliation_targets_request_kind",
                table: "reconciliation_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliation_targets_requester",
                table: "reconciliation_targets");

            migrationBuilder.DropIndex(
                name: "ix_raw_receipts_tenant_connection_ingress",
                table: "raw_receipts");

            migrationBuilder.DropColumn(
                name: "ingress_sequence",
                table: "raw_receipts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliation_targets_request_kind",
                table: "reconciliation_targets",
                sql: "request_kind IN ('Event', 'Sweep')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliation_targets_requester",
                table: "reconciliation_targets",
                sql: "(request_kind = 'Sweep') = (requested_by_event_id IS NULL)");
        }
    }
}
