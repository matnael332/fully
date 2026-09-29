using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes raw work-event content purgeable on its own (Gate 2F, under Gate 2 owner decisions 1 and 5): a
    /// receipt's payload and fingerprint can be cleared while the receipt row and every piece of metadata stay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>payload</c> and <c>payload_fingerprint</c> become nullable, and a check keeps them present or cleared
    /// together, so no receipt ever holds half of its content. A partial index over content-bearing receipts,
    /// by tenant and receipt time, serves the purge of each tenant's expired content, oldest first; purged rows
    /// leave it. Nothing is dropped or renamed, and no row changes.
    /// </para>
    /// <para>
    /// <c>retention_expired_tenants</c> is decision 1's retention function, the third and last approved
    /// tenant-less function. It is <c>SECURITY DEFINER</c> with the fixed <c>search_path</c>
    /// <c>pg_catalog, pg_temp</c>, schema-qualifies everything it uses and has no dynamic SQL. It returns only the
    /// ids of tenants holding raw content received at or before the caller's cutoff, oldest expired content
    /// first, at most 100 per call, and locks and changes nothing. Whatever cutoff it is given, it never reports
    /// content younger than 720 hours by the database's own clock, so it cannot reveal recent activity.
    /// 720 hours is the 30-day window of <c>RawContentRetention.Window</c> in exact hours, independent of the
    /// session's time zone. <c>EXECUTE</c> is revoked from PUBLIC and granted to the runtime role only. Purging
    /// itself stays tenant-scoped: each tenant's rows are cleared under that tenant's row-level security.
    /// </para>
    /// <para>
    /// The function runs as its owner, the migrating role, which <c>FORCE ROW LEVEL SECURITY</c> binds too; in
    /// Azure that role is not a superuser. The owner therefore gets one narrow policy of its own: read-only, for
    /// that role only, and only rows whose content is already past the same 720 hours, which are the rows the
    /// purge is about to clear. The runtime role gains no direct cross-tenant read, and every other policy is
    /// unchanged.
    /// </para>
    /// <para>
    /// Down removes the function, the policy, the index and the check, then restores <c>NOT NULL</c>. That last
    /// step refuses, and the rollback fails whole, while any receipt's content has been purged: <c>SET NOT
    /// NULL</c> checks every row regardless of row-level security, and content that was purged cannot be
    /// restored and must never be invented.
    /// </para>
    /// </remarks>
    public partial class RawContentRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AlterColumn<string>(
                name: "payload_fingerprint",
                table: "raw_receipts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "payload",
                table: "raw_receipts",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.CreateIndex(
                name: "ix_raw_receipts_tenant_content_received",
                table: "raw_receipts",
                columns: new[] { "tenant_id", "received_at_utc" },
                filter: "payload IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_raw_receipts_content",
                table: "raw_receipts",
                sql: "(payload IS NULL) = (payload_fingerprint IS NULL)");

            // CURRENT_USER is the migrating role, which also owns the function below.
            migrationBuilder.Sql(
                """
                CREATE POLICY retention_discovery_raw_receipts ON raw_receipts
                    AS PERMISSIVE
                    FOR SELECT
                    TO CURRENT_USER
                    USING (payload IS NOT NULL AND received_at_utc <= now() - interval '720 hours');
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.retention_expired_tenants(p_cutoff timestamp with time zone, p_limit integer)
                    RETURNS TABLE (tenant_id uuid)
                    LANGUAGE sql
                    STABLE
                    STRICT
                    SECURITY DEFINER
                    SET search_path = pg_catalog, pg_temp
                AS $function$
                    SELECT r.tenant_id
                    FROM public.raw_receipts AS r
                    WHERE r.payload IS NOT NULL
                      AND r.received_at_utc <= least(p_cutoff, now() - interval '720 hours')
                    GROUP BY r.tenant_id
                    ORDER BY min(r.received_at_utc), r.tenant_id
                    LIMIT least(greatest(p_limit, 0), 100)
                $function$;
                """);

            migrationBuilder.Sql(
                "REVOKE ALL ON FUNCTION public.retention_expired_tenants(timestamp with time zone, integer) FROM PUBLIC;");

            // Guarded like the earlier grants: the runtime role is created by bootstrap, never by a
            // migration, so the schema can still be migrated where that role does not exist yet.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        GRANT EXECUTE ON FUNCTION public.retention_expired_tenants(timestamp with time zone, integer)
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

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.retention_expired_tenants(timestamp with time zone, integer);");
            migrationBuilder.Sql("DROP POLICY IF EXISTS retention_discovery_raw_receipts ON raw_receipts;");

            migrationBuilder.DropIndex(
                name: "ix_raw_receipts_tenant_content_received",
                table: "raw_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_raw_receipts_content",
                table: "raw_receipts");

            // No default and no backfill: this fails, and the rollback with it, while any content was purged.
            migrationBuilder.Sql("ALTER TABLE raw_receipts ALTER COLUMN payload_fingerprint SET NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE raw_receipts ALTER COLUMN payload SET NOT NULL;");
        }
    }
}
