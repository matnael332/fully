using Microsoft.EntityFrameworkCore;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence;

namespace WorkContinuity.Infrastructure.Reconciliation;

/// <summary>Requests reconciliation of the fields an event holds (Gate 3E, decision D3E-11).</summary>
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
/// </remarks>
internal static class ReconciliationTargets
{
    private static readonly string ScopeConflict = ReconciliationOutcomes.CodeOf(ReconciliationOutcome.ScopeConflict);

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
}
