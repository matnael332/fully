using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes the database's own clock the only clock of raw-content retention, at the 28-day purge age (Gate 2F owner
    /// reviews): raw content is purged two days before the 30-day limit, so the limit holds between hourly purge runs,
    /// and no application clock can delay or advance it, neither the purge Worker's nor the ingress host's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retention function is replaced by <c>retention_expired_tenants(p_limit)</c>: it takes no cutoff at all, so
    /// nothing outside the database can move its boundary, and it reports only tenants holding content the database's
    /// clock shows to be at least 672 hours old, <c>RawContentRetention.PurgeAge</c> in exact hours. The owner read
    /// policy <c>retention_discovery_raw_receipts</c> moves to the same 672 hours. The purge statement, in application
    /// code, is bounded by the same clock and the same age, and measures every age with it.
    /// </para>
    /// <para>
    /// The age basis itself, the receipt time <c>received_at_utc</c>, becomes the database's own. It defaults to
    /// <c>now()</c>, and the trigger <c>stamp_receipt_time_raw_receipts</c> stamps it with <c>now()</c> on every insert,
    /// whatever value is supplied, and refuses any later change, so no application clock can make content look younger
    /// or older. Its function is not a definer, reads nothing, has a fixed <c>search_path</c> and EXECUTE revoked from
    /// PUBLIC: it only sets the inserted row's own receipt time.
    /// </para>
    /// <para>
    /// The retention function keeps its name, attributes, fixed <c>search_path</c>, result and body apart from the
    /// bound. A new signature needs a new function, so the old one is dropped and the new one's EXECUTE is revoked from
    /// PUBLIC and granted to the runtime role only, exactly as before. No table, column type, index, row or other
    /// policy changes: a receipt stored before this migration keeps the receipt time it was stored with.
    /// </para>
    /// <para>
    /// Down removes the trigger, its function and the default, and restores the <c>RawContentRetention</c>
    /// definitions, with their caller cutoff and 720-hour bound, and the same grants. It touches no data, so it also
    /// works after content was purged.
    /// </para>
    /// </remarks>
    public partial class RawContentPurgeAge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("DROP FUNCTION public.retention_expired_tenants(timestamp with time zone, integer);");
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.retention_expired_tenants(p_limit integer)
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
                      AND r.received_at_utc <= now() - interval '672 hours'
                    GROUP BY r.tenant_id
                    ORDER BY min(r.received_at_utc), r.tenant_id
                    LIMIT least(greatest(p_limit, 0), 100)
                $function$;
                """);
            migrationBuilder.Sql(Grants("integer"));
            migrationBuilder.Sql(OwnerPolicy("672 hours"));

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "received_at_utc",
                table: "raw_receipts",
                type: "timestamptz",
                nullable: false,
                defaultValueSql: "now()",
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz");
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.stamp_receipt_time()
                    RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path = pg_catalog, pg_temp
                AS $function$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        NEW.received_at_utc := now();
                    ELSIF NEW.received_at_utc IS DISTINCT FROM OLD.received_at_utc THEN
                        RAISE EXCEPTION 'The receipt time is stamped by the database and never changes.'
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'stamp_receipt_time_raw_receipts';
                    END IF;

                    RETURN NEW;
                END
                $function$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION public.stamp_receipt_time() FROM PUBLIC;");
            migrationBuilder.Sql(
                """
                CREATE TRIGGER stamp_receipt_time_raw_receipts
                    BEFORE INSERT OR UPDATE OF received_at_utc ON public.raw_receipts
                    FOR EACH ROW
                    EXECUTE FUNCTION public.stamp_receipt_time();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("DROP TRIGGER stamp_receipt_time_raw_receipts ON public.raw_receipts;");
            migrationBuilder.Sql("DROP FUNCTION public.stamp_receipt_time();");
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "received_at_utc",
                table: "raw_receipts",
                type: "timestamptz",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz",
                oldDefaultValueSql: "now()");

            migrationBuilder.Sql("DROP FUNCTION public.retention_expired_tenants(integer);");
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
            migrationBuilder.Sql(Grants("timestamp with time zone, integer"));
            migrationBuilder.Sql(OwnerPolicy("720 hours"));
        }

        // Guarded like the earlier grants: the runtime role is created by bootstrap, never by a migration, so the
        // schema can still be migrated where that role does not exist yet.
        private static string Grants(string arguments) =>
            $$"""
            REVOKE ALL ON FUNCTION public.retention_expired_tenants({{arguments}}) FROM PUBLIC;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                    GRANT EXECUTE ON FUNCTION public.retention_expired_tenants({{arguments}})
                        TO workcontinuity_app;
                END IF;
            END
            $$;
            """;

        private static string OwnerPolicy(string bound) =>
            $"""
            ALTER POLICY retention_discovery_raw_receipts ON raw_receipts
                USING (payload IS NOT NULL AND received_at_utc <= now() - interval '{bound}');
            """;
    }
}
