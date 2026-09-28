using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the durable event ledger and the per-field ordering state of Gate 2D.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>event_ledger</c> keeps one entry per processed receipt: identity, provenance, ordering and deduplication
    /// metadata and the outcome. <c>event_ledger_fields</c> keeps each changed field's name, data type and
    /// decision. <c>field_ordering_states</c> keeps, per object and field, the ordering evidence of the last
    /// accepted change, which is what lets a delayed older event be recognised as older. None of them has a column
    /// that could hold a value, an actor, a reference, a payload or a fingerprint: raw content stays in
    /// <c>raw_receipts</c> and expires with it (Gate 2 owner decision 5).
    /// </para>
    /// <para>
    /// All three tables are tenant-owned and get exactly the row-level security of the other tenant-owned tables:
    /// enabled, forced, and the same tenant policy. Their foreign keys include the tenant, against the ledger's
    /// (tenant, event) key, because PostgreSQL checks foreign keys without row-level security: a field or an
    /// ordering state can never point at another tenant's event. The two ledger tables are append-only:
    /// restrictive policies refuse every update and delete, whoever holds the privilege, so the ledger cannot be
    /// rewritten even if a later bootstrap re-grants broad table privileges. The runtime role is granted only what
    /// processing needs, and no tenant-less function is added.
    /// </para>
    /// <para>
    /// One additive index on <c>raw_receipts</c> serves the duplicate-candidate lookup for deliveries without a
    /// provider event id.
    /// </para>
    /// </remarks>
    public partial class EventLedger : Migration
    {
        private const string TenantPredicate =
            "tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid";

        private static readonly string[] LedgerTables = ["event_ledger", "event_ledger_fields", "field_ordering_states"];

        private static readonly string[] AppendOnlyTables = ["event_ledger", "event_ledger_fields"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "event_ledger",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    connection_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    schema_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    object_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    object_connection = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    object_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    object_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_revision = table.Column<long>(type: "bigint", nullable: true),
                    source_updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quarantine_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    reason_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    duplicate_candidate_of = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_ledger", x => x.event_id);
                    table.UniqueConstraint("ak_event_ledger_tenant_event", x => new { x.tenant_id, x.event_id });
                    table.CheckConstraint("ck_event_ledger_normalized", "outcome = 'Quarantined' OR (event_type IS NOT NULL AND schema_version IS NOT NULL AND object_system IS NOT NULL AND object_connection IS NOT NULL AND object_type IS NOT NULL AND object_id IS NOT NULL)");
                    table.CheckConstraint("ck_event_ledger_outcome", "outcome IN ('AcceptedForProjection', 'Stale', 'ReconciliationRequired', 'Quarantined')");
                    table.CheckConstraint("ck_event_ledger_quarantine_reason", "(outcome = 'Quarantined') = (quarantine_reason IS NOT NULL)");
                    table.CheckConstraint("ck_event_ledger_reason_code", "(outcome IN ('Quarantined', 'ReconciliationRequired')) = (reason_code IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "event_ledger_fields",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_ledger_fields", x => new { x.event_id, x.field });
                    table.CheckConstraint("ck_event_ledger_fields_decision", "decision IN ('Advance', 'Stale', 'Ambiguous', 'Impossible', 'Withheld')");
                    table.ForeignKey(
                        name: "fk_event_ledger_fields_event",
                        columns: x => new { x.tenant_id, x.event_id },
                        principalTable: "event_ledger",
                        principalColumns: new[] { "tenant_id", "event_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_ordering_states",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_connection = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    object_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: true),
                    source_updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    source_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_ordering_states", x => new { x.tenant_id, x.object_system, x.object_connection, x.object_type, x.object_id, x.field });
                    table.ForeignKey(
                        name: "fk_field_ordering_states_last_event",
                        columns: x => new { x.tenant_id, x.last_event_id },
                        principalTable: "event_ledger",
                        principalColumns: new[] { "tenant_id", "event_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_raw_receipts_tenant_connection_fingerprint",
                table: "raw_receipts",
                columns: new[] { "tenant_id", "connection_reference", "payload_fingerprint" },
                filter: "source_event_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_event_ledger_tenant_connection_source_event",
                table: "event_ledger",
                columns: new[] { "tenant_id", "connection_reference", "source_event_id" },
                unique: true,
                filter: "source_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_event_ledger_tenant_receipt",
                table: "event_ledger",
                columns: new[] { "tenant_id", "receipt_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_event_ledger_fields_tenant_event",
                table: "event_ledger_fields",
                columns: new[] { "tenant_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_field_ordering_states_tenant_last_event",
                table: "field_ordering_states",
                columns: new[] { "tenant_id", "last_event_id" });

            foreach (var table in LedgerTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"""
                    CREATE POLICY tenant_isolation_{table} ON {table}
                        USING ({TenantPredicate})
                        WITH CHECK ({TenantPredicate});
                    """);
            }

            // Restrictive policies are ANDed with the tenant policy, so no row of the ledger can be updated or
            // deleted by anyone row-level security applies to, whatever table privileges they hold.
            foreach (var table in AppendOnlyTables)
            {
                migrationBuilder.Sql($"CREATE POLICY append_only_{table}_update ON {table} AS RESTRICTIVE FOR UPDATE USING (false);");
                migrationBuilder.Sql($"CREATE POLICY append_only_{table}_delete ON {table} AS RESTRICTIVE FOR DELETE USING (false);");
            }

            // Ordering state advances but is never deleted: forgetting a field's evidence would let a delayed older
            // event be accepted again as if it were the first.
            migrationBuilder.Sql("CREATE POLICY no_delete_field_ordering_states ON field_ordering_states AS RESTRICTIVE FOR DELETE USING (false);");

            // Guarded like the earlier grants: the runtime role is created by bootstrap, never by a migration.
            // Only what processing needs: the ledger is read and appended to; ordering state is also updated. The
            // bootstrap grants DML on every table, so a later bootstrap run re-grants what is revoked here; the
            // restrictive policies above keep the guarantees regardless, and TRUNCATE is never granted.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        GRANT SELECT, INSERT ON event_ledger, event_ledger_fields TO workcontinuity_app;
                        GRANT SELECT, INSERT, UPDATE ON field_ordering_states TO workcontinuity_app;
                        REVOKE UPDATE, DELETE, TRUNCATE ON event_ledger, event_ledger_fields FROM workcontinuity_app;
                        REVOKE DELETE, TRUNCATE ON field_ordering_states FROM workcontinuity_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Dropping the tables removes their policies, indexes and grants too. It also deletes the ledger and
            // the ordering state: a rollback past this migration loses that data.
            migrationBuilder.DropTable(
                name: "event_ledger_fields");

            migrationBuilder.DropTable(
                name: "field_ordering_states");

            migrationBuilder.DropTable(
                name: "event_ledger");

            migrationBuilder.DropIndex(
                name: "ix_raw_receipts_tenant_connection_fingerprint",
                table: "raw_receipts");
        }
    }
}
