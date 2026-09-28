using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the Snapshot of Gate 2E: each work object's version and the current fact of each of its fields, with
    /// the provenance that makes it a fact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>snapshot_objects</c> keeps one row per tenant and object: its version, which counts the accepted events
    /// projected onto it, and the event that produced it. <c>snapshot_fields</c> keeps one row per tenant, object
    /// and field. It holds the current value, the data type and the authority rule that made the source
    /// authoritative. It also holds the source, the canonical event, the provider event id and revision, and the
    /// source-updated, occurrence, observed and processed times, which are what freshness is derived from. Two
    /// versions complete it: the field's own, and the object version at which the field last changed. Only
    /// <c>value</c> holds work content, and only the fact: no column can hold a before value, an actor, a
    /// reference, a payload or a fingerprint. The Snapshot is kept while it is needed (PRD §12); it is not raw
    /// payload retention.
    /// </para>
    /// <para>
    /// Both tables are tenant-owned and get exactly the row-level security of the other tenant-owned tables:
    /// enabled, forced, and the same tenant policy. Every foreign key includes the tenant, because PostgreSQL
    /// checks foreign keys without row-level security. A field therefore always belongs to its own tenant's
    /// object, and neither table can name another tenant's ledger event, even for the schema owner. Rows are
    /// updated in place and never deleted: restrictive policies refuse every delete, whatever privileges a later
    /// bootstrap re-grants. The runtime role is granted only what projection needs, and no function is added.
    /// </para>
    /// </remarks>
    public partial class SnapshotProjection : Migration
    {
        private const string TenantPredicate =
            "tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid";

        private static readonly string[] SnapshotTables = ["snapshot_objects", "snapshot_fields"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "snapshot_objects",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_connection = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    object_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    last_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshot_objects", x => new { x.tenant_id, x.object_system, x.object_connection, x.object_type, x.object_id });
                    table.CheckConstraint("ck_snapshot_objects_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_snapshot_objects_last_event",
                        columns: x => new { x.tenant_id, x.last_event_id },
                        principalTable: "event_ledger",
                        principalColumns: new[] { "tenant_id", "event_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_fields",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_connection = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    object_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    authority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    connection_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    last_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_revision = table.Column<long>(type: "bigint", nullable: true),
                    source_updated_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    observed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    field_version = table.Column<long>(type: "bigint", nullable: false),
                    object_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snapshot_fields", x => new { x.tenant_id, x.object_system, x.object_connection, x.object_type, x.object_id, x.field });
                    table.CheckConstraint("ck_snapshot_fields_authority", "authority IN ('SourceTaskSystem', 'LocalAgent', 'Employee', 'OrganizationIdentitySystem', 'DefiningSystem', 'AuthoritativeDestination')");
                    table.CheckConstraint("ck_snapshot_fields_data_type", "data_type IN ('TaskStatus', 'PcActivity', 'ApprovalDecision', 'IdentityAndPermissions', 'Deadline', 'ActionResult')");
                    table.CheckConstraint("ck_snapshot_fields_source_kind", "source_kind IN ('SaasConnector', 'LocalAgent', 'Employee', 'IdentityProvider', 'ActionExecutor')");
                    table.CheckConstraint("ck_snapshot_fields_value", "value IS NULL OR value <> ''");
                    table.CheckConstraint("ck_snapshot_fields_versions", "field_version >= 1 AND object_version >= 1");
                    table.ForeignKey(
                        name: "fk_snapshot_fields_last_event",
                        columns: x => new { x.tenant_id, x.last_event_id },
                        principalTable: "event_ledger",
                        principalColumns: new[] { "tenant_id", "event_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_snapshot_fields_object",
                        columns: x => new { x.tenant_id, x.object_system, x.object_connection, x.object_type, x.object_id },
                        principalTable: "snapshot_objects",
                        principalColumns: new[] { "tenant_id", "object_system", "object_connection", "object_type", "object_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_fields_tenant_last_event",
                table: "snapshot_fields",
                columns: new[] { "tenant_id", "last_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_objects_tenant_last_event",
                table: "snapshot_objects",
                columns: new[] { "tenant_id", "last_event_id" });

            foreach (var table in SnapshotTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"""
                    CREATE POLICY tenant_isolation_{table} ON {table}
                        USING ({TenantPredicate})
                        WITH CHECK ({TenantPredicate});
                    """);

                // Restrictive, so ANDed with the tenant policy: no Snapshot row is ever deleted by anyone row-level
                // security applies to. Deleting a field's fact or an object's version would let an older state
                // reappear as current.
                migrationBuilder.Sql($"CREATE POLICY no_delete_{table} ON {table} AS RESTRICTIVE FOR DELETE USING (false);");
            }

            // Guarded like the earlier grants: the runtime role is created by bootstrap, never by a migration. Only
            // what projection needs: read, insert and update. The bootstrap grants DML on every table, so a later
            // bootstrap run re-grants what is revoked here; the restrictive policies keep the guarantee regardless,
            // and TRUNCATE is never granted.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        GRANT SELECT, INSERT, UPDATE ON snapshot_objects, snapshot_fields TO workcontinuity_app;
                        REVOKE DELETE, TRUNCATE ON snapshot_objects, snapshot_fields FROM workcontinuity_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Dropping the tables removes their policies, indexes and grants too. It also deletes the Snapshot: a
            // rollback past this migration loses every projected fact and version.
            migrationBuilder.DropTable(
                name: "snapshot_fields");

            migrationBuilder.DropTable(
                name: "snapshot_objects");
        }
    }
}
