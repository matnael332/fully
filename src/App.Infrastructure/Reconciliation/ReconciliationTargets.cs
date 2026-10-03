using Microsoft.EntityFrameworkCore;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Infrastructure.Reconciliation;

/// <summary>
/// Requests reconciliation of the fields an event holds (Gate 3E, decision D3E-11), and records the fields initial sync
/// establishes (Gate 3F, decision D3F-8), in the one target model.
/// </summary>
/// <remarks>
/// <para>
/// Processing calls it in the transaction that records a <c>ReconciliationRequired</c> outcome, after the ledger entry is
/// written, so the outcome and the identifiers that execute it commit together or not at all. One statement per field,
/// atomic under concurrent requests: a new target starts pending at generation 1; an existing one rises by a generation,
/// its attempts reset, and it is pending again, so a worker that claimed an older generation can never resolve this one.
/// </para>
/// <para>
/// A target's identity is never replaced. A request that names another source scope or data type than the target holds,
/// such as the same object seen in another scope, raises the generation but holds the target with a scope-conflict outcome:
/// nothing is remapped, and the conflict is left to later lifecycle work. Values never reach this table.
/// </para>
/// <para>
/// Initial sync records each field its baseline read, in the transaction that applied the read, with the request kind
/// <c>Sync</c>: a new target starts resolved, or held with its outcome, at generation 1; a known one is requested and
/// settled again in the same step, so a worker that claimed an older generation can never close this one. A field seen
/// in another scope than its known target holds is held with the scope-conflict outcome, as a request would hold it, and
/// nothing from the read is applied.
/// </para>
/// <para>
/// When a scope is enabled again, or for the first time, every field already known in it awaits a baseline: its earlier
/// state is not current work until a baseline read of the scope resolves the field again. Nothing else clears the mark,
/// so a field the new baseline no longer finds, or does not confirm, never becomes current with its stale state.
/// </para>
/// </remarks>
internal static class ReconciliationTargets
{
    private static readonly string ScopeConflict = ReconciliationOutcomes.CodeOf(ReconciliationOutcome.ScopeConflict);

    /// <summary>The scope and data type of a field's known target, which never change once stored.</summary>
    internal sealed record KnownTarget(string? SourceScope, FactDataType DataType)
    {
        /// <summary>Whether the target is the same field: the same scope and the same data type.</summary>
        internal bool Matches(ReconciliationTarget target) =>
            string.Equals(SourceScope, target.SourceScope, StringComparison.Ordinal) && DataType == target.DataType;
    }

    /// <summary>Requests reconciliation of every field the held result names.</summary>
    /// <exception cref="ArgumentException">The result is not a held event.</exception>
    internal static async Task RequestAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        EventProcessingResult result,
        string? sourceScope,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        if (result.Outcome != EventProcessingOutcome.ReconciliationRequired || result.WorkObject is not { } workObject)
        {
            throw new ArgumentException("Only an event held for reconciliation requests it.", nameof(result));
        }

        var eventId = result.EventId.Value;
        var at = requestedAt.ToUniversalTime();
        foreach (var field in result.Fields)
        {
            var dataType = field.DataType.ToString();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO public.reconciliation_targets AS t (
                    tenant_id, object_system, object_connection, object_type, object_id, field, data_type, source_scope,
                    state, generation, request_kind, requested_at_utc, requested_by_event_id, attempts, created_at_utc)
                VALUES (
                    {tenant.Value}, {workObject.SourceSystem}, {workObject.ConnectionReference}, {workObject.ObjectType},
                    {workObject.SourceObjectId}, {field.Field}, {dataType}, {sourceScope},
                    'Pending', 1, 'Event', {at}, {eventId}, 0, {at})
                ON CONFLICT (tenant_id, object_system, object_connection, object_type, object_id, field) DO UPDATE SET
                    generation = t.generation + 1,
                    request_kind = 'Event',
                    requested_at_utc = EXCLUDED.requested_at_utc,
                    requested_by_event_id = EXCLUDED.requested_by_event_id,
                    attempts = 0,
                    next_attempt_at_utc = NULL,
                    state = CASE
                        WHEN t.source_scope IS NOT DISTINCT FROM EXCLUDED.source_scope AND t.data_type = EXCLUDED.data_type
                        THEN 'Pending' ELSE 'Held' END,
                    last_outcome = CASE
                        WHEN t.source_scope IS NOT DISTINCT FROM EXCLUDED.source_scope AND t.data_type = EXCLUDED.data_type
                        THEN t.last_outcome ELSE {ScopeConflict} END
                """,
                cancellationToken);
        }
    }

    /// <summary>
    /// Locks a field's known target, in the caller's transaction, as reconciliation locks it before it applies a read.
    /// </summary>
    /// <returns>The target's scope and data type, or null when no target is known for the field.</returns>
    internal static async Task<KnownTarget?> LockAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        ReconciliationTarget target,
        CancellationToken cancellationToken)
    {
        var workObject = target.WorkObject;
        var rows = await dbContext.Database.SqlQuery<KnownRow>(
                $"""
                SELECT source_scope AS "SourceScope", data_type AS "DataType"
                FROM public.reconciliation_targets
                WHERE tenant_id = {tenant.Value} AND object_system = {workObject.SourceSystem}
                  AND object_connection = {workObject.ConnectionReference} AND object_type = {workObject.ObjectType}
                  AND object_id = {workObject.SourceObjectId} AND field = {target.Field}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        return rows.Count == 1 ? new KnownTarget(rows[0].SourceScope, Enum.Parse<FactDataType>(rows[0].DataType)) : null;
    }

    /// <summary>
    /// Records what initial sync established for a field whose known target, if any, is the same field: resolved when the
    /// outcome resolves it, held with its outcome otherwise. Written in the transaction that applied the read.
    /// </summary>
    internal static async Task EstablishAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        ReconciliationTarget target,
        ReconciliationOutcome outcome,
        Guid? resolvingEventId,
        DateTimeOffset establishedAt,
        CancellationToken cancellationToken)
    {
        var workObject = target.WorkObject;
        var at = establishedAt.ToUniversalTime();
        var dataType = target.DataType.ToString();
        var code = ReconciliationOutcomes.CodeOf(outcome);
        var resolved = ReconciliationOutcomes.DispositionOf(outcome) == ReconciliationDisposition.Resolve;
        var state = resolved ? "Resolved" : "Held";
        DateTimeOffset? resolvedAt = resolved ? at : null;
        Guid? eventId = resolved ? resolvingEventId : null;
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.reconciliation_targets AS t (
                tenant_id, object_system, object_connection, object_type, object_id, field, data_type, source_scope,
                state, generation, resolved_generation, request_kind, requested_at_utc, requested_by_event_id, attempts,
                last_outcome, resolved_at_utc, resolved_by_event_id, verified_at_utc, created_at_utc)
            VALUES (
                {tenant.Value}, {workObject.SourceSystem}, {workObject.ConnectionReference}, {workObject.ObjectType},
                {workObject.SourceObjectId}, {target.Field}, {dataType}, {target.SourceScope},
                {state}, 1, CASE WHEN {resolved} THEN 1 END, 'Sync', {at}, NULL, 0,
                {code}, {resolvedAt}, {eventId}, {resolvedAt}, {at})
            ON CONFLICT (tenant_id, object_system, object_connection, object_type, object_id, field) DO UPDATE SET
                generation = t.generation + 1,
                state = EXCLUDED.state,
                resolved_generation = CASE WHEN {resolved} THEN t.generation + 1 ELSE t.resolved_generation END,
                request_kind = 'Sync',
                requested_at_utc = EXCLUDED.requested_at_utc,
                requested_by_event_id = NULL,
                attempts = 0,
                next_attempt_at_utc = NULL,
                last_outcome = EXCLUDED.last_outcome,
                resolved_at_utc = COALESCE(EXCLUDED.resolved_at_utc, t.resolved_at_utc),
                resolved_by_event_id = COALESCE(EXCLUDED.resolved_by_event_id, t.resolved_by_event_id),
                verified_at_utc = COALESCE(EXCLUDED.verified_at_utc, t.verified_at_utc),
                awaiting_baseline_version = CASE WHEN {resolved} THEN NULL ELSE t.awaiting_baseline_version END
            WHERE t.source_scope IS NOT DISTINCT FROM EXCLUDED.source_scope AND t.data_type = EXCLUDED.data_type
            """,
            cancellationToken);
    }

    /// <summary>
    /// Marks every field already known in a scope of a connection as awaiting a baseline, in the transaction that enables
    /// the scope, at the connection's scope-set version.
    /// </summary>
    internal static Task AwaitBaselineAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        string sourceSystem,
        string connectionReference,
        string sourceScope,
        long scopeVersion,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE public.reconciliation_targets
            SET awaiting_baseline_version = {scopeVersion}
            WHERE tenant_id = {tenant.Value} AND object_system = {sourceSystem}
              AND object_connection = {connectionReference} AND source_scope = {sourceScope}
            """,
            cancellationToken);

    /// <summary>
    /// Holds a field's known target that initial sync found in another scope or of another data type, as a request naming
    /// another scope holds it: the generation rises and nothing is remapped.
    /// </summary>
    internal static Task ConflictAsync(
        WorkContinuityDbContext dbContext,
        TenantId tenant,
        ReconciliationTarget target,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        var workObject = target.WorkObject;
        var at = requestedAt.ToUniversalTime();
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE public.reconciliation_targets AS t
            SET generation = t.generation + 1, state = 'Held', request_kind = 'Sync', requested_at_utc = {at},
                requested_by_event_id = NULL, attempts = 0, next_attempt_at_utc = NULL, last_outcome = {ScopeConflict}
            WHERE tenant_id = {tenant.Value} AND object_system = {workObject.SourceSystem}
              AND object_connection = {workObject.ConnectionReference} AND object_type = {workObject.ObjectType}
              AND object_id = {workObject.SourceObjectId} AND field = {target.Field}
            """,
            cancellationToken);
    }

    private sealed class KnownRow
    {
        public string? SourceScope { get; init; }

        public string DataType { get; init; } = string.Empty;
    }
}
