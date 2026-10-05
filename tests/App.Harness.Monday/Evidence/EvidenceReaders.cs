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
/// <param name="OwnsTables">The role owns a table of the schema, or is a member of a role that does.</param>
public sealed record DatabaseRoleFacts(bool Superuser, bool BypassesRowSecurity, bool OwnsTables)
{
    /// <summary>What the harness assumes when the catalog does not answer about the role: every privilege.</summary>
    public static DatabaseRoleFacts Unknown { get; } = new(true, true, true);

    /// <summary>Gets whether the role could step around row-level security: any one of the three is enough.</summary>
    public bool IsPrivileged => Superuser || BypassesRowSecurity || OwnsTables;
}

/// <summary>
/// Whether the role the harness connects as could step around row-level security.
/// </summary>
/// <remarks>
/// A superuser, a role with BYPASSRLS and the owner of the schema's tables are not subject to the policies that isolate
/// tenants, and the owner can change the schema; a member of the owning role can become it. The harness therefore
/// refuses to start as any of them (owner decisions D3G-2 and D3G-4): it holds the runtime role's connection only. The
/// check reads the catalog about the current role and no data, inside the tenant-scoped transaction like every other
/// harness read. A role the catalog does not answer about counts as privileged.
/// </remarks>
public sealed class DatabaseRoleCheck
{
    private const string RoleSql =
        "SELECT r.rolsuper, r.rolbypassrls, "
        + "EXISTS (SELECT 1 FROM pg_tables AS t WHERE t.schemaname = 'public' AND pg_has_role(current_user, t.tableowner, 'MEMBER')) "
        + "FROM pg_roles AS r WHERE r.rolname = current_user";

    private readonly ITenantScopedDatabase _database;

    /// <summary>Creates the check.</summary>
    /// <param name="database">The tenant-scoped database of the run.</param>
    public DatabaseRoleCheck(ITenantScopedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Reads what the catalog says about the current role; a failure when the database cannot say.</summary>
    public async Task<Result<DatabaseRoleFacts>> ReadAsync(CancellationToken cancellationToken)
    {
        var read = await _database.ExecuteAsync(
            (dbContext, token) => EvidenceSql.ReadAsync(
                dbContext,
                RoleSql,
                row => new DatabaseRoleFacts(
                    row.IsDBNull(0) || row.GetBoolean(0),
                    row.IsDBNull(1) || row.GetBoolean(1),
                    row.IsDBNull(2) || row.GetBoolean(2)),
                token),
            cancellationToken);
        return read.IsFailure
            ? Result<DatabaseRoleFacts>.Failure(read.Error)
            : Result<DatabaseRoleFacts>.Success(read.Value.Count == 1 ? read.Value[0] : DatabaseRoleFacts.Unknown);
    }

    /// <summary>True when the current role is privileged or owns a table; a failure when the database cannot say.</summary>
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
