using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Gate 3B maintenance before 3G: a started authorization records when its PKCE verifier was confirmed deleted, so
    /// a verifier whose deletion never happened, because the completing process died or the deletion failed, stays due
    /// until a cleanup deletes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive only: one nullable column holding an instant, never a verifier; a check that a verifier is confirmed
    /// gone only once its transaction was consumed or closed; and a partial index over the transactions whose
    /// verifier's deletion is not yet recorded. Existing rows start with no recorded deletion, so a first cleanup
    /// confirms each of their verifiers gone, deleting any still stored.
    /// </para>
    /// <para>
    /// The runtime role keeps exactly its select, insert and update on the table, which covers the new column; no grant
    /// changes, and the R3B-2 re-assertion recorded for the Azure rollout is unchanged.
    /// </para>
    /// </remarks>
    public partial class ConnectorAuthorizationVerifierCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verifier_deleted_at_utc",
                table: "connector_authorization_transactions",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_connector_authorization_transactions_verifier_pending",
                table: "connector_authorization_transactions",
                columns: new[] { "tenant_id", "consumed_at_utc" },
                filter: "verifier_deleted_at_utc IS NULL AND consumed_at_utc IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connector_authorization_transactions_verifier_deleted",
                table: "connector_authorization_transactions",
                sql: "verifier_deleted_at_utc IS NULL OR (consumed_at_utc IS NOT NULL AND verifier_deleted_at_utc >= consumed_at_utc)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Rolling back loses only the record of which verifiers were confirmed gone; no authorization changes.
            migrationBuilder.DropIndex(
                name: "ix_connector_authorization_transactions_verifier_pending",
                table: "connector_authorization_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connector_authorization_transactions_verifier_deleted",
                table: "connector_authorization_transactions");

            migrationBuilder.DropColumn(
                name: "verifier_deleted_at_utc",
                table: "connector_authorization_transactions");
        }
    }
}
