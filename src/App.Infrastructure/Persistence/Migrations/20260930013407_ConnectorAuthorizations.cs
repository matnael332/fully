using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the Gate 3B connection authorization lifecycle: the authorizations being started and each connection's
    /// delegated authorization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>connector_authorization_transactions</c> keeps one row per started authorization: the tenant, the canonical
    /// user, the intended connection, the source system, the purpose, the SHA-256 of the state's random part, the
    /// SHA-256 of the redirect address, the requested scope names, and the created, expiry and consumed times, which the
    /// store stamps with the database clock. A check constraint caps the lifetime at the provider's 10-minute code
    /// validity. <c>connector_authorizations</c> keeps one row per authorized connection: the canonical user, the state,
    /// the provider's identifiers for the delegating user and account, the credential generation, the opaque
    /// <c>material_version</c> naming where the connector keeps the credential, the access expiry, the granted scope
    /// names, the refresh claim and lifecycle times. No column of either table can hold a token, a client credential or a
    /// PKCE verifier (decision D3B-2, invariant 53).
    /// </para>
    /// <para>
    /// Both tables are tenant-owned and get exactly the row-level security of the other tenant-owned tables: enabled,
    /// forced, and the same tenant policy. No tenant-less function is added. Every foreign key includes the tenant,
    /// because PostgreSQL checks foreign keys without row-level security; the two unique constraints added to
    /// <c>users</c> and <c>connections</c> are the keys those foreign keys point at, and each is trivially satisfied
    /// because its second column is already unique. The runtime role holds only what the store needs on both tables:
    /// every privilege is revoked, DELETE included, and read, insert and update are granted back. The store never
    /// deletes: a revoked authorization stays as minimal metadata.
    /// </para>
    /// </remarks>
    public partial class ConnectorAuthorizations : Migration
    {
        private const string TenantPredicate =
            "tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid";

        private static readonly string[] AuthorizationTables = ["connector_authorizations", "connector_authorization_transactions"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_users_tenant_user",
                table: "users",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_connections_tenant_connection",
                table: "connections",
                columns: new[] { "tenant_id", "connection_id" });

            migrationBuilder.CreateTable(
                name: "connector_authorization_transactions",
                columns: table => new
                {
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    redirect_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    requested_scopes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_authorization_transactions", x => x.transaction_id);
                    table.CheckConstraint("ck_connector_authorization_transactions_consumed", "consumed_at_utc IS NULL OR consumed_at_utc >= created_at_utc");
                    table.CheckConstraint("ck_connector_authorization_transactions_hashes", "octet_length(state_hash) = 32 AND octet_length(redirect_hash) = 32");
                    table.CheckConstraint("ck_connector_authorization_transactions_lifetime", "expires_at_utc > created_at_utc AND expires_at_utc <= created_at_utc + interval '10 minutes'");
                    table.CheckConstraint("ck_connector_authorization_transactions_purpose", "purpose IN ('Connect', 'Reauthorize')");
                    table.CheckConstraint("ck_connector_authorization_transactions_scopes", "requested_scopes <> ''");
                    table.ForeignKey(
                        name: "fk_connector_authorization_transactions_user",
                        columns: x => new { x.tenant_id, x.canonical_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "connector_authorizations",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_account = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    material_version = table.Column<Guid>(type: "uuid", nullable: false),
                    access_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    granted_scopes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    refresh_claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refresh_claimed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    authorized_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    refreshed_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    reauthorization_required_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    provider_revocation_confirmed = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_authorizations", x => x.connection_id);
                    table.CheckConstraint("ck_connector_authorizations_claim", "(refresh_claim_id IS NULL) = (refresh_claimed_at_utc IS NULL)");
                    table.CheckConstraint("ck_connector_authorizations_generation", "generation >= 1");
                    table.CheckConstraint("ck_connector_authorizations_identifiers", "provider_subject <> '' AND provider_account <> '' AND granted_scopes <> ''");
                    table.CheckConstraint("ck_connector_authorizations_reauthorization", "state <> 'ReauthorizationRequired' OR reauthorization_required_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_connector_authorizations_revocation_confirmed", "provider_revocation_confirmed IS NULL OR state = 'Revoked'");
                    table.CheckConstraint("ck_connector_authorizations_revoked", "(state = 'Revoked') = (revoked_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_connector_authorizations_state", "state IN ('Active', 'ReauthorizationRequired', 'Revoked')");
                    table.ForeignKey(
                        name: "fk_connector_authorizations_connection",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "connection_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_connector_authorizations_user",
                        columns: x => new { x.tenant_id, x.canonical_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_connector_authorization_transactions_open",
                table: "connector_authorization_transactions",
                columns: new[] { "tenant_id", "expires_at_utc" },
                filter: "consumed_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_connector_authorization_transactions_tenant_user",
                table: "connector_authorization_transactions",
                columns: new[] { "tenant_id", "canonical_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_connector_authorizations_tenant_connection",
                table: "connector_authorizations",
                columns: new[] { "tenant_id", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_connector_authorizations_tenant_user",
                table: "connector_authorizations",
                columns: new[] { "tenant_id", "canonical_user_id" });

            foreach (var table in AuthorizationTables)
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

            // Exactly what the store needs, whatever was granted before (Gate 3B review item R3B-2). Every privilege of the
            // runtime role on both tables is revoked, DELETE and TRUNCATE included, including what the bootstrap's
            // default privileges gave the tables when they were created; then read, insert and update are granted back.
            // Guarded like the earlier grants: the runtime role is created by bootstrap, never by a migration.
            // The global bootstrap is unchanged and grants DML on every table each time it runs, so a rerun after this
            // migration would re-grant DELETE on both tables. The Azure rollout must therefore never rerun it after this
            // migration without re-asserting and verifying these privileges in the same approved step (Gate 3 scope,
            // R3B-2).
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        REVOKE ALL PRIVILEGES ON connector_authorizations, connector_authorization_transactions
                            FROM workcontinuity_app;
                        GRANT SELECT, INSERT, UPDATE ON connector_authorizations, connector_authorization_transactions
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

            // Dropping the tables removes their policies, indexes and grants too. It also deletes every authorization and
            // every started authorization: a rollback past this migration loses the delegation records, and whatever the
            // connector keeps outside the database would no longer be referenced by any row.
            migrationBuilder.DropTable(
                name: "connector_authorization_transactions");

            migrationBuilder.DropTable(
                name: "connector_authorizations");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_users_tenant_user",
                table: "users");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_connections_tenant_connection",
                table: "connections");
        }
    }
}
