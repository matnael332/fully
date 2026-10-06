using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WorkContinuity.Core.Results;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Harness.Monday.Evidence;

/// <summary>One field of current work, as the active view presents it.</summary>
/// <param name="ObjectType">The object's canonical type.</param>
/// <param name="ObjectId">The object's source id.</param>
/// <param name="Field">The field.</param>
/// <param name="DataType">The fact's data type.</param>
/// <param name="Value">The confirmed value. Work content: never logged, never part of diagnostics.</param>
/// <param name="SourceUpdatedAtUtc">The source's own update time of the value, when it has one.</param>
/// <param name="FieldVersion">The field's version.</param>
public sealed record CurrentWorkField(
    string ObjectType,
    string ObjectId,
    string Field,
    string DataType,
    string? Value,
    DateTimeOffset? SourceUpdatedAtUtc,
    long FieldVersion)
{
    /// <summary>Identifies the field; the value is never rendered.</summary>
    public override string ToString() => $"CurrentWorkField({ObjectType} {ObjectId}, {Field})";
}

/// <summary>One object of current work, as the active view presents it.</summary>
/// <param name="ObjectType">The object's canonical type.</param>
/// <param name="ObjectId">The object's source id.</param>
/// <param name="Version">The object's version.</param>
public sealed record CurrentWorkObject(string ObjectType, string ObjectId, long Version);

/// <summary>The trusted tenant's current work: what its enabled, baselined scopes hold.</summary>
/// <param name="Objects">The objects, at most <see cref="CurrentWorkReader.MaxRows"/>.</param>
/// <param name="Fields">The fields, at most <see cref="CurrentWorkReader.MaxRows"/>.</param>
public sealed record CurrentWork(IReadOnlyList<CurrentWorkObject> Objects, IReadOnlyList<CurrentWorkField> Fields);

/// <summary>
/// Reads current work for the trusted tenant through the two active views, and through nothing else (owner decision
/// D3G-6).
/// </summary>
/// <remarks>
/// The views present only what a participating connection's enabled, baselined scopes hold; the tables beneath them
/// also keep the state of disabled scopes and of scopes awaiting a baseline, which is not current work. The reader
/// therefore names the views only. It runs inside the tenant-scoped transaction, as the runtime role, so forced
/// row-level security limits it to the trusted tenant, and the views run as their caller.
/// </remarks>
public sealed class CurrentWorkReader
{
    /// <summary>The most rows either list holds.</summary>
    public const int MaxRows = 500;

    private const string ObjectsSql =
        "SELECT object_type, object_id, version FROM active_snapshot_objects ORDER BY object_type, object_id LIMIT 500";

    private const string FieldsSql =
        "SELECT object_type, object_id, field, data_type, value, source_updated_at_utc, field_version "
        + "FROM active_snapshot_fields ORDER BY object_type, object_id, field LIMIT 500";

    private readonly ITenantScopedDatabase _database;

    /// <summary>Creates the reader.</summary>
    /// <param name="database">The tenant-scoped database of the run.</param>
    public CurrentWorkReader(ITenantScopedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Reads the trusted tenant's current work.</summary>
    public Task<Result<CurrentWork>> ReadAsync(CancellationToken cancellationToken) =>
        _database.ExecuteAsync(
            async (dbContext, token) =>
            {
                var objects = await EvidenceSql.ReadAsync(
                    dbContext,
                    ObjectsSql,
                    row => new CurrentWorkObject(row.GetString(0), row.GetString(1), row.GetInt64(2)),
                    token);
                var fields = await EvidenceSql.ReadAsync(
                    dbContext,
                    FieldsSql,
                    row => new CurrentWorkField(
                        row.GetString(0),
                        row.GetString(1),
                        row.GetString(2),
                        row.GetString(3),
                        row.IsDBNull(4) ? null : row.GetString(4),
                        row.IsDBNull(5) ? null : row.GetFieldValue<DateTimeOffset>(5),
                        row.GetInt64(6)),
                    token);
                return new CurrentWork(objects, fields);
            },
            cancellationToken);
}

/// <summary>A count of rows sharing fixed, content-free codes.</summary>
/// <param name="Codes">The codes that identify the group: states, outcomes and schema versions the product wrote.</param>
/// <param name="Count">How many rows share them.</param>
public sealed record EvidenceCount(IReadOnlyList<string> Codes, long Count);

/// <summary>One connection's initial-sync state.</summary>
/// <param name="ConnectionId">The connection.</param>
/// <param name="Generation">The generation.</param>
/// <param name="State">The state.</param>
/// <param name="LastOutcome">The last outcome code, or empty.</param>
/// <param name="ScopeVersion">The scope-set version.</param>
/// <param name="ReplayFloor">The replay floor.</param>
/// <param name="CutoverFence">The cutover fence, when the generation has one.</param>
/// <param name="LiveAtUtc">When the generation went live, when it did.</param>
public sealed record SyncEvidence(
    Guid ConnectionId,
    long Generation,
    string State,
    string LastOutcome,
    long ScopeVersion,
    long ReplayFloor,
    long? CutoverFence,
    DateTimeOffset? LiveAtUtc);

/// <summary>One scope of one initial-sync generation.</summary>
/// <param name="ConnectionId">The connection.</param>
/// <param name="Generation">The generation.</param>
/// <param name="ResourceId">The configured resource id.</param>
/// <param name="State">The state.</param>
/// <param name="LastOutcome">The last outcome code, or empty.</param>
/// <param name="PagesRead">Pages read.</param>
/// <param name="ObjectsRead">Objects read.</param>
/// <param name="FieldsApplied">Fields applied.</param>
public sealed record SyncScopeEvidence(
    Guid ConnectionId,
    long Generation,
    string ResourceId,
    string State,
    string LastOutcome,
    int PagesRead,
    int ObjectsRead,
    int FieldsApplied);

/// <summary>One configured source scope.</summary>
/// <param name="ConnectionId">The connection.</param>
/// <param name="ResourceId">The configured resource id.</param>
/// <param name="Enabled">Whether it is enabled.</param>
/// <param name="BaselineGeneration">The generation that established its baseline, when one did.</param>
public sealed record SourceScopeEvidence(Guid ConnectionId, string ResourceId, bool Enabled, long? BaselineGeneration);

/// <summary>One connection's authorization state. No provider identifier, no credential.</summary>
/// <param name="ConnectionId">The connection.</param>
/// <param name="State">The state.</param>
/// <param name="Generation">The credential generation.</param>
/// <param name="AuthorizedAtUtc">When it was authorized or last reauthorized.</param>
/// <param name="RefreshedAtUtc">When its credential was last renewed, when it was.</param>
/// <param name="RevokedAtUtc">When it was revoked, when it was.</param>
/// <param name="ProviderRevocationConfirmed">Whether the provider confirmed the revocation.</param>
public sealed record AuthorizationEvidence(
    Guid ConnectionId,
    string State,
    long Generation,
    DateTimeOffset AuthorizedAtUtc,
    DateTimeOffset? RefreshedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    bool ProviderRevocationConfirmed);

/// <summary>The authorization transactions, by what remains to be done for them.</summary>
/// <param name="Open">Neither consumed nor closed.</param>
/// <param name="VerifierDue">Consumed or closed, with no recorded verifier deletion.</param>
/// <param name="VerifierDeleted">With a recorded verifier deletion.</param>
public sealed record TransactionEvidence(long Open, long VerifierDue, long VerifierDeleted);

/// <summary>The trusted tenant's operational metadata: ids, states, counts, times and fixed outcome codes.</summary>
/// <param name="Receipts">Raw receipts by processing state and schema version.</param>
/// <param name="HighestIngressSequence">The highest ingress sequence of a receipt, when there is one.</param>
/// <param name="Ledger">Ledger entries by outcome, reason code and quarantine reason.</param>
/// <param name="Targets">Reconciliation targets by state and last outcome.</param>
/// <param name="Syncs">Each connection's initial-sync state.</param>
/// <param name="SyncScopes">Each generation's scopes.</param>
/// <param name="SourceScopes">The configured source scopes.</param>
/// <param name="Authorizations">Each connection's authorization state.</param>
/// <param name="Transactions">The authorization transactions.</param>
public sealed record OperationalEvidence(
    IReadOnlyList<EvidenceCount> Receipts,
    long? HighestIngressSequence,
    IReadOnlyList<EvidenceCount> Ledger,
    IReadOnlyList<EvidenceCount> Targets,
    IReadOnlyList<SyncEvidence> Syncs,
    IReadOnlyList<SyncScopeEvidence> SyncScopes,
    IReadOnlyList<SourceScopeEvidence> SourceScopes,
    IReadOnlyList<AuthorizationEvidence> Authorizations,
    TransactionEvidence Transactions);

/// <summary>
/// Reads the trusted tenant's operational metadata (owner decision D3G-6): receipt ids' states, ledger outcomes,
/// reconciliation targets, sync state, authorization state, counts, timestamps and fixed outcome codes.
/// </summary>
/// <remarks>
/// It reads no payload, no value and no provider text: every statement is fixed, selects only state, count, time,
/// identifier and outcome-code columns, and changes nothing. It never reads the Snapshot: current work has its own
/// reader, over the active views. Like every harness read it runs inside the tenant-scoped transaction, as the runtime
/// role, under forced row-level security.
/// </remarks>
public sealed class OperationalEvidenceReader
{
    private const string ReceiptsSql =
        "SELECT processing_state::text, schema_version, count(*) FROM raw_receipts GROUP BY 1, 2 ORDER BY 1, 2 LIMIT 200";

    private const string SequenceSql = "SELECT max(ingress_sequence) FROM raw_receipts";

    private const string LedgerSql =
        "SELECT outcome::text, coalesce(reason_code, ''), coalesce(quarantine_reason::text, ''), count(*) "
        + "FROM event_ledger GROUP BY 1, 2, 3 ORDER BY 1, 2, 3 LIMIT 200";

    private const string TargetsSql =
        "SELECT state::text, coalesce(last_outcome, ''), count(*) FROM reconciliation_targets GROUP BY 1, 2 ORDER BY 1, 2 LIMIT 200";

    private const string SyncsSql =
        "SELECT connection_id, generation, state::text, coalesce(last_outcome, ''), scope_version, replay_floor, cutover_fence, live_at_utc "
        + "FROM connection_syncs ORDER BY connection_id LIMIT 200";

    private const string SyncScopesSql =
        "SELECT connection_id, generation, resource_id, state::text, coalesce(last_outcome, ''), pages_read, objects_read, fields_applied "
        + "FROM connection_sync_scopes ORDER BY connection_id, generation, resource_id LIMIT 200";

    private const string SourceScopesSql =
        "SELECT connection_id, resource_id, enabled, baseline_generation FROM connector_source_scopes ORDER BY connection_id, resource_id LIMIT 200";

    private const string AuthorizationsSql =
        "SELECT connection_id, state::text, generation, authorized_at_utc, refreshed_at_utc, revoked_at_utc, provider_revocation_confirmed "
        + "FROM connector_authorizations ORDER BY connection_id LIMIT 200";

    private const string TransactionsSql =
        "SELECT count(*) FILTER (WHERE consumed_at_utc IS NULL), "
        + "count(*) FILTER (WHERE consumed_at_utc IS NOT NULL AND verifier_deleted_at_utc IS NULL), "
        + "count(*) FILTER (WHERE verifier_deleted_at_utc IS NOT NULL) FROM connector_authorization_transactions";

    private readonly ITenantScopedDatabase _database;

    /// <summary>Creates the reader.</summary>
    /// <param name="database">The tenant-scoped database of the run.</param>
    public OperationalEvidenceReader(ITenantScopedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Reads the trusted tenant's operational metadata.</summary>
    public Task<Result<OperationalEvidence>> ReadAsync(CancellationToken cancellationToken) =>
        _database.ExecuteAsync(
            async (dbContext, token) =>
            {
                var receipts = await EvidenceSql.ReadAsync(
                    dbContext, ReceiptsSql, row => new EvidenceCount([row.GetString(0), row.GetString(1)], row.GetInt64(2)), token);
                var sequence = await EvidenceSql.ReadAsync(
                    dbContext, SequenceSql, row => row.IsDBNull(0) ? (long?)null : row.GetInt64(0), token);
                var ledger = await EvidenceSql.ReadAsync(
                    dbContext, LedgerSql, row => new EvidenceCount([row.GetString(0), row.GetString(1), row.GetString(2)], row.GetInt64(3)), token);
                var targets = await EvidenceSql.ReadAsync(
                    dbContext, TargetsSql, row => new EvidenceCount([row.GetString(0), row.GetString(1)], row.GetInt64(2)), token);
                var syncs = await EvidenceSql.ReadAsync(
                    dbContext,
                    SyncsSql,
                    row => new SyncEvidence(
                        row.GetGuid(0),
                        row.GetInt64(1),
                        row.GetString(2),
                        row.GetString(3),
                        row.GetInt64(4),
                        row.GetInt64(5),
                        row.IsDBNull(6) ? null : row.GetInt64(6),
                        row.IsDBNull(7) ? null : row.GetFieldValue<DateTimeOffset>(7)),
                    token);
                var syncScopes = await EvidenceSql.ReadAsync(
                    dbContext,
                    SyncScopesSql,
                    row => new SyncScopeEvidence(
                        row.GetGuid(0), row.GetInt64(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetInt32(5), row.GetInt32(6), row.GetInt32(7)),
                    token);
                var sourceScopes = await EvidenceSql.ReadAsync(
                    dbContext,
                    SourceScopesSql,
                    row => new SourceScopeEvidence(row.GetGuid(0), row.GetString(1), row.GetBoolean(2), row.IsDBNull(3) ? null : row.GetInt64(3)),
                    token);
                var authorizations = await EvidenceSql.ReadAsync(
                    dbContext,
                    AuthorizationsSql,
                    row => new AuthorizationEvidence(
                        row.GetGuid(0),
                        row.GetString(1),
                        row.GetInt64(2),
                        row.GetFieldValue<DateTimeOffset>(3),
                        row.IsDBNull(4) ? null : row.GetFieldValue<DateTimeOffset>(4),
                        row.IsDBNull(5) ? null : row.GetFieldValue<DateTimeOffset>(5),
                        !row.IsDBNull(6) && row.GetBoolean(6)),
                    token);
                var transactions = await EvidenceSql.ReadAsync(
                    dbContext, TransactionsSql, row => new TransactionEvidence(row.GetInt64(0), row.GetInt64(1), row.GetInt64(2)), token);

                return new OperationalEvidence(
                    receipts,
                    sequence.Count == 1 ? sequence[0] : null,
                    ledger,
                    targets,
                    syncs,
                    syncScopes,
                    sourceScopes,
                    authorizations,
                    transactions.Count == 1 ? transactions[0] : new TransactionEvidence(0, 0, 0));
            },
            cancellationToken);
}

/// <summary>What the catalog says about the role the harness connects as.</summary>
/// <param name="Superuser">The role is a superuser.</param>
/// <param name="BypassesRowSecurity">The role has BYPASSRLS.</param>
/// <param name="CreatesRoles">The role has CREATEROLE.</param>
/// <param name="CreatesDatabases">The role has CREATEDB.</param>
/// <param name="Replicates">The role has REPLICATION.</param>
/// <param name="InheritsPrivileges">The role has INHERIT rather than NOINHERIT.</param>
/// <param name="CannotLogIn">The role does not have LOGIN.</param>
/// <param name="OwnsDatabase">The role owns the current database, or is a member of a role that does.</param>
/// <param name="OwnsSchema">The role owns a schema of the database, or is a member of a role that does.</param>
/// <param name="OwnsRelation">
/// The role owns a table, partitioned table, view, materialized view, sequence or foreign table of the database outside
/// the temporary schemas, or is a member of a role that does.
/// </param>
/// <param name="MemberOfAnyRole">
/// The role is a member of another role, directly or through any chain of memberships, or the answer about its
/// memberships could not be read (owner decision B-14, hardened by the owner's Decision 3 of 2026-10-06: membership
/// itself is disqualifying, whatever its options).
/// </param>
public sealed record DatabaseRoleFacts(
    bool Superuser,
    bool BypassesRowSecurity,
    bool CreatesRoles,
    bool CreatesDatabases,
    bool Replicates,
    bool InheritsPrivileges,
    bool CannotLogIn,
    bool OwnsDatabase,
    bool OwnsSchema,
    bool OwnsRelation,
    bool MemberOfAnyRole)
{
    /// <summary>What the harness assumes when the catalog does not answer about the role: every privilege.</summary>
    public static DatabaseRoleFacts Unknown { get; } =
        new(true, true, true, true, true, true, true, true, true, true, true);

    /// <summary>Gets whether the role is anything other than the plain runtime role: any one of the eleven is enough.</summary>
    public bool IsPrivileged =>
        Superuser || BypassesRowSecurity || CreatesRoles || CreatesDatabases || Replicates || InheritsPrivileges || CannotLogIn
        || OwnsDatabase || OwnsSchema || OwnsRelation || MemberOfAnyRole;
}

/// <summary>
/// The one row the recursive membership statement answers: how many roles are the current role, how many roles it is a
/// member of through <c>pg_auth_members</c>, directly or transitively, and whether any of them is privileged.
/// </summary>
/// <param name="CurrentRoleRows">The roles named like the current role; exactly one in a well-formed catalog.</param>
/// <param name="GrantedRoles">The distinct roles the current role is a member of, at any depth.</param>
/// <param name="PrivilegedGrantedRole">
/// One of them is a superuser, has BYPASSRLS, owns a table of the schema, or has no catalog entry; null when the answer
/// did not say.
/// </param>
public sealed record RoleMembershipFacts(long CurrentRoleRows, long GrantedRoles, bool? PrivilegedGrantedRole)
{
    /// <summary>
    /// True unless the database answered exactly one well-formed row in which the current role is found exactly once, is
    /// a member of no role at all, and so of no privileged one (the owner's Decision 3 of 2026-10-06: membership itself
    /// is disqualifying). Every other answer fails closed.
    /// </summary>
    /// <param name="rows">The rows the statement answered.</param>
    public static bool IsPrivileged(IReadOnlyList<RoleMembershipFacts> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count != 1
            || rows[0].CurrentRoleRows != 1
            || rows[0].GrantedRoles != 0
            || rows[0].PrivilegedGrantedRole is not false;
    }
}

/// <summary>
/// Whether the role the harness connects as is anything other than the plain runtime role.
/// </summary>
/// <remarks>
/// <para>
/// A superuser, a role with BYPASSRLS and the owner of the schema's tables are not subject to the policies that isolate
/// tenants, and the owner can change the schema; a member of the owning role can become it. The harness therefore
/// refuses to start as any of them (owner decisions D3G-2 and D3G-4): it holds the runtime role's connection only. The
/// check reads the catalog about the current role and no data, inside the tenant-scoped transaction like every other
/// harness read. A role the catalog does not answer about counts as privileged.
/// </para>
/// <para>
/// Hardened by the owner's Decision 3 of 2026-10-06: the role must be exactly the plain role the runtime-role procedure
/// creates, LOGIN, NOSUPERUSER, NOBYPASSRLS, NOCREATEROLE, NOCREATEDB, NOREPLICATION and NOINHERIT; it may own neither
/// the database, nor any schema, nor any table, view or sequence, directly or through a role it is a member of; and it
/// may be a member of no role at all. Each of these is read by itself, and any one of them refuses the start. There is no
/// allowlist, of predefined <c>pg_</c> roles or of any other. Temporary relations are left out of the ownership read: a
/// session may create those, and they change no one's privileges.
/// </para>
/// <para>
/// Membership (owner decision B-14). The statement follows <c>pg_auth_members</c> from the current role outwards, one
/// membership after another, and never stops at the immediate parent. The recursion is a <c>UNION</c>, which drops every
/// role already reached, so a cycle ends it and it visits each role at most once: it is bounded by the number of roles
/// in the catalog. Every membership counts, whatever its INHERIT, SET or ADMIN option. Since Decision 3, any role
/// reached at all refuses the start, so the statement's count of the roles reached decides; whether one of them is
/// privileged is still answered, and a privileged answer, a role reached through a membership that has no catalog entry
/// and an answer that is not exactly one well-formed row each refuse as well. The check changes nothing: it grants,
/// revokes and alters no role.
/// </para>
/// </remarks>
public sealed class DatabaseRoleCheck
{
    private const string RoleSql =
        "SELECT r.rolsuper, r.rolbypassrls, r.rolcreaterole, r.rolcreatedb, r.rolreplication, r.rolinherit, NOT r.rolcanlogin, "
        + "EXISTS (SELECT 1 FROM pg_database AS d WHERE d.datname = current_database() AND pg_has_role(current_user, d.datdba, 'MEMBER')), "
        + "EXISTS (SELECT 1 FROM pg_namespace AS n WHERE pg_has_role(current_user, n.nspowner, 'MEMBER')), "
        + "EXISTS (SELECT 1 FROM pg_class AS c WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f') AND c.relpersistence <> 't' "
        + "AND pg_has_role(current_user, c.relowner, 'MEMBER')) "
        + "FROM pg_roles AS r WHERE r.rolname = current_user";

    // The two catalog projections the recursion reads are kept as two separate common table expressions, so that the
    // harness's tests can run exactly this statement over a synthetic catalog of their own.
    private const string MembershipSql =
        "SELECT answer.current_role_rows, answer.granted_roles, answer.privileged_granted_role FROM ("
        + "WITH RECURSIVE "
        + "role_facts (oid, rolname, privileged) AS ("
        + "SELECT r.oid, r.rolname, (r.rolsuper OR r.rolbypassrls OR EXISTS (SELECT 1 FROM pg_tables AS t WHERE t.schemaname = 'public' AND t.tableowner = r.rolname)) "
        + "FROM pg_roles AS r), "
        + "memberships (member, roleid) AS ("
        + "SELECT m.member, m.roleid FROM pg_auth_members AS m), "
        + "current_role_row (oid) AS ("
        + "SELECT f.oid FROM role_facts AS f WHERE f.rolname = current_user), "
        + "granted (roleid) AS ("
        + "SELECT m.roleid FROM memberships AS m JOIN current_role_row AS c ON m.member = c.oid "
        + "UNION "
        + "SELECT m.roleid FROM memberships AS m JOIN granted AS g ON m.member = g.roleid) "
        + "SELECT (SELECT count(*) FROM current_role_row) AS current_role_rows, "
        + "(SELECT count(*) FROM granted) AS granted_roles, "
        + "EXISTS (SELECT 1 FROM granted AS g LEFT JOIN role_facts AS f ON f.oid = g.roleid WHERE f.privileged IS NOT FALSE) AS privileged_granted_role"
        + ") AS answer";

    private readonly ITenantScopedDatabase _database;

    /// <summary>Creates the check.</summary>
    /// <param name="database">The tenant-scoped database of the run.</param>
    public DatabaseRoleCheck(ITenantScopedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>The recursive membership statement, for the harness's own tests and the evidence procedure's pin.</summary>
    internal static string MembershipStatement => MembershipSql;

    /// <summary>Reads what the catalog says about the current role; a failure when the database cannot say.</summary>
    public async Task<Result<DatabaseRoleFacts>> ReadAsync(CancellationToken cancellationToken)
    {
        var read = await _database.ExecuteAsync(
            async (dbContext, token) =>
            {
                var role = await EvidenceSql.ReadAsync(
                    dbContext,
                    RoleSql,
                    row => new DatabaseRoleFacts(
                        row.IsDBNull(0) || row.GetBoolean(0),
                        row.IsDBNull(1) || row.GetBoolean(1),
                        row.IsDBNull(2) || row.GetBoolean(2),
                        row.IsDBNull(3) || row.GetBoolean(3),
                        row.IsDBNull(4) || row.GetBoolean(4),
                        row.IsDBNull(5) || row.GetBoolean(5),
                        row.IsDBNull(6) || row.GetBoolean(6),
                        row.IsDBNull(7) || row.GetBoolean(7),
                        row.IsDBNull(8) || row.GetBoolean(8),
                        row.IsDBNull(9) || row.GetBoolean(9),
                        false),
                    token);
                var membership = await EvidenceSql.ReadAsync(
                    dbContext,
                    MembershipSql,
                    row => new RoleMembershipFacts(
                        row.IsDBNull(0) ? -1 : row.GetInt64(0),
                        row.IsDBNull(1) ? -1 : row.GetInt64(1),
                        row.IsDBNull(2) ? null : row.GetBoolean(2)),
                    token);
                return role.Count == 1
                    ? role[0] with { MemberOfAnyRole = RoleMembershipFacts.IsPrivileged(membership) }
                    : DatabaseRoleFacts.Unknown;
            },
            cancellationToken);
        return read.IsFailure
            ? Result<DatabaseRoleFacts>.Failure(read.Error)
            : Result<DatabaseRoleFacts>.Success(read.Value);
    }

    /// <summary>True when the current role is not the plain runtime role; a failure when the database cannot say.</summary>
    public async Task<Result<bool>> IsPrivilegedAsync(CancellationToken cancellationToken)
    {
        var facts = await ReadAsync(cancellationToken);
        return facts.IsFailure ? Result<bool>.Failure(facts.Error) : Result<bool>.Success(facts.Value.IsPrivileged);
    }
}

/// <summary>Runs one fixed, parameterless SELECT on the tenant-scoped transaction's own connection.</summary>
internal static class EvidenceSql
{
    internal static async Task<IReadOnlyList<TRow>> ReadAsync<TRow>(
        WorkContinuityDbContext dbContext,
        string sql,
        Func<DbDataReader, TRow> map,
        CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        var rows = new List<TRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}
