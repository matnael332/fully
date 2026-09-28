using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the one tenant-less function the outbox relay needs to find its work (Gate 2C, under Gate 2
    /// owner decision 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The relay must learn which tenants have unpublished outbox messages before any tenant is known, and
    /// forced row-level security hides every outbox and tenant row from the runtime role without one.
    /// <c>outbox_pending_tenants</c> is the approved exception. It is <c>SECURITY DEFINER</c> with the fixed
    /// <c>search_path</c> <c>pg_catalog, pg_temp</c>: every object it uses is schema-qualified, and keeping
    /// <c>public</c> off the path means nothing created there can shadow an operator it relies on. It returns
    /// only the ids of tenants with pending messages, oldest pending work first, and at most 100 per call,
    /// whatever limit is asked for. It returns no message, payload or row id, locks and changes nothing, and
    /// has no dynamic SQL. <c>EXECUTE</c> is revoked from PUBLIC and granted to the runtime role only.
    /// </para>
    /// <para>
    /// Claiming stays tenant-scoped: the relay then locks that tenant's own rows under the tenant's
    /// row-level security, so no cross-tenant read, lock or write exists anywhere.
    /// </para>
    /// <para>
    /// The function runs as its owner, the migrating role. <c>FORCE ROW LEVEL SECURITY</c> binds the owner
    /// too, and in Azure that role is not a superuser, so the owner gets one narrow policy of its own:
    /// read-only, pending rows only, for that role only. The runtime role gains no direct cross-tenant read,
    /// and every other policy is unchanged. No table or column changes.
    /// </para>
    /// </remarks>
    public partial class OutboxRelayTenantDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // CURRENT_USER is the migrating role, which also owns the function below.
            migrationBuilder.Sql(
                """
                CREATE POLICY relay_discovery_outbox_messages ON outbox_messages
                    AS PERMISSIVE
                    FOR SELECT
                    TO CURRENT_USER
                    USING (published_at_utc IS NULL);
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.outbox_pending_tenants(p_limit integer)
                    RETURNS TABLE (tenant_id uuid)
                    LANGUAGE sql
                    STABLE
                    STRICT
                    SECURITY DEFINER
                    SET search_path = pg_catalog, pg_temp
                AS $function$
                    SELECT o.tenant_id
                    FROM public.outbox_messages AS o
                    WHERE o.published_at_utc IS NULL
                    GROUP BY o.tenant_id
                    ORDER BY min(o.occurred_at_utc), o.tenant_id
                    LIMIT least(greatest(p_limit, 0), 100)
                $function$;
                """);

            migrationBuilder.Sql(
                "REVOKE ALL ON FUNCTION public.outbox_pending_tenants(integer) FROM PUBLIC;");

            // Guarded like the earlier grants: the runtime role is created by bootstrap, never by a
            // migration, so the schema can still be migrated where that role does not exist yet.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        GRANT EXECUTE ON FUNCTION public.outbox_pending_tenants(integer)
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

            // Removes the function, its grants and the owner's policy. No data is touched.
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.outbox_pending_tenants(integer);");
            migrationBuilder.Sql("DROP POLICY IF EXISTS relay_discovery_outbox_messages ON outbox_messages;");
        }
    }
}
