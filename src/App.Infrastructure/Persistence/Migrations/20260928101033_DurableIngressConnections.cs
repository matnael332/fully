using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds source connections and the one tenant-less function that resolves an inbound connection to
    /// its tenant (Gate 2B, under Gate 2 owner decision 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>connections</c> is tenant-owned and gets exactly the row-level security of the other
    /// tenant-owned tables: enabled, forced, and the same tenant policy.
    /// </para>
    /// <para>
    /// Ingress has to find a connection's tenant before any tenant is known, which forced row-level
    /// security forbids by design. <c>resolve_ingress_connection</c> is the one approved exception. It is
    /// <c>SECURITY DEFINER</c> with the fixed <c>search_path</c> <c>pg_catalog, pg_temp</c>: every object it
    /// uses is schema-qualified, and keeping <c>public</c> off the path means nothing created there can
    /// shadow an operator it relies on. It takes one connection id and returns that connection's tenant
    /// id and source system when the connection is active, and nothing otherwise. It has no dynamic SQL,
    /// so it cannot list, search or read anything else. <c>EXECUTE</c> is revoked from PUBLIC and granted
    /// to the runtime role only.
    /// </para>
    /// <para>
    /// The function runs as its owner, the migrating role. <c>FORCE ROW LEVEL SECURITY</c> binds the owner
    /// too, and in Azure that role is not a superuser, so the owner gets one narrow policy of its own:
    /// read-only, active connections only, for that role only. The runtime role gains no direct
    /// cross-tenant read, and every other table's policies are unchanged.
    /// </para>
    /// </remarks>
    public partial class DurableIngressConnections : Migration
    {
        private const string TenantPredicate =
            "tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "connections",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connections", x => x.connection_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_connections_tenant_id",
                table: "connections",
                column: "tenant_id");

            migrationBuilder.Sql("ALTER TABLE connections ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE connections FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                $"""
                CREATE POLICY tenant_isolation_connections ON connections
                    USING ({TenantPredicate})
                    WITH CHECK ({TenantPredicate});
                """);

            // CURRENT_USER is the migrating role, which also owns the resolver below.
            migrationBuilder.Sql(
                """
                CREATE POLICY ingress_resolution_connections ON connections
                    AS PERMISSIVE
                    FOR SELECT
                    TO CURRENT_USER
                    USING (status = 'Active');
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.resolve_ingress_connection(p_connection_id uuid)
                    RETURNS TABLE (tenant_id uuid, source_system character varying)
                    LANGUAGE sql
                    STABLE
                    STRICT
                    SECURITY DEFINER
                    SET search_path = pg_catalog, pg_temp
                AS $function$
                    SELECT c.tenant_id, c.source_system
                    FROM public.connections AS c
                    WHERE c.connection_id = p_connection_id
                      AND c.status = 'Active'
                $function$;
                """);

            migrationBuilder.Sql(
                "REVOKE ALL ON FUNCTION public.resolve_ingress_connection(uuid) FROM PUBLIC;");

            // Guarded like the Gate 1D grants: the runtime role is created by bootstrap, never by a
            // migration, so the schema can still be migrated where that role does not exist yet.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        GRANT SELECT, INSERT, UPDATE, DELETE ON connections TO workcontinuity_app;
                        GRANT EXECUTE ON FUNCTION public.resolve_ingress_connection(uuid)
                            TO workcontinuity_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.resolve_ingress_connection(uuid);");

            // Dropping the table removes its policies, index and grants too. It also deletes every
            // registered connection: a rollback past this migration loses that data.
            migrationBuilder.DropTable(
                name: "connections");
        }
    }
}
