using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Core.Reconciliation;

/// <summary>
/// One field of one source object that reconciliation rereads: the object, the field token, its canonical data type and
/// the source scope the field id belongs to.
/// </summary>
/// <remarks>
/// Identifiers only (D3E-11): no value, label or display text. The scope is what the source must still report the object
/// in for the field id to mean the same field; a reader whose source scopes its fields refuses a target without one
/// (D3E-10). The string form names ids only.
/// </remarks>
public sealed record ReconciliationTarget
{
    /// <summary>Initializes a new instance of the <see cref="ReconciliationTarget"/> class.</summary>
    /// <exception cref="ArgumentNullException">The object is null.</exception>
    /// <exception cref="ArgumentException">A component is malformed.</exception>
    public ReconciliationTarget(ObjectReference workObject, string field, FactDataType dataType, string? sourceScope)
    {
        ArgumentNullException.ThrowIfNull(workObject);
        if (!workObject.IsWellFormed)
        {
            throw new ArgumentException("A reconciliation target names a well-formed object.", nameof(workObject));
        }

        if (!EventText.IsToken(field))
        {
            throw new ArgumentException("A field is a token.", nameof(field));
        }

        if (dataType == FactDataType.None || !Enum.IsDefined(dataType))
        {
            throw new ArgumentException("A field has a defined data type.", nameof(dataType));
        }

        if (sourceScope is not null && !EventText.IsIdentifier(sourceScope))
        {
            throw new ArgumentException("A source scope is a bounded, opaque identifier.", nameof(sourceScope));
        }

        WorkObject = workObject;
        Field = field;
        DataType = dataType;
        SourceScope = sourceScope;
    }

    /// <summary>Gets the object, identified by its home system, connection, type and provider id.</summary>
    public ObjectReference WorkObject { get; }

    /// <summary>Gets the field token.</summary>
    public string Field { get; }

    /// <summary>Gets the field's canonical data type.</summary>
    public FactDataType DataType { get; }

    /// <summary>Gets the source scope the field id belongs to, when the source has one.</summary>
    public string? SourceScope { get; }

    /// <summary>Identifies the target by ids only.</summary>
    public override string ToString() =>
        $"ReconciliationTarget({WorkObject.SourceSystem}, {WorkObject.ObjectType} {WorkObject.SourceObjectId}, {Field})";
}

/// <summary>One authoritative read: the targets of one source and connection, under the tenant's trusted context.</summary>
/// <remarks>
/// A read never spans tenants or connections: every target names the same source system and connection reference, and
/// the tenant is the one reconciliation runs for, never something a source said.
/// </remarks>
public sealed class AuthoritativeReadRequest
{
    /// <summary>Initializes a new instance of the <see cref="AuthoritativeReadRequest"/> class.</summary>
    /// <exception cref="ArgumentNullException">The targets are null.</exception>
    /// <exception cref="ArgumentException">A component is missing, or the targets span sources, connections or duplicates.</exception>
    public AuthoritativeReadRequest(
        TenantId tenant,
        Guid connectionId,
        CorrelationId correlationId,
        IReadOnlyList<ReconciliationTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (tenant.IsEmpty || connectionId == Guid.Empty || correlationId.IsEmpty)
        {
            throw new ArgumentException("A read names its tenant, connection and correlation.", nameof(tenant));
        }

        if (targets.Count == 0 || targets.Any(target => target is null))
        {
            throw new ArgumentException("A read has targets.", nameof(targets));
        }

        var first = targets[0].WorkObject;
        if (targets.Any(target => target.WorkObject.SourceSystem != first.SourceSystem
                || target.WorkObject.ConnectionReference != first.ConnectionReference))
        {
            throw new ArgumentException("A read never spans source systems or connections.", nameof(targets));
        }

        if (targets.Select(target => (target.WorkObject.ObjectType, target.WorkObject.SourceObjectId, target.Field)).Distinct().Count() != targets.Count)
        {
            throw new ArgumentException("A read names each target once.", nameof(targets));
        }

        Tenant = tenant;
        ConnectionId = connectionId;
        CorrelationId = correlationId;
        SourceSystem = first.SourceSystem;
        ConnectionReference = first.ConnectionReference;
        Targets = targets.ToArray();
    }

    /// <summary>Gets the trusted tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the connection the read uses.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets the connection reference every target is homed at.</summary>
    public string ConnectionReference { get; }

    /// <summary>Gets the correlation of the reconciliation run.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>Gets the source system every target belongs to.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the targets.</summary>
    public IReadOnlyList<ReconciliationTarget> Targets { get; }

    /// <summary>Identifies the read by size only.</summary>
    public override string ToString() => $"AuthoritativeReadRequest({SourceSystem}, {Targets.Count} targets)";
}

/// <summary>
/// The exact provider values a reader consumed from one response, as the source returned them, kept as the evidence of a
/// confirmed reconciliation event.
/// </summary>
/// <remarks>
/// Untrusted provider data: never a normalized or canonical rewrite, never the full response, never a credential. It is
/// stored only as a raw receipt with its own schema version, under the raw-content retention (D3E-11).
/// </remarks>
public sealed class ObservationEvidence
{
    /// <summary>The largest evidence document a reader may produce.</summary>
    public const int MaxDocumentLength = 16 * 1024;

    /// <summary>Initializes a new instance of the <see cref="ObservationEvidence"/> class.</summary>
    /// <exception cref="ArgumentException">The schema version is not a token, or the document is empty or too long.</exception>
    public ObservationEvidence(string schemaVersion, string document)
    {
        if (!EventText.IsToken(schemaVersion))
        {
            throw new ArgumentException("An evidence schema version is a token.", nameof(schemaVersion));
        }

        if (string.IsNullOrWhiteSpace(document) || document.Length > MaxDocumentLength)
        {
            throw new ArgumentException("Evidence is a bounded, non-empty document.", nameof(document));
        }

        SchemaVersion = schemaVersion;
        Document = document;
    }

    /// <summary>Gets the evidence schema version, distinct from every delivery schema of the source.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the evidence document. Untrusted provider data.</summary>
    public string Document { get; }

    /// <summary>Identifies the evidence without its content.</summary>
    public override string ToString() => $"ObservationEvidence({SchemaVersion})";
}

/// <summary>A field's state as an authoritative read of its source reported it.</summary>
public sealed class AuthoritativeFieldState
{
    /// <summary>Initializes a new instance of the <see cref="AuthoritativeFieldState"/> class.</summary>
    /// <param name="value">The value, or null when the field is empty.</param>
    /// <param name="sourceUpdatedAt">The source's own update time for the field, or null when it gave none it can be read safely.</param>
    /// <param name="evidence">The exact provider values the reader consumed.</param>
    /// <exception cref="ArgumentNullException">The evidence is null.</exception>
    /// <exception cref="ArgumentException">The value is not a valid canonical value: a reader refuses it instead.</exception>
    public AuthoritativeFieldState(string? value, DateTimeOffset? sourceUpdatedAt, ObservationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (value is not null && !EventText.IsValue(value))
        {
            throw new ArgumentException("An authoritative value is a valid canonical value, never cut to fit.", nameof(value));
        }

        Value = value;
        SourceUpdatedAt = sourceUpdatedAt?.ToUniversalTime();
        Evidence = evidence;
    }

    /// <summary>Gets the value, or null when the field is empty. Provider data: authoritative, never an instruction.</summary>
    public string? Value { get; }

    /// <summary>Gets the source's update time for the field, or null: never fabricated from any other time.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; }

    /// <summary>Gets the evidence.</summary>
    public ObservationEvidence Evidence { get; }

    /// <summary>Identifies the state without its value.</summary>
    public override string ToString() => "AuthoritativeFieldState(value redacted)";
}

/// <summary>What a reader reported for one target: its authoritative state, or why there is none.</summary>
public sealed class TargetReading
{
    private TargetReading(AuthoritativeFieldState? state, ReconciliationOutcome refusal)
    {
        State = state;
        Refusal = refusal;
    }

    /// <summary>Gets the observed state, or null when the read was refused.</summary>
    public AuthoritativeFieldState? State { get; }

    /// <summary>Gets why the read was refused, or <see cref="ReconciliationOutcome.None"/> when it was observed.</summary>
    public ReconciliationOutcome Refusal { get; }

    /// <summary>An observed state.</summary>
    /// <exception cref="ArgumentNullException">The state is null.</exception>
    public static TargetReading Observed(AuthoritativeFieldState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new TargetReading(state, ReconciliationOutcome.None);
    }

    /// <summary>A refused read.</summary>
    /// <exception cref="ArgumentException">The outcome is not one a reader may report.</exception>
    public static TargetReading Refused(ReconciliationOutcome outcome) =>
        ReconciliationOutcomes.IsReaderRefusal(outcome)
            ? new TargetReading(state: null, outcome)
            : throw new ArgumentException("A reader refuses with a reader outcome.", nameof(outcome));

    /// <summary>Identifies the reading without content.</summary>
    public override string ToString() => State is null ? $"TargetReading({Refusal})" : "TargetReading(observed)";
}

/// <summary>What one authoritative read returned: a reading per target, or one failure for the whole call.</summary>
public sealed class AuthoritativeReadResult
{
    private readonly IReadOnlyDictionary<ReconciliationTarget, TargetReading> _readings;

    private AuthoritativeReadResult(
        ConnectorIdentity? identity,
        ReconciliationOutcome failure,
        TimeSpan? retryAfter,
        IReadOnlyDictionary<ReconciliationTarget, TargetReading> readings)
    {
        Identity = identity;
        Failure = failure;
        RetryAfter = retryAfter;
        _readings = readings;
    }

    /// <summary>Gets the identity whose authority the read used, when it got that far.</summary>
    public ConnectorIdentity? Identity { get; }

    /// <summary>Gets the failure of the whole call, or <see cref="ReconciliationOutcome.None"/>.</summary>
    public ReconciliationOutcome Failure { get; }

    /// <summary>Gets how long the provider asked to wait, when it did.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>A call that read the targets, with the identity it used.</summary>
    /// <exception cref="ArgumentNullException">A component is null.</exception>
    public static AuthoritativeReadResult Read(ConnectorIdentity identity, IReadOnlyDictionary<ReconciliationTarget, TargetReading> readings)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(readings);
        return new AuthoritativeReadResult(identity, ReconciliationOutcome.None, retryAfter: null, readings);
    }

    /// <summary>A read that sent nothing, because the reader refused every target before calling its source.</summary>
    /// <exception cref="ArgumentNullException">The readings are null.</exception>
    /// <exception cref="ArgumentException">A reading is an observation, which only a call can produce.</exception>
    public static AuthoritativeReadResult Refused(IReadOnlyDictionary<ReconciliationTarget, TargetReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Values.Any(reading => reading.State is not null))
        {
            throw new ArgumentException("Only a call to the source observes a state.", nameof(readings));
        }

        return new AuthoritativeReadResult(identity: null, ReconciliationOutcome.None, retryAfter: null, readings);
    }

    /// <summary>A call that failed as a whole.</summary>
    /// <exception cref="ArgumentException">The outcome is not one a reader may report, or the wait is negative.</exception>
    public static AuthoritativeReadResult Failed(ReconciliationOutcome failure, ConnectorIdentity? identity, TimeSpan? retryAfter = null)
    {
        if (!ReconciliationOutcomes.IsReaderRefusal(failure))
        {
            throw new ArgumentException("A read fails with a reader outcome.", nameof(failure));
        }

        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentException("A provider's requested wait cannot be negative.", nameof(retryAfter));
        }

        return new AuthoritativeReadResult(identity, failure, retryAfter, new Dictionary<ReconciliationTarget, TargetReading>());
    }

    /// <summary>
    /// The reading for one target: the call's failure, the target's own reading, or an unsupported response when the reader
    /// returned nothing for it.
    /// </summary>
    public TargetReading ReadingFor(ReconciliationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Failure != ReconciliationOutcome.None)
        {
            return TargetReading.Refused(Failure);
        }

        return _readings.TryGetValue(target, out var reading) ? reading : TargetReading.Refused(ReconciliationOutcome.UnsupportedResponse);
    }

    /// <summary>Identifies the result by outcome only.</summary>
    public override string ToString() => Failure == ReconciliationOutcome.None
        ? $"AuthoritativeReadResult({_readings.Count} readings)"
        : $"AuthoritativeReadResult({Failure})";
}

/// <summary>
/// Rereads the current authoritative state of known source objects (architecture §2: provider API reconciliation is the
/// correctness path). One reader serves one source system.
/// </summary>
/// <remarks>
/// A reader only reads: it never writes to the source, never decides what reaches the Snapshot, and never resolves
/// anything. It binds every answer to what it was asked: an answer for another object, scope or field, or of an
/// unsupported shape, is refused, never remapped. It reports values only through <see cref="AuthoritativeFieldState"/>,
/// with the exact provider values it consumed as evidence. It writes no log.
/// </remarks>
public interface IAuthoritativeSourceReader
{
    /// <summary>Gets the source system token this reader serves.</summary>
    string SourceSystem { get; }

    /// <summary>Gets the largest number of distinct objects one read may address.</summary>
    int MaxObjectsPerRead { get; }

    /// <summary>Reads the targets' current state from the source.</summary>
    Task<AuthoritativeReadResult> ReadAsync(AuthoritativeReadRequest request, CancellationToken cancellationToken);
}

/// <summary>One reconciliation run: one tenant, source system and connection, at most a bounded number of targets.</summary>
public sealed record ReconciliationRun
{
    /// <summary>The most targets one run may claim.</summary>
    public const int MaxTargetsPerRun = 500;

    /// <summary>Initializes a new instance of the <see cref="ReconciliationRun"/> class.</summary>
    /// <exception cref="ArgumentException">A component is missing or out of range.</exception>
    public ReconciliationRun(TenantId tenant, string sourceSystem, Guid connectionId, int maxTargets)
    {
        if (tenant.IsEmpty || connectionId == Guid.Empty || !EventText.IsToken(sourceSystem))
        {
            throw new ArgumentException("A run names its tenant, source system and connection.", nameof(tenant));
        }

        if (maxTargets is < 1 or > MaxTargetsPerRun)
        {
            throw new ArgumentException("A run claims between 1 and the maximum number of targets.", nameof(maxTargets));
        }

        Tenant = tenant;
        SourceSystem = sourceSystem;
        ConnectionId = connectionId;
        MaxTargets = maxTargets;
    }

    /// <summary>Gets the trusted tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the source system.</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the connection.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Gets the most targets the run may claim.</summary>
    public int MaxTargets { get; }
}

/// <summary>What one run did: how many targets it claimed and each outcome it recorded.</summary>
public sealed class ReconciliationRunSummary
{
    /// <summary>Initializes a new instance of the <see cref="ReconciliationRunSummary"/> class.</summary>
    public ReconciliationRunSummary(int claimed, IReadOnlyDictionary<ReconciliationOutcome, int> outcomes, int notApplied)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        Claimed = claimed;
        Outcomes = outcomes;
        NotApplied = notApplied;
    }

    /// <summary>Gets how many targets the run claimed.</summary>
    public int Claimed { get; }

    /// <summary>Gets how many targets ended with each outcome.</summary>
    public IReadOnlyDictionary<ReconciliationOutcome, int> Outcomes { get; }

    /// <summary>
    /// Gets how many claimed targets recorded nothing: their claim was lost to another worker, or their transaction did not
    /// commit. Their claim lapses and they are claimed again.
    /// </summary>
    public int NotApplied { get; }

    /// <summary>The number of targets that ended with an outcome.</summary>
    public int Count(ReconciliationOutcome outcome) => Outcomes.TryGetValue(outcome, out var count) ? count : 0;

    /// <summary>Identifies the summary by counts only.</summary>
    public override string ToString() => $"ReconciliationRunSummary({Claimed} claimed, {NotApplied} not applied)";
}

/// <summary>
/// Reconciles known source objects with their source of truth (invariant 17): event-driven, for objects held as
/// <c>ReconciliationRequired</c>, and by sweep, for objects already reconciled once.
/// </summary>
public interface ISourceReconciler
{
    /// <summary>Claims due targets of one connection, rereads them and applies the result through processing.</summary>
    Task<Result<ReconciliationRunSummary>> RunAsync(ReconciliationRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a reread of the least recently verified reconciled targets of one connection, at most the run's maximum.
    /// It never creates a target, so it never discovers an object.
    /// </summary>
    Task<Result<int>> RequestSweepAsync(ReconciliationRun run, CancellationToken cancellationToken = default);
}

/// <summary>The fixed, content-free refusals of a reconciliation run.</summary>
public static class ReconciliationErrors
{
    /// <summary>The run names another tenant than the trusted context.</summary>
    public static Error TenantMismatch { get; } =
        Error.Forbidden("reconciliation.run.tenant_mismatch", "The run's tenant is not the trusted tenant.");

    /// <summary>No reader serves the source system.</summary>
    public static Error ReaderMissing { get; } =
        Error.NotFound("reconciliation.run.reader_missing", "No authoritative reader serves the source system.");

    /// <summary>The database could not be reached; nothing was recorded.</summary>
    public static Error Unavailable { get; } =
        Error.Infrastructure("reconciliation.run.unavailable", "Reconciliation could not reach its state; nothing was recorded.");
}
