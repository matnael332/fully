using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The Gate 3E reconciliation targets (decision D3E-11): one row per tenant, object and field that reconciliation
    /// rereads, with the field's source scope and its request, claim, retry and result state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive: one new table, nothing else changes. No column can hold a value, a label, a provider response, a
    /// credential or display text; the ledger events it names are referenced through foreign keys that include the tenant.
    /// </para>
    /// <para>
    /// Row-level security is enabled and forced with the standard tenant policy, and a restrictive policy refuses every
    /// delete, whatever is granted: a target is never deleted. The runtime role holds exactly what reconciliation and
    /// processing need: every privilege is revoked, including what the bootstrap's default privileges gave the new table,
    /// and read, insert and update are granted back. The global bootstrap grants DML on every table each time it runs, so
    /// a rerun after this migration re-grants DELETE; the restrictive policy keeps the guarantee regardless, and the Azure
    /// rollout re-asserts these privileges in the same approved step, as for the Gate 3B tables (R3B-2).
    /// </para>
    /// </remarks>
    public partial class ReconciliationTargets : Migration
    {
        private const string Table = "reconciliation_targets";

        private const string TenantPredicate =
            "tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "reconciliation_targets",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_connection = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    object_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    resolved_generation = table.Column<long>(type: "bigint", nullable: true),
                    request_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    requested_by_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_generation = table.Column<long>(type: "bigint", nullable: true),
                    claimed_until_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_attempted_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    resolved_by_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verified_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_targets", x => new { x.tenant_id, x.object_system, x.object_connection, x.object_type, x.object_id, x.field });
                    table.CheckConstraint("ck_reconciliation_targets_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_reconciliation_targets_claim", "(claim_id IS NULL) = (claimed_generation IS NULL) AND (claim_id IS NULL) = (claimed_until_utc IS NULL) AND (claimed_generation IS NULL OR claimed_generation BETWEEN 1 AND generation)");
                    table.CheckConstraint("ck_reconciliation_targets_data_type", "data_type IN ('TaskStatus', 'PcActivity', 'ApprovalDecision', 'IdentityAndPermissions', 'Deadline', 'ActionResult')");
                    table.CheckConstraint("ck_reconciliation_targets_generation", "generation >= 1 AND (resolved_generation IS NULL OR resolved_generation BETWEEN 1 AND generation)");
                    table.CheckConstraint("ck_reconciliation_targets_request_kind", "request_kind IN ('Event', 'Sweep')");
                    table.CheckConstraint("ck_reconciliation_targets_requester", "(request_kind = 'Sweep') = (requested_by_event_id IS NULL)");
                    table.CheckConstraint("ck_reconciliation_targets_resolved", "state <> 'Resolved' OR resolved_generation = generation");
                    table.CheckConstraint("ck_reconciliation_targets_scope", "source_scope IS NULL OR source_scope <> ''");
                    table.CheckConstraint("ck_reconciliation_targets_state", "state IN ('Pending', 'Resolved', 'Held')");
                    table.ForeignKey(
                        name: "fk_reconciliation_targets_requested_by_event",
                        columns: x => new { x.tenant_id, x.requested_by_event_id },
                        principalTable: "event_ledger",
                        principalColumns: new[] { "tenant_id", "event_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reconciliation_targets_resolved_by_event",
                        columns: x => new { x.tenant_id, x.resolved_by_event_id },
                        principalTable: "event_ledger",
                        principalColumns: new[] { "tenant_id", "event_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_targets_due",
                table: "reconciliation_targets",
                columns: new[] { "tenant_id", "object_system", "object_connection", "state", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_targets_tenant_requested_by",
                table: "reconciliation_targets",
                columns: new[] { "tenant_id", "requested_by_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_targets_tenant_resolved_by",
                table: "reconciliation_targets",
                columns: new[] { "tenant_id", "resolved_by_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_targets_verified",
                table: "reconciliation_targets",
                columns: new[] { "tenant_id", "object_system", "object_connection", "state", "verified_at_utc" });

            migrationBuilder.Sql($"ALTER TABLE {Table} ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"ALTER TABLE {Table} FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                $"""
                CREATE POLICY tenant_isolation_{Table} ON {Table}
                    USING ({TenantPredicate})
                    WITH CHECK ({TenantPredicate});
                """);
            migrationBuilder.Sql($"CREATE POLICY no_delete_{Table} ON {Table} AS RESTRICTIVE FOR DELETE USING (false);");

            // Exactly what reconciliation and processing need, whatever was granted before: every privilege of the runtime
            // role is revoked, DELETE and TRUNCATE included, then read, insert and update are granted back. Guarded like the
            // earlier grants: the runtime role is created by bootstrap, never by a migration.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        REVOKE ALL PRIVILEGES ON reconciliation_targets FROM workcontinuity_app;
                        GRANT SELECT, INSERT, UPDATE ON reconciliation_targets TO workcontinuity_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Dropping the table removes its policies, indexes and grants too, and every target with it: a rollback past this
            // migration loses which objects were reconciled and which were waiting, though no Snapshot value or ledger entry.
            migrationBuilder.DropTable(
                name: "reconciliation_targets");
        }
    }
}
