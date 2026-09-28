using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Infrastructure.Persistence.Entities;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Ingress;

/// <summary>
/// Gate 2B durable ingress against the real local PostgreSQL, through the runtime role: the source is
/// verified and the tenant resolved from the connection before any write; the receipt and its outbox
/// marker commit together before the delivery is acknowledged; duplicates are idempotent; refused input
/// leaves nothing behind; a failure rolls back and is retried; and logs never carry content.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class IngressTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_authentic_delivery_is_stored_with_its_outbox_marker_and_then_acknowledged()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var correlation = CorrelationId.New();
        const string payload = """{"item":{"id":"1001","status":"done"},"board":"b-7"}""";

        var outcome = await host.SubmitAsync(
            SyntheticSource.Delivery(connection, "evt-accepted", payload, correlation));

        Assert.Equal(IngressStatus.Accepted, outcome.Status);
        Assert.True(outcome.IsAcknowledged);
        Assert.Equal(correlation, outcome.CorrelationId);
        var receiptId = Assert.NotNull(outcome.ReceiptId);

        // The tenant is set only inside the ingress flow; it never leaks back to the caller.
        Assert.False(host.TenantContextAccessor.Current.IsResolved);

        var receipt = Assert.Single(await ReceiptsAsync(tenant));
        Assert.Equal(receiptId, receipt.ReceiptId);
        Assert.Equal(tenant.Value, receipt.TenantId);
        Assert.Equal(ConnectionRecord.ReferenceFor(connection), receipt.ConnectionReference);
        Assert.Equal(SyntheticSource.System, receipt.SourceSystem);
        Assert.Equal("evt-accepted", receipt.SourceEventId);
        Assert.Equal(SyntheticSource.SchemaVersion, receipt.SchemaVersion);
        Assert.Equal(IngressHost.Now, receipt.ReceivedAtUtc);
        Assert.Equal(nameof(RawReceiptState.Received), receipt.ProcessingState);
        Assert.Equal(Sha256Hex(payload), receipt.PayloadFingerprint);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(receipt.Payload)));

        // The marker names the receipt and nothing else, so it holds no content that must expire.
        var marker = Assert.Single(await MarkersAsync(tenant));
        Assert.Equal(IngressMessageTypes.ReceiptReceived, marker.MessageType);
        var markerBody = JsonNode.Parse(marker.Payload)!.AsObject();
        Assert.Equal(["receiptId"], markerBody.Select(property => property.Key));
        Assert.Equal(receiptId.ToString("D"), (string?)markerBody["receiptId"]);
        Assert.Equal(correlation.Value, marker.CorrelationId);
        Assert.Equal(IngressHost.Now, marker.OccurredAtUtc);
        Assert.Null(marker.PublishedAtUtc);
    }

    [Fact]
    public async Task The_tenant_comes_from_the_connection_even_when_the_payload_names_another()
    {
        var (tenantA, connectionA) = await NewConnectionAsync();
        var (tenantB, _) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var payload = $$$"""{"tenantId":"{{{tenantB}}}","tenant_id":"{{{tenantB}}}","item":{"id":"1"}}""";

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connectionA, "evt-claims-b", payload));

        Assert.Equal(IngressStatus.Accepted, outcome.Status);
        var receiptId = Assert.NotNull(outcome.ReceiptId);
        Assert.Equal(tenantA.Value, Assert.Single(await ReceiptsAsync(tenantA)).TenantId);
        Assert.Single(await MarkersAsync(tenantA));
        Assert.Empty(await ReceiptsAsync(tenantB));
        Assert.Empty(await MarkersAsync(tenantB));

        // Under row-level security the named tenant cannot see the receipt; its real owner can.
        Assert.Equal(0, await VisibleReceiptCountAsync(tenantB, receiptId));
        Assert.Equal(1, await VisibleReceiptCountAsync(tenantA, receiptId));
    }

    [Fact]
    public async Task A_repeated_delivery_is_acknowledged_as_a_duplicate_without_a_second_receipt_or_marker()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var delivery = SyntheticSource.Delivery(connection, "evt-repeat", """{"n":1}""");

        var first = await host.SubmitAsync(delivery);
        var second = await host.SubmitAsync(delivery);
        var retriedUnderNewCorrelation = await host.SubmitAsync(
            SyntheticSource.Delivery(connection, "evt-repeat", """{"n":1}"""));

        Assert.Equal(IngressStatus.Accepted, first.Status);
        Assert.Equal(IngressStatus.Duplicate, second.Status);
        Assert.Equal(IngressStatus.Duplicate, retriedUnderNewCorrelation.Status);
        Assert.True(second.IsAcknowledged);
        Assert.True(retriedUnderNewCorrelation.IsAcknowledged);
        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(first.ReceiptId, retriedUnderNewCorrelation.ReceiptId);

        Assert.Single(await ReceiptsAsync(tenant));
        Assert.Single(await MarkersAsync(tenant));
        Assert.DoesNotContain(host.Logs.Entries, entry =>
            entry.Message.Contains("different payload fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repeated_event_id_with_different_content_keeps_the_first_receipt_and_says_so()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        const string original = """{"status":"working"}""";

        var first = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-changed", original));
        var second = await host.SubmitAsync(
            SyntheticSource.Delivery(connection, "evt-changed", """{"status":"done"}"""));

        Assert.Equal(IngressStatus.Duplicate, second.Status);
        Assert.Equal(first.ReceiptId, second.ReceiptId);

        var receipt = Assert.Single(await ReceiptsAsync(tenant));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(receipt.Payload)));
        Assert.Single(await MarkersAsync(tenant));

        var warning = Assert.Single(host.Logs.Entries, entry =>
            entry.Message.Contains("different payload fingerprint", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(first.ReceiptId!.Value.ToString("D"), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_event_store_exactly_one_receipt_and_one_marker()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var delivery = SyntheticSource.Delivery(connection, "evt-concurrent", """{"n":2}""");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => host.SubmitAsync(delivery), Token)));

        Assert.Single(outcomes, outcome => outcome.Status == IngressStatus.Accepted);
        Assert.All(outcomes, outcome => Assert.True(outcome.IsAcknowledged, $"{outcome.Status} {outcome.Error}"));
        Assert.Single(outcomes.Select(outcome => outcome.ReceiptId).Distinct());
        Assert.Single(await ReceiptsAsync(tenant));
        Assert.Single(await MarkersAsync(tenant));
    }

    [Fact]
    public async Task The_same_event_id_on_another_connection_or_tenant_is_a_different_delivery()
    {
        var (tenantA, connectionA1) = await NewConnectionAsync();
        var connectionA2 = await IngressData.RegisterConnectionAsync(
            fixture, tenantA, ConnectionStatus.Active, SyntheticSource.System, Token);
        var (tenantB, connectionB) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());

        var outcomes = new[]
        {
            await host.SubmitAsync(SyntheticSource.Delivery(connectionA1, "evt-shared", """{"n":1}""")),
            await host.SubmitAsync(SyntheticSource.Delivery(connectionA2, "evt-shared", """{"n":1}""")),
            await host.SubmitAsync(SyntheticSource.Delivery(connectionB, "evt-shared", """{"n":1}""")),
        };

        Assert.All(outcomes, outcome => Assert.Equal(IngressStatus.Accepted, outcome.Status));
        Assert.Equal(3, outcomes.Select(outcome => outcome.ReceiptId).Distinct().Count());
        Assert.Equal(2, (await ReceiptsAsync(tenantA)).Count);
        Assert.Single(await ReceiptsAsync(tenantB));
    }

    [Fact]
    public async Task A_delivery_without_a_source_event_id_is_stored_each_time_with_its_fingerprint()
    {
        // Without a provider event id there is no authoritative duplicate key. Both deliveries are kept;
        // the shared fingerprint lets later processing flag them as duplicate candidates.
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        const string payload = """{"n":5}""";

        var first = await host.SubmitAsync(SyntheticSource.Delivery(connection, sourceEventId: null, payload));
        var second = await host.SubmitAsync(SyntheticSource.Delivery(connection, sourceEventId: null, payload));

        Assert.Equal(IngressStatus.Accepted, first.Status);
        Assert.Equal(IngressStatus.Accepted, second.Status);
        Assert.NotEqual(first.ReceiptId, second.ReceiptId);

        var receipts = await ReceiptsAsync(tenant);
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, receipt => Assert.Null(receipt.SourceEventId));
        Assert.All(receipts, receipt => Assert.Equal(Sha256Hex(payload), receipt.PayloadFingerprint));
        Assert.Equal(2, (await MarkersAsync(tenant)).Count);
    }

    [Fact]
    public async Task An_unknown_connection_is_rejected_before_anything_is_stored()
    {
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var marker = IngressData.NewMarker();
        var correlation = CorrelationId.New();

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(
            Guid.CreateVersion7(), "evt-unknown", IngressData.PayloadWith(marker), correlation));

        AssertRejected(outcome, IngressErrors.UnknownConnection, QuarantineReason.UnknownTenant);
        Assert.Equal(correlation, outcome.CorrelationId);
        Assert.Equal(0, await CountStoredContentAsync(marker));
    }

    [Fact]
    public async Task A_disabled_connection_is_refused_like_an_unknown_one()
    {
        var (tenant, connection) = await NewConnectionAsync(connectionStatus: ConnectionStatus.Disabled);
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var marker = IngressData.NewMarker();

        var outcome = await host.SubmitAsync(
            SyntheticSource.Delivery(connection, "evt-disabled", IngressData.PayloadWith(marker)));

        AssertRejected(outcome, IngressErrors.UnknownConnection, QuarantineReason.UnknownTenant);
        Assert.Empty(await ReceiptsAsync(tenant));
        Assert.Equal(0, await CountStoredContentAsync(marker));
    }

    [Fact]
    public async Task A_connection_whose_tenant_is_suspended_or_missing_is_rejected_without_storing()
    {
        var (suspended, suspendedConnection) = await NewConnectionAsync(tenantStatus: "Suspended");
        var orphan = TenantId.New();
        var orphanConnection = await IngressData.RegisterConnectionAsync(
            fixture, orphan, ConnectionStatus.Active, SyntheticSource.System, Token);
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var marker = IngressData.NewMarker();

        var toSuspended = await host.SubmitAsync(
            SyntheticSource.Delivery(suspendedConnection, "evt-suspended", IngressData.PayloadWith(marker)));
        var toOrphan = await host.SubmitAsync(
            SyntheticSource.Delivery(orphanConnection, "evt-orphan", IngressData.PayloadWith(marker)));

        AssertRejected(toSuspended, IngressErrors.TenantInactive, QuarantineReason.UnknownTenant);
        AssertRejected(toOrphan, IngressErrors.TenantInactive, QuarantineReason.UnknownTenant);
        Assert.Empty(await ReceiptsAsync(suspended));
        Assert.Empty(await ReceiptsAsync(orphan));
        Assert.Equal(0, await CountStoredContentAsync(marker));
    }

    [Fact]
    public async Task A_delivery_claiming_another_source_than_its_connection_is_rejected()
    {
        var (tenant, connection) = await NewConnectionAsync();

        // Even a verifier that accepts everything for the claimed source cannot rescue the mismatch.
        await using var host = new IngressHost(
            fixture, new SyntheticSourceVerifier(), new AcceptingVerifier("other-source"));
        var marker = IngressData.NewMarker();

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(
            connection, "evt-mismatch", IngressData.PayloadWith(marker), sourceSystem: "other-source"));

        AssertRejected(outcome, IngressErrors.SourceMismatch, QuarantineReason.InvalidAuthentication);
        Assert.Empty(await ReceiptsAsync(tenant));
        Assert.Equal(0, await CountStoredContentAsync(marker));
    }

    [Fact]
    public async Task A_delivery_that_fails_verification_is_rejected_before_anything_is_stored()
    {
        var (tenant, connection) = await NewConnectionAsync();
        var (otherTenant, otherConnection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var marker = IngressData.NewMarker();
        var payload = IngressData.PayloadWith(marker);
        var signedForOtherContent = SyntheticSource.Sign(
            connection, payload.Replace("working", "done", StringComparison.Ordinal));

        var outcomes = new[]
        {
            await host.SubmitAsync(SyntheticSource.Delivery(
                connection, "evt-proof-1", payload, proof: SyntheticSource.Sign(connection, "other"))),
            await host.SubmitAsync(SyntheticSource.Delivery(
                connection, "evt-proof-2", payload, proof: signedForOtherContent)),
            await host.SubmitAsync(new IngressSubmission(
                connection, SyntheticSource.System, "evt-proof-3", SyntheticSource.SchemaVersion, payload,
                CorrelationId.New(), proof: null)),

            // A delivery signed for another tenant's connection, replayed onto this one.
            await host.SubmitAsync(SyntheticSource.Delivery(
                connection, "evt-proof-4", payload, proof: SyntheticSource.Sign(otherConnection, payload))),
        };

        Assert.All(outcomes, outcome =>
            AssertRejected(outcome, IngressErrors.SourceNotVerified, QuarantineReason.InvalidAuthentication));
        Assert.Empty(await ReceiptsAsync(tenant));
        Assert.Empty(await ReceiptsAsync(otherTenant));
        Assert.Equal(0, await CountStoredContentAsync(marker));
    }

    [Fact]
    public async Task A_source_without_exactly_one_verifier_is_never_accepted()
    {
        var (tenant, connection) = await NewConnectionAsync();
        var payload = IngressData.PayloadWith(IngressData.NewMarker());

        await using (var unverified = new IngressHost(fixture))
        {
            AssertRejected(
                await unverified.SubmitAsync(SyntheticSource.Delivery(connection, "evt-no-verifier", payload)),
                IngressErrors.SourceNotVerified,
                QuarantineReason.InvalidAuthentication);
        }

        await using (var ambiguous = new IngressHost(
            fixture, new SyntheticSourceVerifier(), new SyntheticSourceVerifier()))
        {
            AssertRejected(
                await ambiguous.SubmitAsync(SyntheticSource.Delivery(connection, "evt-two-verifiers", payload)),
                IngressErrors.SourceNotVerified,
                QuarantineReason.InvalidAuthentication);
        }

        Assert.Empty(await ReceiptsAsync(tenant));
    }

    [Fact]
    public async Task A_verifier_that_throws_counts_as_not_verified_and_its_message_is_not_logged()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new ThrowingVerifier());
        var proof = "proof-" + IngressData.NewMarker();

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(
            connection, "evt-verifier-throws", IngressData.PayloadWith(IngressData.NewMarker()), proof: proof));

        AssertRejected(outcome, IngressErrors.SourceNotVerified, QuarantineReason.InvalidAuthentication);
        Assert.Contains(host.Logs.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Text.Contains(proof, StringComparison.Ordinal));
        Assert.Empty(await ReceiptsAsync(tenant));
    }

    [Fact]
    public async Task Malformed_input_is_rejected_before_anything_is_stored()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var marker = IngressData.NewMarker();
        var payload = IngressData.PayloadWith(marker);

        IngressSubmission Bad(string content) => SyntheticSource.Delivery(connection, "evt-bad", content);

        var cases = new (string Case, IngressSubmission Delivery, Error Expected)[]
        {
            ("empty connection id", SyntheticSource.Delivery(Guid.Empty, "evt-bad", payload), IngressErrors.InvalidConnectionId),
            ("uppercase source system", SyntheticSource.Delivery(connection, "evt-bad", payload, sourceSystem: "Synthetic"), IngressErrors.InvalidSourceSystem),
            ("padded source event id", SyntheticSource.Delivery(connection, " evt-bad", payload), IngressErrors.InvalidSourceEventId),
            ("schema version with a space", SyntheticSource.Delivery(connection, "evt-bad", payload, schemaVersion: "v 1"), IngressErrors.InvalidSchemaVersion),
            ("blank payload", Bad("   "), IngressErrors.EmptyPayload),
            ("not JSON", Bad("note " + marker), IngressErrors.PayloadNotStorable),
            ("trailing comma", Bad($$"""{"note":"{{marker}}",}"""), IngressErrors.PayloadNotStorable),
            ("comment", Bad($$"""{"note":"{{marker}}"} /* comment */"""), IngressErrors.PayloadNotStorable),
            ("repeated key", Bad($$"""{"note":"{{marker}}","note":"again"}"""), IngressErrors.PayloadNotStorable),
            ("escaped NUL in a value", Bad($$"""{"note":"{{marker}}","z":"\u0000"}"""), IngressErrors.PayloadNotStorable),
            ("escaped NUL in a key", Bad($$"""{"note":"{{marker}}","\u0000":"z"}"""), IngressErrors.PayloadNotStorable),
            ("escaped lone surrogate", Bad($$"""{"note":"{{marker}}","z":"\ud800"}"""), IngressErrors.PayloadNotStorable),
            ("raw lone surrogate", Bad($$"""{"note":"{{marker}}","z":"{{(char)0xD800}}"}"""), IngressErrors.PayloadNotStorable),
            ("nesting deeper than 64", Bad(new string('[', 128) + '"' + marker + '"' + new string(']', 128)), IngressErrors.PayloadNotStorable),
        };

        foreach (var (name, delivery, expected) in cases)
        {
            var outcome = await host.SubmitAsync(delivery);

            Assert.True(
                outcome.Status == IngressStatus.Rejected && Equals(expected, outcome.Error),
                $"{name}: expected rejection {expected}, got {outcome.Status} {outcome.Error}");
            Assert.Equal(QuarantineReason.BadSchema, outcome.RejectionReason);
        }

        Assert.Empty(await ReceiptsAsync(tenant));
        Assert.Empty(await MarkersAsync(tenant));
        Assert.Equal(0, await CountStoredContentAsync(marker));
    }

    [Fact]
    public async Task Unusual_but_well_formed_json_is_stored()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        string[] payloads =
        [
            """{"face":"😀","text":"line\nbreak \"quoted\""}""",
            $$"""{"face":"{{char.ConvertFromUtf32(0x1F600)}}"}""",
            """[1,2,{"three":3}]""",
            "42",
        ];

        for (var index = 0; index < payloads.Length; index++)
        {
            var outcome = await host.SubmitAsync(
                SyntheticSource.Delivery(connection, $"evt-unusual-{index}", payloads[index]));

            Assert.True(
                outcome.Status == IngressStatus.Accepted,
                $"payload {index}: {outcome.Status} {outcome.Error}");
        }

        Assert.Equal(payloads.Length, (await ReceiptsAsync(tenant)).Count);
    }

    [Fact]
    public async Task A_payload_only_the_database_refuses_is_rejected_after_a_full_rollback()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var marker = IngressData.NewMarker();

        // Well-formed JSON whose number PostgreSQL's numeric type cannot hold: the checks before the
        // write accept it, so the database's own refusal is what is under test here.
        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(
            connection, "evt-overflow", $$"""{"note":"{{marker}}","n":1e1000000}"""));

        AssertRejected(outcome, IngressErrors.PayloadNotStorable, QuarantineReason.BadSchema);
        Assert.Contains(host.Logs.Entries, entry =>
            entry.Message.Contains("persistence.invalid_data", StringComparison.Ordinal));
        Assert.Empty(await ReceiptsAsync(tenant));
        Assert.Empty(await MarkersAsync(tenant));
        Assert.Equal(0, await CountStoredContentAsync(marker));
    }

    [Fact]
    public async Task A_failure_inside_the_write_is_not_acknowledged_leaves_nothing_and_the_retry_is_stored()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var failing = CorrelationId.New();
        var delivery = SyntheticSource.Delivery(connection, "evt-retry", """{"n":3}""", failing);
        var name = "ingress_test_fail_" + Guid.NewGuid().ToString("N")[..12];

        // A trigger that fails the marker insert for this delivery only: the receipt insert in the same
        // transaction must not survive it.
        await IngressData.ExecuteAdministrativeAsync(
            fixture,
            $"""
            CREATE FUNCTION public.{name}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.correlation_id = '{failing.Value}' THEN
                    RAISE EXCEPTION 'simulated failure inside the ingress transaction';
                END IF;
                RETURN NEW;
            END
            $body$;
            CREATE TRIGGER {name} BEFORE INSERT ON public.outbox_messages
                FOR EACH ROW EXECUTE FUNCTION public.{name}();
            """,
            Token);

        IngressOutcome failed;
        try
        {
            failed = await host.SubmitAsync(delivery);
        }
        finally
        {
            await IngressData.ExecuteAdministrativeAsync(
                fixture,
                $"DROP TRIGGER IF EXISTS {name} ON public.outbox_messages; DROP FUNCTION IF EXISTS public.{name}();",
                Token);
        }

        Assert.Equal(IngressStatus.Failed, failed.Status);
        Assert.False(failed.IsAcknowledged);
        Assert.Null(failed.ReceiptId);
        Assert.Equal(IngressErrors.Unavailable, failed.Error);
        Assert.Equal(failing, failed.CorrelationId);
        Assert.Empty(await ReceiptsAsync(tenant));
        Assert.Empty(await MarkersAsync(tenant));

        // The source retries the unacknowledged delivery, and now it is stored exactly once.
        var retried = await host.SubmitAsync(delivery);

        Assert.Equal(IngressStatus.Accepted, retried.Status);
        Assert.Single(await ReceiptsAsync(tenant));
        Assert.Single(await MarkersAsync(tenant));
    }

    [Fact]
    public async Task A_delivery_without_a_correlation_id_gets_one_that_reaches_its_marker()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(
            connection, "evt-no-correlation", """{"n":4}""", correlationId: default(CorrelationId)));

        Assert.Equal(IngressStatus.Accepted, outcome.Status);
        Assert.False(outcome.CorrelationId.IsEmpty);
        Assert.Equal(outcome.CorrelationId.Value, Assert.Single(await MarkersAsync(tenant)).CorrelationId);
    }

    [Fact]
    public async Task Ingress_logs_carry_ids_and_codes_but_never_the_payload_or_the_proof()
    {
        var (_, connection) = await NewConnectionAsync();
        var (_, suspendedConnection) = await NewConnectionAsync(tenantStatus: "Suspended");
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var content = IngressData.NewMarker();
        var proof = "proof-" + IngressData.NewMarker();
        var payload = IngressData.PayloadWith(content);

        var accepted = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-log-1", payload));
        await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-log-1", payload));
        await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-log-1", IngressData.PayloadWith(content + "x")));
        await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-log-2", payload, proof: proof));
        await host.SubmitAsync(SyntheticSource.Delivery(Guid.CreateVersion7(), "evt-log-3", payload));
        await host.SubmitAsync(SyntheticSource.Delivery(suspendedConnection, "evt-log-4", payload));
        await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-log-5", "note " + content));
        await host.SubmitAsync(SyntheticSource.Delivery(
            connection, "evt-log-6", $$"""{"note":"{{content}}","n":1e1000000}"""));

        var entries = host.Logs.Entries;
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain(content, entry.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(proof, entry.Text, StringComparison.Ordinal);
        });

        // What operators need is there instead: identifiers and stable codes.
        Assert.Contains(entries, entry => entry.Message.Contains(accepted.ReceiptId!.Value.ToString("D"), StringComparison.Ordinal));
        foreach (var code in new[]
                 {
                     IngressErrors.SourceNotVerified.Code,
                     IngressErrors.UnknownConnection.Code,
                     IngressErrors.TenantInactive.Code,
                     IngressErrors.PayloadNotStorable.Code,
                 })
        {
            Assert.Contains(entries, entry => entry.Message.Contains(code, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Ingress_refuses_to_run_in_a_scope_that_already_has_a_tenant()
    {
        var (tenant, connection) = await NewConnectionAsync();
        await using var host = new IngressHost(fixture, new SyntheticSourceVerifier());
        var marker = IngressData.NewMarker();

        // A caller's tenant must never stand in for the connection's, so ingress refuses to start at all.
        await Task.Run(
            async () =>
            {
                host.TenantContextAccessor.SetResolved(
                    TenantContext.Resolved(TenantId.New(), TenantResolutionSource.AuthenticatedPrincipal));

                await Assert.ThrowsAsync<InvalidOperationException>(() => host.SubmitAsync(
                    SyntheticSource.Delivery(connection, "evt-scoped", IngressData.PayloadWith(marker))));
            },
            Token);

        Assert.Empty(await ReceiptsAsync(tenant));
        Assert.Equal(0, await CountStoredContentAsync(marker));
    }

    private static void AssertRejected(IngressOutcome outcome, Error expected, QuarantineReason reason)
    {
        Assert.Equal(IngressStatus.Rejected, outcome.Status);
        Assert.False(outcome.IsAcknowledged);
        Assert.Null(outcome.ReceiptId);
        Assert.Equal(expected, outcome.Error);
        Assert.Equal(reason, outcome.RejectionReason);
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private Task<(TenantId Tenant, Guid Connection)> NewConnectionAsync(
        string tenantStatus = "Active",
        ConnectionStatus connectionStatus = ConnectionStatus.Active) =>
        IngressData.NewConnectionAsync(fixture, Token, tenantStatus, connectionStatus);

    private Task<IReadOnlyList<StoredReceipt>> ReceiptsAsync(TenantId tenant) =>
        IngressData.ReceiptsAsync(fixture, tenant, Token);

    private Task<IReadOnlyList<StoredMarker>> MarkersAsync(TenantId tenant) =>
        IngressData.MarkersAsync(fixture, tenant, Token);

    private Task<long> CountStoredContentAsync(string marker) =>
        IngressData.CountStoredContentAsync(fixture, marker, Token);

    private async Task<int> VisibleReceiptCountAsync(TenantId tenant, Guid receiptId)
    {
        var (database, context) = fixture.CreateTenantScoped(tenant);
        await using (context)
        {
            var read = await database.ExecuteAsync(
                (dbContext, token) => dbContext.RawReceipts
                    .AsNoTracking()
                    .CountAsync(receipt => receipt.ReceiptId == receiptId, token),
                Token);

            Assert.True(read.IsSuccess, read.IsSuccess ? string.Empty : read.Error.Code);
            return read.Value;
        }
    }
}
