using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Builds canonical event parts that are valid by default, so each test changes exactly one thing.
/// </summary>
/// <remarks>
/// The source is a deterministic synthetic connector that exists only in tests (Gate 2 owner
/// decision 3); nothing here is monday-specific.
/// </remarks>
internal sealed class EventBuilder
{
    /// <summary>The fixed receipt instant every default time is derived from.</summary>
    internal static readonly DateTimeOffset Received = new(2026, 9, 28, 4, 10, 8, TimeSpan.Zero);

    public CanonicalEventId EventId { get; set; } = CanonicalEventId.New();

    public TenantId TenantId { get; set; } = TenantId.New();

    public string Type { get; set; } = CanonicalEventSchemas.ObjectChangedType;

    public string SchemaVersion { get; set; } = CanonicalEventSchemas.ObjectChangedVersion1;

    public EventSource Source { get; set; } = HomeConnector();

    public ObjectReference WorkObject { get; set; } = Task();

    public string? SourceEventId { get; set; } = "evt-1001";

    public long? SourceRevision { get; set; } = 7;

    public DateTimeOffset? OccurredAt { get; set; } = Received.AddMinutes(-2);

    public DateTimeOffset? SourceUpdatedAt { get; set; } = Received.AddMinutes(-1);

    public DateTimeOffset ReceivedAt { get; set; } = Received;

    public DateTimeOffset? ProcessedAt { get; set; } = Received.AddSeconds(1);

    public CorrelationId CorrelationId { get; set; } = CorrelationId.New();

    public Guid? CausationId { get; set; } = Guid.CreateVersion7();

    public List<FieldChange> Changes { get; set; } = [new("status", FactDataType.TaskStatus, "working", "done")];

    public EventActor? Actor { get; set; } = EventActor.Unresolved("user-7");

    public string? SourceReference { get; set; } = "https://example.invalid/items/42";

    /// <summary>The synthetic connector that owns <see cref="Task"/>.</summary>
    internal static EventSource HomeConnector() =>
        new(EventSourceKind.SaasConnector, "synthetic", "connection-1");

    /// <summary>A task owned by the synthetic connector.</summary>
    internal static ObjectReference Task(string sourceObjectId = "task-42") =>
        new("synthetic", "connection-1", "task", sourceObjectId);

    public CanonicalEventMetadata Metadata() => new(
        EventId,
        TenantId,
        Type,
        SchemaVersion,
        Source,
        WorkObject,
        SourceEventId,
        SourceRevision,
        OccurredAt,
        SourceUpdatedAt,
        ReceivedAt,
        ProcessedAt,
        CorrelationId,
        CausationId);

    public CanonicalEventContent Content() => new(Changes, Actor, SourceReference);

    public Result<CanonicalEvent> Create() => CanonicalEvent.Create(Metadata(), Content());

    /// <summary>Creates the event and fails the test if it does not validate.</summary>
    public CanonicalEvent Valid()
    {
        var result = Create();
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : string.Empty);
        return result.Value;
    }
}
