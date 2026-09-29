using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Retention;
using WorkContinuity.Core.Transport;

namespace WorkContinuity.Infrastructure.Observability;

/// <summary>
/// The Event Backbone's operational metrics (architecture §10), on one meter. They cover ingress, the outbox
/// relay, transport settlement and re-drive, processing, Snapshot projection and raw-content retention.
/// </summary>
/// <remarks>
/// <para>
/// Every tag comes from a bounded vocabulary. An outcome, a canonical reason, a receipt state or a retention level
/// comes from a closed enumeration. A reason code comes from the fixed error catalogs, and anything else becomes
/// <c>other</c>. The rest are a settlement kind or a yes or no. No tenant, connection, object, event, receipt,
/// message or actor id and no value is ever a tag, so cardinality stays fixed and nothing personal reaches a
/// metric. Per-tenant diagnosis uses the existing structured logs, which carry the ids.
/// </para>
/// <para>
/// A measurement is taken only for what happened: a projection or a purge counts once its transaction committed.
/// The hosts' OpenTelemetry pipelines subscribe to <see cref="MeterName"/>. Each instance owns its own meter, so a
/// test can listen to exactly the instruments of the composition it builds.
/// </para>
/// </remarks>
public sealed class EventBackboneMetrics : IDisposable
{
    /// <summary>The meter name the hosts' OpenTelemetry pipelines subscribe to.</summary>
    public const string MeterName = "WorkContinuity.EventBackbone";

    /// <summary>The tag value for "not applicable".</summary>
    public const string None = "none";

    /// <summary>The tag value for a code outside the fixed catalogs.</summary>
    public const string Other = "other";

    private static readonly HashSet<string> KnownCodes = CollectCodes(
        typeof(IngressErrors),
        typeof(TransportErrors),
        typeof(ProcessingErrors),
        typeof(CanonicalEventErrors));

    private readonly Counter<long> _ingressDeliveries;
    private readonly Counter<long> _outboxMessages;
    private readonly Histogram<double> _relayLag;
    private readonly Counter<long> _relayPasses;
    private readonly Histogram<long> _passTenants;
    private readonly Counter<long> _settlements;
    private readonly Histogram<long> _deliveryCount;
    private readonly Counter<long> _redrives;
    private readonly Counter<long> _processedEvents;
    private readonly Histogram<double> _processingDuration;
    private readonly Histogram<double> _processingLag;
    private readonly Counter<long> _projectedEvents;
    private readonly Counter<long> _projectedFields;
    private readonly Counter<long> _projectionFailures;
    private readonly Histogram<double> _projectionDuration;
    private readonly Histogram<double> _snapshotLag;
    private readonly Counter<long> _purgeRuns;
    private readonly Counter<long> _purgedReceipts;
    private readonly Histogram<double> _purgeDuration;
    private readonly Histogram<double> _oldestPurgedAge;
    private readonly Histogram<double> _oldestRemainingAge;
    private readonly Counter<long> _overdueContent;

    /// <summary>Initializes a new instance of the <see cref="EventBackboneMetrics"/> class, with its own meter.</summary>
    public EventBackboneMetrics()
    {
        Meter = new Meter(MeterName, "1.0.0");

        _ingressDeliveries = Meter.CreateCounter<long>(
            "workcontinuity.ingress.deliveries", "{delivery}", "Deliveries ingress accepted, recognized as repeated, rejected before storing, or failed to store.");

        _outboxMessages = Meter.CreateCounter<long>(
            "workcontinuity.outbox.messages", "{message}", "Outbox messages the relay published, failed to publish, or could not relay.");
        _relayLag = Meter.CreateHistogram<double>(
            "workcontinuity.outbox.relay_lag", "s", "Time from an outbox message's receipt to its publication.");
        _relayPasses = Meter.CreateCounter<long>(
            "workcontinuity.outbox.relay_passes", "{pass}", "Relay passes, by outcome and by whether more work was left than one pass takes.");
        _passTenants = Meter.CreateHistogram<long>(
            "workcontinuity.outbox.pass_tenants", "{tenant}", "Tenants with pending outbox work found by one relay pass.");

        _settlements = Meter.CreateCounter<long>(
            "workcontinuity.transport.settlements", "{message}", "Broker messages completed, abandoned for retry, or dead-lettered.");
        _deliveryCount = Meter.CreateHistogram<long>(
            "workcontinuity.transport.delivery_count", "{delivery}", "The delivery attempt of each consumed broker message.");
        _redrives = Meter.CreateCounter<long>(
            "workcontinuity.transport.redrives", "{message}", "Explicit re-drives of dead-lettered messages, by outcome.");

        _processedEvents = Meter.CreateCounter<long>(
            "workcontinuity.processing.events", "{event}", "Receipts processed, by outcome, canonical reason and reason code.");
        _processingDuration = Meter.CreateHistogram<double>(
            "workcontinuity.processing.duration", "ms", "Time spent processing one receipt, by outcome.");
        _processingLag = Meter.CreateHistogram<double>(
            "workcontinuity.processing.lag", "s", "Time from a receipt to its processing decision, by outcome.");

        _projectedEvents = Meter.CreateCounter<long>(
            "workcontinuity.snapshot.projected_events", "{event}", "Accepted events projected onto the Snapshot.");
        _projectedFields = Meter.CreateCounter<long>(
            "workcontinuity.snapshot.projected_fields", "{field}", "Fields projected onto the Snapshot.");
        _projectionFailures = Meter.CreateCounter<long>(
            "workcontinuity.snapshot.projection_failures", "{event}", "Projections that failed and rolled their event back.");
        _projectionDuration = Meter.CreateHistogram<double>(
            "workcontinuity.snapshot.projection_duration", "ms", "Time spent projecting one accepted event.");
        _snapshotLag = Meter.CreateHistogram<double>(
            "workcontinuity.snapshot.lag", "s", "Time from a receipt to its fact in the Snapshot.");

        _purgeRuns = Meter.CreateCounter<long>(
            "workcontinuity.retention.runs", "{run}", "Raw-content purge runs, by outcome.");
        _purgedReceipts = Meter.CreateCounter<long>(
            "workcontinuity.retention.purged", "{receipt}", "Receipts whose raw content was purged, by processing state.");
        _purgeDuration = Meter.CreateHistogram<double>(
            "workcontinuity.retention.duration", "ms", "Time one purge run took.");
        _oldestPurgedAge = Meter.CreateHistogram<double>(
            "workcontinuity.retention.oldest_purged_age", "s", "Age of the oldest raw content one purge run removed.");
        _oldestRemainingAge = Meter.CreateHistogram<double>(
            "workcontinuity.retention.oldest_remaining_age", "s", "Age of the oldest due raw content left after a purge run.");
        _overdueContent = Meter.CreateCounter<long>(
            "workcontinuity.retention.overdue", "{run}", "Purge runs that found raw content past the overdue age or the 30-day limit, by level and by whether the run purged it or it remains.");
    }

    /// <summary>Gets every tag key any instrument carries.</summary>
    public static IReadOnlyList<string> TagKeys { get; } = ["outcome", "reason", "reason_code", "settlement", "state", "backlog", "level"];

    /// <summary>Gets the meter all instruments belong to.</summary>
    public Meter Meter { get; }

    /// <inheritdoc />
    public void Dispose() => Meter.Dispose();

    /// <summary>The tag value of an enumeration member: its snake_case name, or <see cref="Other"/> if undefined.</summary>
    public static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? Snake(value.ToString()) : Other;

    /// <summary>The tag value of a reason code: the code if the catalogs define it, otherwise <see cref="Other"/>.</summary>
    public static string Code(string? code) =>
        code is null ? None : KnownCodes.Contains(code) ? code : Other;

    internal void IngressDelivery(IngressOutcome outcome) =>
        _ingressDeliveries.Add(
            1,
            new("outcome", Name(outcome.Status)),
            new("reason", outcome.Status == IngressStatus.Rejected ? Name(outcome.RejectionReason) : None));

    internal void OutboxPublished(TimeSpan relayLag)
    {
        _outboxMessages.Add(1, new KeyValuePair<string, object?>("outcome", "published"));
        _relayLag.Record(Math.Max(0, relayLag.TotalSeconds));
    }

    internal void OutboxPublishFailed() =>
        _outboxMessages.Add(1, new KeyValuePair<string, object?>("outcome", "publish_failed"));

    internal void OutboxUnrelayable() =>
        _outboxMessages.Add(1, new KeyValuePair<string, object?>("outcome", "unrelayable"));

    internal void RelayPass(OutboxRelayPass pass)
    {
        var outcome = pass.DiscoveryFailed ? "discovery_failed" : pass.HasFailures ? "failed" : "completed";
        _relayPasses.Add(1, new("outcome", outcome), new("backlog", pass.MoreWork ? "true" : "false"));
        if (!pass.DiscoveryFailed)
        {
            _passTenants.Record(pass.Tenants);
        }
    }

    internal void Settled(TransportSettlement settlement, TransportHandlingResult? result, int deliveryCount)
    {
        var reason = settlement.Kind switch
        {
            TransportSettlementKind.DeadLetter => Code(settlement.DeadLetterReason?.Code),
            TransportSettlementKind.Abandon => Code(result?.Reason?.Code),
            _ => None,
        };
        _settlements.Add(1, new("settlement", Name(settlement.Kind)), new("reason_code", reason));
        _deliveryCount.Record(Math.Max(0, deliveryCount));
    }

    internal void Redriven(RedriveStatus status) =>
        _redrives.Add(1, new KeyValuePair<string, object?>("outcome", Name(status)));

    internal void Processed(EventProcessingResult result, TimeSpan duration, TimeSpan? lag)
    {
        var outcome = Name(result.Outcome);
        _processedEvents.Add(
            1,
            new("outcome", outcome),
            new("reason", result.Outcome == EventProcessingOutcome.Quarantined ? Name(result.QuarantineReason) : None),
            new("reason_code", Code(result.ReasonCode)));
        _processingDuration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));
        if (lag is { } decided)
        {
            _processingLag.Record(Math.Max(0, decided.TotalSeconds), new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    internal void ProcessingFailed(Error error, bool transient, TimeSpan duration)
    {
        var outcome = transient ? "unavailable" : "refused";
        _processedEvents.Add(1, new("outcome", outcome), new("reason", None), new("reason_code", Code(error.Code)));
        _processingDuration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));
    }

    internal void Projected(int fields, TimeSpan duration, TimeSpan lag)
    {
        _projectedEvents.Add(1);
        _projectedFields.Add(fields);
        _projectionDuration.Record(duration.TotalMilliseconds);
        _snapshotLag.Record(Math.Max(0, lag.TotalSeconds));
    }

    internal void ProjectionFailed() => _projectionFailures.Add(1);

    internal void PurgeRun(RawContentPurgeRun run)
    {
        _purgeRuns.Add(1, new KeyValuePair<string, object?>("outcome", Name(run.Outcome)));
        AddPurged("processed", run.ProcessedPurged);
        AddPurged("quarantined", run.QuarantinedPurged);
        AddPurged("unprocessed", run.UnprocessedPurged);
        _purgeDuration.Record(run.Duration.TotalMilliseconds);
        if (run.OldestPurgedAge is { } oldest)
        {
            _oldestPurgedAge.Record(Math.Max(0, oldest.TotalSeconds));
        }

        if (run.OldestRemainingAge is { } left)
        {
            _oldestRemainingAge.Record(Math.Max(0, left.TotalSeconds));
        }

        // Lateness is counted whenever it is seen: content purged late, and content still left behind.
        AddOverdue(run.PurgedStatus, "purged");
        AddOverdue(run.RemainingStatus, "remaining");
    }

    private void AddOverdue(RawContentRetentionStatus status, string state)
    {
        if (status is RawContentRetentionStatus.Overdue or RawContentRetentionStatus.LimitExceeded)
        {
            _overdueContent.Add(1, new("level", Name(status)), new("state", state));
        }
    }

    private void AddPurged(string state, int count)
    {
        if (count > 0)
        {
            _purgedReceipts.Add(count, new KeyValuePair<string, object?>("state", state));
        }
    }

    private static string Snake(string pascal)
    {
        var builder = new StringBuilder(pascal.Length + 8);
        for (var index = 0; index < pascal.Length; index++)
        {
            var character = pascal[index];
            if (char.IsUpper(character))
            {
                if (index > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static HashSet<string> CollectCodes(params Type[] catalogs) =>
        catalogs
            .SelectMany(catalog => catalog.GetProperties(BindingFlags.Public | BindingFlags.Static))
            .Where(property => property.PropertyType == typeof(Error))
            .Select(property => ((Error)property.GetValue(null)!).Code)
            .ToHashSet(StringComparer.Ordinal);
}
