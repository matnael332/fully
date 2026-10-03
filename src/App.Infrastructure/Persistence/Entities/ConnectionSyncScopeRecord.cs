using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>Where one scope of one initial-sync generation stands.</summary>
public enum SyncScopeState
{
    /// <summary>Unspecified. Never persisted.</summary>
    None = 0,

    /// <summary>Its baseline has not completed yet.</summary>
    Pending = 1,

    /// <summary>Every page was read and every supported field applied.</summary>
    Completed = 2,

    /// <summary>Retrying cannot complete it: the generation fails and a new one is needed.</summary>
    Held = 3,
}

/// <summary>
/// One configured scope of one initial-sync generation and its progress (Gate 3F): the generation's scope set is fixed
/// when it starts.
/// </summary>
/// <remarks>
/// Metadata only: the state, the run of like failures, the last outcome code and counts. No continuation, value, label or
/// response is stored; a resumed scan starts the scope again from its first page. Rows are never deleted.
/// </remarks>
public sealed class ConnectionSyncScopeRecord : ITenantOwned
{
    private ConnectionSyncScopeRecord()
    {
        // Materialization only.
    }

    /// <summary>Gets the owning tenant.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the connection.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>Gets the generation.</summary>
    public long Generation { get; private set; }

    /// <summary>Gets the scope's resource id.</summary>
    public string ResourceId { get; private set; } = string.Empty;

    /// <summary>Gets the state.</summary>
    public SyncScopeState State { get; private set; }

    /// <summary>Gets the attempts in the current run of like failures.</summary>
    public int Attempts { get; private set; }

    /// <summary>Gets the code of the last outcome.</summary>
    public string? LastOutcome { get; private set; }

    /// <summary>Gets how many pages the completed scan read.</summary>
    public int PagesRead { get; private set; }

    /// <summary>Gets how many objects the completed scan read.</summary>
    public int ObjectsRead { get; private set; }

    /// <summary>Gets how many fields the completed scan applied or found current.</summary>
    public int FieldsApplied { get; private set; }

    /// <summary>Gets when the scope completed.</summary>
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>A scope of a new generation, not read yet.</summary>
    public static ConnectionSyncScopeRecord Plan(TenantId tenant, Guid connectionId, long generation, string resourceId) =>
        new()
        {
            TenantId = tenant,
            ConnectionId = connectionId,
            Generation = generation,
            ResourceId = resourceId,
            State = SyncScopeState.Pending,
        };

    /// <summary>The scope's baseline completed.</summary>
    public void Complete(int pagesRead, int objectsRead, int fieldsApplied, DateTimeOffset now)
    {
        RequirePending();
        State = SyncScopeState.Completed;
        Attempts = 0;
        LastOutcome = SyncScopeOutcomes.CodeOf(SyncScopeOutcome.Completed);
        PagesRead = pagesRead;
        ObjectsRead = objectsRead;
        FieldsApplied = fieldsApplied;
        CompletedAtUtc = now.ToUniversalTime();
    }

    /// <summary>
    /// Records a failure of the scope: held at once when retrying cannot change it, held once a bounded failure reached
    /// <paramref name="maxAttempts"/> consecutive attempts, and pending otherwise.
    /// </summary>
    /// <returns>The attempts in the run of like failures this outcome continues.</returns>
    public int RecordFailure(SyncScopeOutcome outcome, int maxAttempts)
    {
        RequirePending();
        var disposition = SyncScopeOutcomes.DispositionOf(outcome);
        var attempts = SyncScopeOutcomes.TryParse(LastOutcome, out var last) && SyncScopeOutcomes.DispositionOf(last) == disposition
            ? Attempts + 1
            : 1;
        Attempts = attempts;
        LastOutcome = SyncScopeOutcomes.CodeOf(outcome);
        if (disposition == ReconciliationDisposition.Hold
            || (disposition == ReconciliationDisposition.RetryThenHold && attempts >= maxAttempts))
        {
            State = SyncScopeState.Held;
        }

        return attempts;
    }

    private void RequirePending()
    {
        if (State != SyncScopeState.Pending)
        {
            throw new InvalidOperationException("Only a pending scope changes.");
        }
    }
}
