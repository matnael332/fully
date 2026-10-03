using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Gate 3F final owner review: the current work view. State is current only while the source scope it was read from is
    /// enabled and its baseline established; nothing is deleted or rewritten to make it so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive only. A source scope gains the generation whose transition to Live established its baseline, null while it
    /// has none, and a check that a disabled scope never has one. A reconciliation target gains the scope-set version at
    /// which its scope was enabled after the field was known, null once a baseline read of the scope confirmed it again.
    /// No existing row is rewritten: a scope's baseline is set by its connection's next transition to Live.
    /// </para>
    /// <para>
    /// Two views present the Snapshot as current work, provider-neutrally, by identifiers only. The fields of a connection
    /// that takes no part in initial sync are all current, as before; a participating connection's field is current only
    /// when its reconciliation target, which has the field's own key and the source scope it was read from, names an
    /// enabled scope of that connection with an established baseline and no baseline is awaited for the field. What
    /// cannot be attributed to such a scope is not current. An object is current when one of its fields is, or when its
    /// connection takes no part. The views run as the caller, so the tables' row-level security applies to every reader,
    /// and the runtime role may only read them. A later change to a column they read must recreate them.
    /// </para>
    /// <para>
    /// The runtime role's privileges on <c>connector_source_scopes</c> are re-asserted exactly, now with update of the
    /// baseline; its table-wide update of <c>reconciliation_targets</c> already covers the new column. The R3B-2
    /// re-assertion recorded for the Azure rollout includes both.
    /// </para>
    /// </remarks>
    public partial class InitialSyncActiveScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<long>(
                name: "awaiting_baseline_version",
                table: "reconciliation_targets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "baseline_generation",
                table: "connector_source_scopes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliation_targets_awaiting_baseline",
                table: "reconciliation_targets",
                sql: "awaiting_baseline_version IS NULL OR awaiting_baseline_version >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connector_source_scopes_baseline",
                table: "connector_source_scopes",
                sql: "baseline_generation IS NULL OR (enabled AND baseline_generation >= 1)");

            migrationBuilder.Sql(
                """
                CREATE VIEW public.active_snapshot_fields WITH (security_invoker = true) AS
                SELECT f.*
                FROM public.snapshot_fields AS f
                LEFT JOIN public.connection_syncs AS s
                       ON s.tenant_id = f.tenant_id AND s.source_system = f.object_system
                      AND s.connection_id::text = f.object_connection
                LEFT JOIN public.reconciliation_targets AS t
                       ON t.tenant_id = f.tenant_id AND t.object_system = f.object_system
                      AND t.object_connection = f.object_connection AND t.object_type = f.object_type
                      AND t.object_id = f.object_id AND t.field = f.field
                LEFT JOIN public.connector_source_scopes AS c
                       ON c.tenant_id = t.tenant_id AND c.connection_id = s.connection_id
                      AND c.source_system = t.object_system AND c.resource_id = t.source_scope
                WHERE s.connection_id IS NULL
                   OR (c.enabled AND c.baseline_generation IS NOT NULL AND t.awaiting_baseline_version IS NULL);
                """);

            migrationBuilder.Sql(
                """
                CREATE VIEW public.active_snapshot_objects WITH (security_invoker = true) AS
                SELECT o.*
                FROM public.snapshot_objects AS o
                WHERE NOT EXISTS (
                          SELECT 1 FROM public.connection_syncs AS s
                          WHERE s.tenant_id = o.tenant_id AND s.source_system = o.object_system
                            AND s.connection_id::text = o.object_connection)
                   OR EXISTS (
                          SELECT 1 FROM public.active_snapshot_fields AS f
                          WHERE f.tenant_id = o.tenant_id AND f.object_system = o.object_system
                            AND f.object_connection = o.object_connection AND f.object_type = o.object_type
                            AND f.object_id = o.object_id);
                """);

            // Exactly what the runtime role needs, whatever was granted before; the views are read-only to it. Guarded like
            // the earlier grants.
            migrationBuilder.Sql(
                """
                REVOKE ALL PRIVILEGES ON active_snapshot_objects, active_snapshot_fields FROM PUBLIC;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        REVOKE ALL PRIVILEGES ON connector_source_scopes FROM workcontinuity_app;
                        GRANT SELECT, INSERT ON connector_source_scopes TO workcontinuity_app;
                        GRANT UPDATE (enabled, baseline_generation, updated_at_utc) ON connector_source_scopes TO workcontinuity_app;
                        REVOKE ALL PRIVILEGES ON active_snapshot_objects, active_snapshot_fields FROM workcontinuity_app;
                        GRANT SELECT ON active_snapshot_objects, active_snapshot_fields TO workcontinuity_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // The views first, then the markers: rolling back loses only which scopes and fields are current, never history.
            migrationBuilder.Sql("DROP VIEW public.active_snapshot_objects;");
            migrationBuilder.Sql("DROP VIEW public.active_snapshot_fields;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliation_targets_awaiting_baseline",
                table: "reconciliation_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connector_source_scopes_baseline",
                table: "connector_source_scopes");

            migrationBuilder.DropColumn(
                name: "awaiting_baseline_version",
                table: "reconciliation_targets");

            migrationBuilder.DropColumn(
                name: "baseline_generation",
                table: "connector_source_scopes");

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        REVOKE ALL PRIVILEGES ON connector_source_scopes FROM workcontinuity_app;
                        GRANT SELECT, INSERT ON connector_source_scopes TO workcontinuity_app;
                        GRANT UPDATE (enabled, updated_at_utc) ON connector_source_scopes TO workcontinuity_app;
                    END IF;
                END
                $$;
                """);
        }
    }
}
