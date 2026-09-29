using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;
using WorkContinuity.Integration.Tests.Processing;
using WorkContinuity.Integration.Tests.Transport;

namespace WorkContinuity.Integration.Tests.Acceptance;

/// <summary>One event as the synthetic source states it: its provider event id and everything it says.</summary>
internal sealed record SourceEvent(string Id, SyntheticEvent Event)
{
    /// <summary>The fact this event states for one field, with the provenance the Snapshot must keep for it.</summary>
    public Fact FactFor(string field)
    {
        var change = Event.Changes.Single(candidate => string.Equals(candidate.Field, field, StringComparison.Ordinal));
        return new Fact(Event.ObjectId, field, change.DataType, change.After, Id, Event.Revision, Event.UpdatedAt, Event.OccurredAt);
    }
}

/// <summary>
/// A Snapshot fact as far as the order of delivery cannot change it: its value and the provenance of the change that
/// holds it. Versions depend on the order and are checked apart, against the ledger.
/// </summary>
internal sealed record Fact(
    string ObjectId,
    string Field,
    string DataType,
    string? Value,
    string? SourceEventId,
    long? Revision,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? OccurredAt);

/// <summary>What the accounting found for a tenant: its ledger and the dead letters that carry its tenant.</summary>
internal sealed record Accounting(IReadOnlyList<LedgerRow> Ledger, IReadOnlyList<DeadLetteredMessage> DeadLetters);

/// <summary>
/// The ground truth: the Snapshot the source's own ordering evidence implies, whatever the order of delivery. It is
/// computed independently of the ordering policy under test, for the case the seeded runs generate: every event
/// carries a revision, distinct per object, and each field is held by the change with the greatest revision.
/// </summary>
internal static class GroundTruth
{
    public static IReadOnlyList<Fact> Of(IEnumerable<SourceEvent> events)
    {
        var stated = events.ToList();
        foreach (var byObject in stated.GroupBy(item => item.Event.ObjectId, StringComparer.Ordinal))
        {
            var revisions = byObject.Select(item => item.Event.Revision ?? throw new InvalidOperationException("The ground truth needs a revision on every event.")).ToList();
            if (revisions.Distinct().Count() != revisions.Count)
            {
                throw new InvalidOperationException("The ground truth needs distinct revisions per object.");
            }
        }

        return
        [
            .. stated
                .SelectMany(item => item.Event.Changes.Select(change => (Stated: item, change.Field)))
                .GroupBy(pair => (pair.Stated.Event.ObjectId, pair.Field))
                .Select(group => group.MaxBy(pair => pair.Stated.Event.Revision!.Value))
                .Select(pair => pair.Stated.FactFor(pair.Field))
                .OrderBy(fact => fact.ObjectId, StringComparer.Ordinal)
                .ThenBy(fact => fact.Field, StringComparer.Ordinal),
        ];
    }
}

/// <summary>
/// The whole Gate 2 path, composed as the hosts compose it: a source-facing host runs ingress and the outbox relay; a
/// processing host runs the transport consumer with the real processing pipeline, and so the ledger and the Snapshot,
/// as its downstream handler. Both use the local PostgreSQL through the runtime role and share one deterministic
/// in-memory broker (Gate 2 owner decision 8). A fault is injected only where a test asks for it, into the host that
/// owns that step.
/// </summary>
/// <remarks>
/// Older pending outbox work left by other tests is first relayed into a separate broker that nothing consumes, as
/// the emulator tests do, so this broker carries only the test's own messages and every count on it is exact. A
/// restart passes the broker that outlived the hosts and relays nothing aside: its pending work is the test's own.
/// </remarks>
internal sealed class WholePath : IAsyncDisposable
{
    private readonly DatabaseFixture _fixture;

    private WholePath(DatabaseFixture fixture, InMemoryTransport broker, TransportHost source, TransportHost processing)
    {
        _fixture = fixture;
        Broker = broker;
        Source = source;
        Processing = processing;
    }

    /// <summary>Gets the broker both hosts share.</summary>
    public InMemoryTransport Broker { get; }

    /// <summary>Gets the source-facing host: ingress and the outbox relay.</summary>
    public TransportHost Source { get; }

    /// <summary>Gets the processing host: the transport consumer, processing, the ledger and the Snapshot.</summary>
    public TransportHost Processing { get; }

    /// <summary>Gets every log record both hosts wrote.</summary>
    public IReadOnlyList<CapturedLog> Logs => [.. Source.Logs.Entries, .. Processing.Logs.Entries];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Starts the path, optionally with services added to either host or over a broker that outlived a restart.</summary>
    public static async Task<WholePath> StartAsync(
        DatabaseFixture fixture,
        Action<IServiceCollection>? source = null,
        Action<IServiceCollection>? processing = null,
        ISourceEventNormalizer? normalizer = null,
        InMemoryTransport? broker = null)
    {
        if (broker is null)
        {
            await DrainOlderPendingWorkAsync(fixture);
        }

        broker ??= new InMemoryTransport();
        var sourceHost = TransportHost.InMemory(fixture, broker, handlers: services =>
        {
            services.AddSingleton<ITransportMessageHandler>(provider => provider.GetRequiredService<RecordingHandler>());
            source?.Invoke(services);
        });

        return new WholePath(fixture, broker, sourceHost, NewProcessingHost(fixture, broker, normalizer, processing));
    }

    /// <summary>
    /// Relays every older pending outbox message into a broker nothing consumes, so a test's broker, or the emulator,
    /// carries only that test's own messages.
    /// </summary>
    public static async Task DrainOlderPendingWorkAsync(DatabaseFixture fixture)
    {
        await using var drainer = TransportHost.InMemory(fixture, new InMemoryTransport());
        for (var pass = 0; pass < 500; pass++)
        {
            var relayed = await drainer.Relay.RelayPendingAsync(Token);
            if (relayed.Tenants == 0 || (relayed.Published == 0 && !relayed.MoreWork))
            {
                return;
            }
        }
    }

    /// <summary>The message a delivery named as its cause: the receipt it announces.</summary>
    public static Guid CausationOf(TransportDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var read = TransportEnvelope.Read(delivery.Message);
        Assert.True(read.IsSuccess, read.IsSuccess ? string.Empty : read.Error.Code);
        return read.Value.CausationId;
    }

    /// <summary>Another processing host on the same broker and database, as a second or a restarted Worker would be.</summary>
    public TransportHost NewProcessingHost(ISourceEventNormalizer? normalizer = null, Action<IServiceCollection>? processing = null) =>
        NewProcessingHost(_fixture, Broker, normalizer, processing);

    /// <summary>Submits the source's event through ingress, signed, in its own scope as one request would be.</summary>
    public Task<IngressOutcome> SubmitAsync(Guid connection, SourceEvent stated, CorrelationId? correlation = null)
    {
        ArgumentNullException.ThrowIfNull(stated);
        return Source.SubmitAsync(SyntheticSource.Delivery(connection, stated.Id, stated.Event.ToPayload(), correlation));
    }

    /// <summary>Submits any delivery through ingress.</summary>
    public Task<IngressOutcome> SubmitAsync(IngressSubmission submission) => Source.SubmitAsync(submission);

    /// <summary>Submits the source's event and requires ingress to have stored it; returns its receipt.</summary>
    public async Task<Guid> SubmitAcceptedAsync(Guid connection, SourceEvent stated)
    {
        var outcome = await SubmitAsync(connection, stated);
        Assert.True(outcome.Status == IngressStatus.Accepted, $"{stated.Id}: {outcome.Status} {outcome.Error?.Code}");
        return outcome.ReceiptId!.Value;
    }

    /// <summary>Runs relay passes until one publishes nothing and leaves no work behind; every pass must succeed.</summary>
    public async Task RelayAsync()
    {
        for (var pass = 0; pass < 100; pass++)
        {
            var relayed = await Source.Relay.RelayPendingAsync(Token);
            Assert.False(relayed.HasFailures, "A relay pass failed.");
            if (relayed.Published == 0 && !relayed.MoreWork)
            {
                return;
            }
        }

        Assert.Fail("The relay did not finish within 100 passes.");
    }

    /// <summary>
    /// Runs relay passes until the broker's crash switch trips at the chosen send, as the relay process dying there
    /// would: nothing the pass had marked is committed. Returns false if nothing was left to send.
    /// </summary>
    public async Task<bool> RelayUntilCrashAsync(Func<TransportEnvelope, bool> at, SendFault fault)
    {
        Broker.CrashSwitch = new CancellationTokenSource();
        Broker.Fault = envelope => at(envelope) ? fault : SendFault.None;
        try
        {
            for (var pass = 0; pass < 100; pass++)
            {
                try
                {
                    var relayed = await Source.Relay.RelayPendingAsync(Broker.CrashSwitch.Token);
                    if (relayed.Published == 0 && !relayed.MoreWork)
                    {
                        return false;
                    }
                }
                catch (OperationCanceledException) when (Broker.CrashSwitch.IsCancellationRequested)
                {
                    return true;
                }
            }

            Assert.Fail("The relay neither reached the crash nor finished within 100 passes.");
            return false;
        }
        finally
        {
            Broker.Fault = _ => SendFault.None;
        }
    }

    /// <summary>Delivers the next available message to the processing host's consumer, or to the one given.</summary>
    public Task<TransportDelivery?> DeliverAsync(IEventTransportConsumer? consumer = null) =>
        Broker.DeliverNextAsync(consumer ?? Processing.Consumer, Token);

    /// <summary>Delivers until nothing is left on the broker.</summary>
    public async Task<IReadOnlyList<TransportDelivery>> DrainAsync()
    {
        var deliveries = await Broker.DrainAsync(Processing.Consumer);
        Assert.Equal(0, Broker.ActiveCount);
        return deliveries;
    }

    /// <summary>Relays everything pending, then delivers until nothing is left.</summary>
    public async Task<IReadOnlyList<TransportDelivery>> FlushAsync()
    {
        await RelayAsync();
        return await DrainAsync();
    }

    /// <summary>Puts a copy of a delivered message back on the broker, as a republication outside its duplicate window delivers it.</summary>
    public void DeliverAgain(ReceivedTransportMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Broker.SendRaw(message.MessageId, message.SessionId, message.CorrelationId, message.ContentType, message.ApplicationProperties, message.Body.ToArray());
    }

    /// <summary>The dead-lettered messages whose validated envelope names the tenant.</summary>
    public async Task<IReadOnlyList<DeadLetteredMessage>> DeadLettersAsync(TenantId tenant) =>
        [.. (await Broker.PeekAsync(10_000, Token)).Where(message => message.Envelope?.TenantId == tenant)];

    /// <summary>
    /// No event is silently lost: the tenant holds exactly the receipts ingress acknowledged, one outbox marker each and
    /// none left unpublished, nothing is left in flight on the broker, and every receipt ends in exactly one durable,
    /// observable state: a ledger entry with its outcome and a matching receipt state, or a dead letter with a stable,
    /// catalogued reason code while the receipt stays intact and unsettled, ready for re-drive.
    /// </summary>
    public async Task<Accounting> AssertNothingSilentlyLostAsync(TenantId tenant, IEnumerable<Guid> acknowledged)
    {
        ArgumentNullException.ThrowIfNull(acknowledged);
        var expected = acknowledged.Distinct().Order().ToList();

        var receipts = await IngressData.ReceiptsAsync(_fixture, tenant, Token);
        Assert.Equal(expected, receipts.Select(receipt => receipt.ReceiptId).Order());

        var markers = await IngressData.MarkersAsync(_fixture, tenant, Token);
        Assert.Equal(expected, markers.Select(marker => ReceiptNamedBy(marker.Payload)).Order());
        Assert.All(markers, marker => Assert.NotNull(marker.PublishedAtUtc));
        Assert.Equal(0, Broker.ActiveCount);

        var ledger = await ProcessingHarness.LedgerAsync(_fixture, tenant);
        var deadLetters = await DeadLettersAsync(tenant);
        foreach (var receipt in receipts)
        {
            var entries = ledger.Where(entry => entry.ReceiptId == receipt.ReceiptId).ToList();
            var letters = deadLetters.Where(letter => letter.Envelope!.CausationId == receipt.ReceiptId).ToList();
            Assert.True(
                entries.Count + letters.Count == 1,
                $"Receipt {receipt.ReceiptId} has {entries.Count} ledger entries and {letters.Count} dead letters; exactly one is required.");

            if (entries.Count == 1)
            {
                Assert.Equal(entries[0].Outcome == "Quarantined" ? "Quarantined" : "Processed", receipt.ProcessingState);
            }
            else
            {
                Assert.Equal("Received", receipt.ProcessingState);
                Assert.NotEqual(TransportContract.UnrecognizedReason, letters[0].Reason);
                Assert.True(TransportContract.IsSafeReason(letters[0].Reason), letters[0].Reason);
            }
        }

        return new Accounting(ledger, deadLetters);
    }

    /// <summary>
    /// The tenant's Snapshot holds exactly the expected facts, each held by the ledger event, accepted for projection,
    /// of the provider event that stated it.
    /// </summary>
    public Task AssertFactsAsync(TenantId tenant, IEnumerable<Fact> expected) => AssertFactsAsync(_fixture, tenant, expected);

    /// <inheritdoc cref="AssertFactsAsync(TenantId, IEnumerable{Fact})"/>
    public static async Task AssertFactsAsync(DatabaseFixture fixture, TenantId tenant, IEnumerable<Fact> expected)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(expected);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        Assert.Equal(
            expected.OrderBy(fact => fact.ObjectId, StringComparer.Ordinal).ThenBy(fact => fact.Field, StringComparer.Ordinal),
            facts
                .Select(row => new Fact(row.ObjectId, row.Field, row.DataType, row.Value, row.SourceEventId, row.SourceRevision, row.SourceUpdatedAtUtc, row.OccurredAtUtc))
                .OrderBy(fact => fact.ObjectId, StringComparer.Ordinal)
                .ThenBy(fact => fact.Field, StringComparer.Ordinal));

        var ledger = await ProcessingHarness.LedgerAsync(fixture, tenant);
        foreach (var row in facts)
        {
            var entry = Assert.Single(ledger, candidate => candidate.SourceEventId == row.SourceEventId && candidate.ObjectId == row.ObjectId);
            Assert.Equal((entry.EventId, "AcceptedForProjection"), (row.LastEventId, entry.Outcome));
        }
    }

    /// <summary>
    /// Every fact of the tenant carries exactly the provenance it was accepted on: its ledger entry and ledger field,
    /// the field's ordering state, its object, and the receipt whose database-stamped time is its observed time.
    /// </summary>
    public Task AssertProvenanceAsync(TenantId tenant) => AssertProvenanceAsync(_fixture, tenant);

    /// <inheritdoc cref="AssertProvenanceAsync(TenantId)"/>
    public static async Task AssertProvenanceAsync(DatabaseFixture fixture, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(fixture, tenant);
        Assert.NotEmpty(facts);

        await using var connection = await fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM snapshot_fields s
            JOIN event_ledger l ON l.tenant_id = s.tenant_id AND l.event_id = s.last_event_id
            JOIN event_ledger_fields f ON f.tenant_id = s.tenant_id AND f.event_id = s.last_event_id AND f.field = s.field
            JOIN field_ordering_states o ON o.tenant_id = s.tenant_id AND o.object_system = s.object_system
                AND o.object_connection = s.object_connection AND o.object_type = s.object_type
                AND o.object_id = s.object_id AND o.field = s.field
            JOIN snapshot_objects so ON so.tenant_id = s.tenant_id AND so.object_system = s.object_system
                AND so.object_connection = s.object_connection AND so.object_type = s.object_type AND so.object_id = s.object_id
            JOIN raw_receipts r ON r.tenant_id = l.tenant_id AND r.receipt_id = l.receipt_id
            WHERE s.tenant_id = @tenant
              AND l.outcome = 'AcceptedForProjection' AND f.decision = 'Advance' AND f.data_type = s.data_type
              AND o.last_event_id = s.last_event_id
              AND (l.object_system, l.object_connection, l.object_type, l.object_id) = (s.object_system, s.object_connection, s.object_type, s.object_id)
              AND (l.source_kind, l.source_system, l.connection_reference) = (s.source_kind, s.source_system, s.connection_reference)
              AND l.source_event_id IS NOT DISTINCT FROM s.source_event_id AND o.source_event_id IS NOT DISTINCT FROM s.source_event_id
              AND l.source_revision IS NOT DISTINCT FROM s.source_revision AND o.source_revision IS NOT DISTINCT FROM s.source_revision
              AND l.source_updated_at_utc IS NOT DISTINCT FROM s.source_updated_at_utc
              AND o.source_updated_at_utc IS NOT DISTINCT FROM s.source_updated_at_utc
              AND l.occurred_at_utc IS NOT DISTINCT FROM s.occurred_at_utc AND o.occurred_at_utc IS NOT DISTINCT FROM s.occurred_at_utc
              AND r.received_at_utc = s.observed_at_utc AND l.received_at_utc = s.observed_at_utc
              AND l.processed_at_utc = s.processed_at_utc
              AND s.object_version <= so.version;
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        Assert.Equal((long)facts.Count, (long)(await command.ExecuteScalarAsync(Token))!);
    }

    /// <summary>
    /// Each object moved exactly one version per event accepted for it, never otherwise; its current version is the one
    /// its latest facts were written at, by the event it names; and no object exists without a fact.
    /// </summary>
    public async Task AssertVersionsAsync(TenantId tenant)
    {
        var objects = await ProcessingHarness.SnapshotObjectsAsync(_fixture, tenant);
        var facts = await ProcessingHarness.SnapshotFieldsAsync(_fixture, tenant);
        var ledger = await ProcessingHarness.LedgerAsync(_fixture, tenant);

        foreach (var snapshotObject in objects)
        {
            var accepted = ledger.Count(entry => entry.ObjectId == snapshotObject.ObjectId && entry.Outcome == "AcceptedForProjection");
            Assert.Equal(accepted, snapshotObject.Version);

            var ofObject = facts.Where(fact => fact.ObjectId == snapshotObject.ObjectId).ToList();
            Assert.Equal(snapshotObject.Version, ofObject.Max(fact => fact.ObjectVersion));
            Assert.Equal(snapshotObject.LastEventId, ofObject.MaxBy(fact => fact.ObjectVersion)!.LastEventId);
            Assert.All(ofObject, fact => Assert.InRange(fact.FieldVersion, 1, snapshotObject.Version));
        }

        Assert.Equal(
            objects.Select(snapshotObject => snapshotObject.ObjectId).Order(StringComparer.Ordinal),
            facts.Select(fact => fact.ObjectId).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>Counts Snapshot rows, in any tenant, whose text contains the marker.</summary>
    public async Task<long> CountSnapshotRowsContainingAsync(string marker)
    {
        await using var connection = await _fixture.OpenAdministrativeConnectionAsync(Token);
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM snapshot_objects o WHERE strpos(o::text, @marker) > 0)
                 + (SELECT count(*) FROM snapshot_fields f WHERE strpos(f::text, @marker) > 0);
            """,
            connection);
        command.Parameters.AddWithValue("marker", marker);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        await Processing.DisposeAsync();
    }

    private static TransportHost NewProcessingHost(
        DatabaseFixture fixture,
        InMemoryTransport broker,
        ISourceEventNormalizer? normalizer,
        Action<IServiceCollection>? processing) =>
        ProcessingHarness.Host(fixture, broker, services =>
        {
            if (normalizer is null)
            {
                services.AddSingleton<ISourceEventNormalizer, SyntheticNormalizer>();
            }
            else
            {
                services.AddSingleton(normalizer);
            }

            processing?.Invoke(services);
        });

    private static Guid ReceiptNamedBy(string markerPayload)
    {
        using var document = JsonDocument.Parse(markerPayload);
        return document.RootElement.GetProperty("receiptId").GetGuid();
    }
}

/// <summary>
/// Fails the commit of chosen transactions after everything in them was written, as a connection lost at the commit
/// would: the transaction rolls back and the step reports a transient failure. An ingress transaction is chosen by the
/// provider event id of the receipt it wrote, a processing transaction by the receipt its ledger entry names; what
/// the transaction had written is counted inside it, as its own tenant sees it, just before it fails.
/// </summary>
internal sealed class CommitFaults : DbTransactionInterceptor
{
    private const string IngressWrites = "SELECT (SELECT count(*) FROM raw_receipts) + (SELECT count(*) FROM outbox_messages);";
    private const string SnapshotWrites = "SELECT (SELECT count(*) FROM snapshot_objects) + (SELECT count(*) FROM snapshot_fields);";

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _ingress = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, int> _processing = [];
    private readonly HashSet<Guid> _decided = [];
    private int _failures;
    private long _rowsWritten = -1;

    /// <summary>Gets or sets a choice made once per receipt, at its first processing commit: whether that commit fails.</summary>
    public Func<Guid, bool>? FailFirstProcessingCommit { get; set; }

    /// <summary>Gets how many commits failed.</summary>
    public int Failures
    {
        get
        {
            lock (_gate)
            {
                return _failures;
            }
        }
    }

    /// <summary>Gets what the last failed transaction had written: receipts and markers for ingress, Snapshot rows for processing.</summary>
    public long RowsWrittenAtLastFailure
    {
        get
        {
            lock (_gate)
            {
                return _rowsWritten;
            }
        }
    }

    /// <summary>Adds the faults to a host's database context.</summary>
    public void Register(IServiceCollection services) =>
        services.ConfigureDbContext<WorkContinuityDbContext>((_, options) => options.AddInterceptors(this));

    /// <summary>Fails the next commits of the ingress write that stores this provider event.</summary>
    public void FailIngress(string sourceEventId, int times)
    {
        lock (_gate)
        {
            _ingress[sourceEventId] = times;
        }
    }

    /// <summary>Fails the next commits of the processing of this receipt; <see cref="int.MaxValue"/> fails every one.</summary>
    public void FailProcessing(Guid receiptId, int times)
    {
        lock (_gate)
        {
            _processing[receiptId] = times;
        }
    }

    /// <summary>Stops failing the processing of this receipt: the fix.</summary>
    public void Clear(Guid receiptId)
    {
        lock (_gate)
        {
            _processing.Remove(receiptId);
        }
    }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(eventData);

        var evidence = Choose(eventData.Context);
        if (evidence is null)
        {
            return result;
        }

        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = evidence;
        var written = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        lock (_gate)
        {
            _rowsWritten = written;
        }

        throw new InvalidOperationException("Injected: the connection was lost at the commit.");
    }

    private string? Choose(DbContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var tracker = context.ChangeTracker;
        lock (_gate)
        {
            foreach (var ledger in tracker.Entries<EventLedgerRecord>())
            {
                var receipt = ledger.Entity.ReceiptId;
                if (_processing.TryGetValue(receipt, out var left) && left > 0)
                {
                    _processing[receipt] = left == int.MaxValue ? left : left - 1;
                    _failures++;
                    return SnapshotWrites;
                }

                if (FailFirstProcessingCommit is { } decide && _decided.Add(receipt) && decide(receipt))
                {
                    _failures++;
                    return SnapshotWrites;
                }
            }

            if (!tracker.Entries<OutboxMessageRecord>().Any())
            {
                return null;
            }

            foreach (var receipt in tracker.Entries<RawReceiptRecord>())
            {
                if (receipt.Entity.SourceEventId is { } sourceEventId
                    && _ingress.TryGetValue(sourceEventId, out var left)
                    && left > 0)
                {
                    _ingress[sourceEventId] = left - 1;
                    _failures++;
                    return IngressWrites;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// A consumer whose process dies after the message was handled and before the broker learned the settlement: the
/// broker keeps the message and delivers it again. Given a set, each broker message loses at most one settlement, so
/// injected faults stay within the retry limit.
/// </summary>
internal sealed class SettlementLost(IEventTransportConsumer inner, HashSet<long>? once = null) : IEventTransportConsumer
{
    /// <summary>What the dying consumer throws.</summary>
    public const string Death = "The consumer died before its settlement reached the broker.";

    public async Task<TransportSettlement> ConsumeAsync(ReceivedTransportMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settlement = await inner.ConsumeAsync(message, cancellationToken);
        if (once is not null && !once.Add(message.SequenceNumber))
        {
            return settlement;
        }

        throw new InvalidOperationException(Death);
    }
}
