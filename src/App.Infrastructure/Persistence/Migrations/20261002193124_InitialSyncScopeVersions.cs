using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkContinuity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Gate 3F owner review: a connection takes part in initial sync from the moment its configuration first enables a scope,
    /// as a NotStarted sync row with no generation yet; and its enabled scope set has a version, raised by every change, that
    /// each generation records and that a Live generation must match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and relaxing only: the state set gains <c>NotStarted</c>, a NotStarted row has generation 0 and no authority,
    /// start or generation scope-set version, and the two version columns are new. Existing rows, all past NotStarted, get
    /// version 1 for both; no default stays on the column, since the application always sets it.
    /// </para>
    /// <para>
    /// A check constraint makes the version rule hold in the database itself: a generation's version is never above the
    /// connection's, and a Live generation's equals it, so neither a stale generation becoming Live nor a scope-set change that
    /// leaves Live in place can commit.
    /// </para>
    /// <para>
    /// The runtime role's privileges on <c>connection_syncs</c> are re-asserted exactly, with update of the two new columns
    /// and still never of the replay floor or the row's identity. The R3B-2 re-assertion recorded for the Azure rollout
    /// includes them.
    /// </para>
    /// </remarks>
    public partial class InitialSyncScopeVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_syncs_generation",
                table: "connection_syncs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_syncs_state",
                table: "connection_syncs");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "started_at_utc",
                table: "connection_syncs",
                type: "timestamptz",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz");

            migrationBuilder.AlterColumn<string>(
                name: "provider_subject",
                table: "connection_syncs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "authorized_at_utc",
                table: "connection_syncs",
                type: "timestamptz",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz");

            migrationBuilder.AddColumn<long>(
                name: "generation_scope_version",
                table: "connection_syncs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "scope_version",
                table: "connection_syncs",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            // Every existing row has a generation, which read version 1; then no default stays.
            migrationBuilder.Sql("UPDATE connection_syncs SET generation_scope_version = scope_version;");
            migrationBuilder.Sql("ALTER TABLE connection_syncs ALTER COLUMN scope_version DROP DEFAULT;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_syncs_generation",
                table: "connection_syncs",
                sql: "generation >= 0 AND (state = 'NotStarted') = (generation = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_syncs_not_started",
                table: "connection_syncs",
                sql: "(state = 'NotStarted') = (provider_subject IS NULL) AND (state = 'NotStarted') = (authorized_at_utc IS NULL) AND (state = 'NotStarted') = (started_at_utc IS NULL) AND (state = 'NotStarted') = (generation_scope_version IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_syncs_scope_version",
                table: "connection_syncs",
                sql: "scope_version >= 1 AND (generation_scope_version IS NULL OR generation_scope_version BETWEEN 1 AND scope_version) AND (state <> 'Live' OR generation_scope_version = scope_version)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_syncs_state",
                table: "connection_syncs",
                sql: "state IN ('NotStarted', 'Buffering', 'Reading', 'Replaying', 'Live', 'Failed', 'RequiresRestart')");

            // Exactly what initial sync needs, whatever was granted before, now with the two version columns; never the
            // replay floor or the row's identity. Guarded like the earlier grants.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workcontinuity_app') THEN
                        REVOKE ALL PRIVILEGES ON connection_syncs FROM workcontinuity_app;
                        GRANT SELECT, INSERT ON connection_syncs TO workcontinuity_app;
                        GRANT UPDATE (generation, state, provider_subject, authorized_at_utc, scope_version, generation_scope_version,
                            buffer_start_fence, replay_fence, cutover_fence, claim_id, claimed_until_utc, attempts, next_attempt_at_utc,
                            last_outcome, started_at_utc, live_at_utc, updated_at_utc) ON connection_syncs TO workcontinuity_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // A NotStarted row has no generation, authority or start to restore into the earlier shape, and none is invented:
            // rolling back is refused while one exists. A forward fix is preferred.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM connection_syncs WHERE state = 'NotStarted') THEN
                        RAISE EXCEPTION 'connection_syncs holds NotStarted rows, which the earlier schema cannot represent.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_syncs_generation",
                table: "connection_syncs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_syncs_not_started",
                table: "connection_syncs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_syncs_scope_version",
                table: "connection_syncs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_connection_syncs_state",
                table: "connection_syncs");

            migrationBuilder.DropColumn(
                name: "generation_scope_version",
                table: "connection_syncs");

            migrationBuilder.DropColumn(
                name: "scope_version",
                table: "connection_syncs");

            migrationBuilder.Sql("ALTER TABLE connection_syncs ALTER COLUMN started_at_utc SET NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE connection_syncs ALTER COLUMN provider_subject SET NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE connection_syncs ALTER COLUMN authorized_at_utc SET NOT NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_syncs_generation",
                table: "connection_syncs",
                sql: "generation >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_connection_syncs_state",
                table: "connection_syncs",
                sql: "state IN ('Buffering', 'Reading', 'Replaying', 'Live', 'Failed', 'RequiresRestart')");
        }
    }
}
