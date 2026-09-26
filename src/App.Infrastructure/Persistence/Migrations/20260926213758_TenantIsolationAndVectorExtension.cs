using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds storage-level tenant isolation and enables the pgvector extension.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row-level security is the second isolation boundary. Application authorization remains the
    /// first; this layer means a mistake there cannot become a cross-tenant read or write.
    /// </para>
    /// <para>
    /// Each policy compares <c>tenant_id</c> against the transaction-local
    /// <c>app.tenant_id</c> setting. <c>current_setting(..., true)</c> returns NULL when the setting
    /// is absent and <c>nullif(..., '')</c> maps a blank to NULL, so an unset tenant yields
    /// <c>tenant_id = NULL</c>, which is never true: the policy matches no rows and the operation
    /// fails closed instead of falling back to "all rows".
    /// </para>
    /// <para>
    /// <c>FORCE ROW LEVEL SECURITY</c> subjects the table owner to its own policies too. A PostgreSQL
    /// superuser still bypasses row-level security entirely, which is precisely why normal runtime
    /// connects as a separate non-superuser role that holds neither SUPERUSER nor BYPASSRLS. Schema
    /// migration keeps the elevated connection; tenant data access never does.
    /// </para>
    /// </remarks>
    public partial class TenantIsolationAndVectorExtension : Migration
    {
        private static readonly string[] TenantOwnedTables =
            ["tenants", "users", "raw_receipts", "outbox_messages"];

        private const string TenantPredicate =
            "tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // pgvector is enabled now so semantic retrieval has storage support when its Gate
            // arrives. No embedding column or table is created here: that would be speculative.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

            foreach (var table in TenantOwnedTables)
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

            // The runtime role is created by local/deployment bootstrap, not by a migration, because
            // a migration must never contain a credential. Granting is guarded so the schema can be
            // migrated on a database where that role does not exist yet.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        GRANT USAGE ON SCHEMA public TO workcontinuity_app;
                        GRANT SELECT, INSERT, UPDATE, DELETE
                            ON tenants, users, raw_receipts, outbox_messages
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

            foreach (var table in TenantOwnedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation_{table} ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }

            // The extension is deliberately left in place. Dropping it would cascade to any object
            // that depends on it, which is a destructive side effect a schema rollback must not have.
        }
    }
}
