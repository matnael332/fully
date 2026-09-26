using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialGate1DSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    outbox_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.outbox_message_id);
                });

            migrationBuilder.CreateTable(
                name: "raw_receipts",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_system = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    schema_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    processing_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_receipts", x => x.receipt_id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identity_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                columns: new[] { "published_at_utc", "occurred_at_utc" },
                filter: "published_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_raw_receipts_received_at_utc",
                table: "raw_receipts",
                column: "received_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_raw_receipts_tenant_connection_source_event",
                table: "raw_receipts",
                columns: new[] { "tenant_id", "connection_reference", "source_event_id" },
                unique: true,
                filter: "source_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_users_tenant_provider_subject",
                table: "users",
                columns: new[] { "tenant_id", "identity_provider", "provider_subject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "raw_receipts");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
