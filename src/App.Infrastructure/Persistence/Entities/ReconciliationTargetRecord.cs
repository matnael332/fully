using WorkContinuity.Core.Events;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Infrastructure.Persistence.Entities;

/// <summary>Where a reconciliation target is in its lifecycle.</summary>
public enum ReconciliationTargetState
{
    /// <summary>Unspecified. Never persisted.</summary>
    None = 0,

    /// <summary>Its latest generation is requested and not yet resolved: it may be claimed when due.</summary>
    Pending = 1,

    /// <summary>Its latest generation was resolved: the Snapshot holds the source's confirmed state for the field.</summary>
    Resolved = 2,

    /// <summary>Unresolved and not retried: a new request reopens it.</summary>
    Held = 3,
}

/// <summary>What asked for a target's latest generation.</summary>
public enum ReconciliationRequestKind
{
    /// <summary>Unspecified. Never persisted.</summary>
    None = 0,

    /// <summary>Processing recorded a <c>ReconciliationRequired</c> outcome for the field.</summary>
    Event = 1,

    /// <summary>A missed-webhook sweep asked to verify a field already reconciled.</summary>
    Sweep = 2,
}

/// <summary>
/// One field of one known source object that reconciliation rereads (Gate 3E, decision D3E-11): its identity, its source
/// scope, and its request, claim, retry and result state.
/// </summary>
/// <remarks>
/// <para>
/// Operational metadata only: no column can hold a value, a label, a provider response, a credential or display text. One
/// row per tenant, object and field, the key of the field's ordering state and Snapshot field. Processing writes it in
/// the transaction that records a <c>ReconciliationRequired</c> outcome, so the identifiers that execute a reconciliation
/// are durable before the outcome is; rows are never deleted.
/// </para>
/// <para>
/// Versioned: every request raises the generation, a worker claims one generation, and only a worker whose claimed
/// generation is still the latest can resolve the target. A newer request is therefore never closed by older work, and a
/// claim that was lost or that lapsed resolves nothing. The transitions here apply only to a row the caller holds locked.
/// </para>
/// </remarks>
public sealed class ReconciliationTargetRecord : ITenantOwned
{
    /// <summary>The maximum stored length of an outcome code.</summary>
    public const int MaxOutcomeLength = 100;

    private ReconciliationTargetRecord()
    {
        // Materialization only: targets are created by processing's request, in SQL.
    }

    /// <summary>Gets the owning tenant.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>Gets the object's home system.</summary>
    public string ObjectSystem { get; private set; } = string.Empty;

    /// <summary>Gets the object's home connection.</summary>
    public string ObjectConnection { get; private set; } = string.Empty;

    /// <summary>Gets the object type token.</summary>
    public string ObjectType { get; private set; } = string.Empty;

    /// <summary>Gets the provider's object id.</summary>
    public string ObjectId { get; private set; } = string.Empty;

    /// <summary>Gets the field token.</summary>
    public string Field { get; private set; } = string.Empty;

    /// <summary>Gets the field's canonical data type.</summary>
    public FactDataType DataType { get; private set; }

    /// <summary>Gets the source scope the field id belongs to, when its source has one. Never replaced once stored.</summary>
    public string? SourceScope { get; private set; }

    /// <summary>Gets the lifecycle state.</summary>
    public ReconciliationTargetState State { get; private set; }

    /// <summary>Gets the latest requested generation.</summary>
    public long Generation { get; private set; }

    /// <summary>Gets the generation last resolved, if any.</summary>
    public long? ResolvedGeneration { get; private set; }

    /// <summary>Gets what asked for the latest generation.</summary>
    public ReconciliationRequestKind RequestKind { get; private set; }

    /// <summary>Gets when the latest generation was requested.</summary>
    public DateTimeOffset RequestedAtUtc { get; private set; }

    /// <summary>Gets the ledger event that requested the latest generation, or null for a sweep.</summary>
    public Guid? RequestedByEventId { get; private set; }

    /// <summary>Gets the claim of the worker reconciling it, if one is.</summary>
    public Guid? ClaimId { get; private set; }

    /// <summary>Gets the generation the current claim is for.</summary>
    public long? ClaimedGeneration { get; private set; }

    /// <summary>Gets when the current claim lapses.</summary>
    public DateTimeOffset? ClaimedUntilUtc { get; private set; }

    /// <summary>
    /// Gets the attempts at the latest generation in its current run of like failures: every claim counts one, and a
    /// failure of another disposition than the last (transient, awaiting access, or an unusable answer) starts the count
    /// again at that attempt. It sets the backoff, and it bounds only unusable answers.
    /// </summary>
    public int Attempts { get; private set; }

    /// <summary>Gets when it may be claimed again, if it is waiting.</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    /// <summary>Gets when it was last claimed.</summary>
    public DateTimeOffset? LastAttemptedAtUtc { get; private set; }

    /// <summary>Gets the code of the last recorded outcome.</summary>
    public string? LastOutcome { get; private set; }

    /// <summary>Gets when it was last resolved.</summary>
    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    /// <summary>Gets the confirmed ledger event that last resolved it, if one did.</summary>
    public Guid? ResolvedByEventId { get; private set; }

    /// <summary>Gets when the Snapshot was last verified to hold the source's state for the field.</summary>
    public DateTimeOffset? VerifiedAtUtc { get; private set; }

    /// <summary>Gets when the target was first requested.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets a value indicating whether the claim is the caller's.</summary>
    public bool IsClaimedBy(Guid claimId) => claimId != Guid.Empty && ClaimId == claimId;

    /// <summary>
    /// The attempts in the run of like failures this outcome would continue: the count so far when the last recorded
    /// outcome has the same disposition, otherwise one, this attempt.
    /// </summary>
    public int AttemptsWith(ReconciliationOutcome outcome) =>
        ReconciliationOutcomes.TryParse(LastOutcome, out var last)
        && ReconciliationOutcomes.DispositionOf(last) == ReconciliationOutcomes.DispositionOf(outcome)
            ? Math.Max(Attempts, 1)
            : 1;

    /// <summary>
    /// Resolves the claimed generation: the field holds the source's confirmed state. If a newer generation was requested
    /// meanwhile, it stays pending for that request, and only the claim is released.
    /// </summary>
    /// <exception cref="InvalidOperationException">The caller does not hold the claim, or the outcome does not resolve.</exception>
    public void Resolve(Guid claimId, ReconciliationOutcome outcome, Guid? resolvingEventId, DateTimeOffset now)
    {
        RequireClaim(claimId, outcome, ReconciliationDisposition.Resolve);
        LastOutcome = ReconciliationOutcomes.CodeOf(outcome);
        if (Generation == ClaimedGeneration)
        {
            State = ReconciliationTargetState.Resolved;
            ResolvedGeneration = Generation;
            ResolvedAtUtc = now.ToUniversalTime();
            VerifiedAtUtc = ResolvedAtUtc;
            ResolvedByEventId = resolvingEventId ?? ResolvedByEventId;
            Attempts = 0;
            NextAttemptAtUtc = null;
        }

        Release();
    }

    /// <summary>
    /// Records a failure to retry: the claimed generation waits until the given time. A transient failure, or one awaiting
    /// access, waits for as long as it lasts and is never held; an unusable answer is held once
    /// <paramref name="maxAttempts"/> consecutive attempts ended that way. A newer generation is left as its request set it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The caller does not hold the claim, or the outcome is not retried.</exception>
    public void Retry(Guid claimId, ReconciliationOutcome outcome, DateTimeOffset nextAttemptAt, int maxAttempts)
    {
        RequireClaim(claimId, outcome, ReconciliationDisposition.Retry, ReconciliationDisposition.AwaitAccess, ReconciliationDisposition.RetryThenHold);
        var attempts = AttemptsWith(outcome);
        LastOutcome = ReconciliationOutcomes.CodeOf(outcome);
        if (Generation == ClaimedGeneration)
        {
            Attempts = attempts;
            if (ReconciliationOutcomes.DispositionOf(outcome) == ReconciliationDisposition.RetryThenHold && attempts >= maxAttempts)
            {
                State = ReconciliationTargetState.Held;
                NextAttemptAtUtc = null;
            }
            else
            {
                NextAttemptAtUtc = nextAttemptAt.ToUniversalTime();
            }
        }

        Release();
    }

    /// <summary>
    /// Holds the claimed generation unresolved: a new request reopens it. A newer generation is left as its request set it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The caller does not hold the claim, or the outcome does not hold.</exception>
    public void Hold(Guid claimId, ReconciliationOutcome outcome)
    {
        RequireClaim(claimId, outcome, ReconciliationDisposition.Hold);
        LastOutcome = ReconciliationOutcomes.CodeOf(outcome);
        if (Generation == ClaimedGeneration)
        {
            State = ReconciliationTargetState.Held;
            NextAttemptAtUtc = null;
        }

        Release();
    }

    private void RequireClaim(Guid claimId, ReconciliationOutcome outcome, params ReconciliationDisposition[] expected)
    {
        if (!IsClaimedBy(claimId))
        {
            throw new InvalidOperationException("Only the worker holding the claim records its outcome.");
        }

        if (!expected.Contains(ReconciliationOutcomes.DispositionOf(outcome)))
        {
            throw new InvalidOperationException("The outcome does not have this disposition.");
        }
    }

    private void Release()
    {
        ClaimId = null;
        ClaimedGeneration = null;
        ClaimedUntilUtc = null;
    }
}
