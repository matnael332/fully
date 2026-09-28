using System.Text;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Transport;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The Gate 2C broker contract: an outbox marker becomes an envelope with deterministic identity, the wire
/// form carries every value typed, a received message is trusted only after every part is validated, only
/// the content-free marker can travel, and a re-drive keeps identity while advancing only its count.
/// </summary>
public sealed class TransportContractTests
{
    private static readonly Guid OutboxId = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid ReceiptId = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-111122223333");
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-444455556666"));
    private static readonly CorrelationId Correlation = CorrelationId.New();

    private static string Marker(Guid receiptId) => $$"""{"receiptId":"{{receiptId:D}}"}""";

    [Fact]
    public void An_outbox_marker_becomes_an_envelope_with_deterministic_identity()
    {
        var envelope = Envelope();

        Assert.Equal(IngressMessageTypes.ReceiptReceived, envelope.MessageType);
        Assert.Equal(Tenant, envelope.TenantId);
        Assert.Equal(OutboxId, envelope.OutboxMessageId);
        Assert.Equal(Correlation, envelope.CorrelationId);
        Assert.Equal(ReceiptId, envelope.CausationId);
        Assert.Equal(0, envelope.RedriveCount);

        // The message id is the outbox id, so republishing a row yields the same broker message id; the
        // session is the tenant; the queue is the canonical event-processing queue.
        Assert.Equal(OutboxId.ToString("D"), envelope.MessageId);
        Assert.Equal(Tenant.Value.ToString("D"), envelope.SessionId);
        Assert.Equal("event-processing", envelope.QueueName);
        Assert.Equal(Marker(ReceiptId), envelope.Body);

        // Building it twice gives the same identity: nothing random or time-based enters it.
        Assert.Equal(envelope.MessageId, Envelope().MessageId);
        Assert.Equal(envelope.SessionId, Envelope().SessionId);
    }

    [Fact]
    public void The_stored_marker_is_accepted_in_either_text_form_and_sent_in_canonical_form()
    {
        // PostgreSQL returns jsonb with a space after the colon; ingress writes it without one.
        var stored = TransportEnvelope.FromOutbox(
            OutboxId, Tenant, IngressMessageTypes.ReceiptReceived, $$"""{"receiptId": "{{ReceiptId:D}}"}""", Correlation);

        Assert.True(stored.IsSuccess);
        Assert.Equal(Marker(ReceiptId), stored.Value.Body);
        Assert.Equal(Marker(ReceiptId), Encoding.UTF8.GetString(stored.Value.ToWire().Body.Span));
    }

    [Fact]
    public void The_wire_form_carries_every_envelope_value_typed_and_nothing_else()
    {
        var wire = Envelope().ToWire();

        Assert.Equal("event-processing", wire.QueueName);
        Assert.Equal(OutboxId.ToString("D"), wire.MessageId);
        Assert.Equal(Tenant.Value.ToString("D"), wire.SessionId);
        Assert.Equal(Correlation.Value, wire.CorrelationId);
        Assert.Equal("application/json", wire.ContentType);
        Assert.Equal(Marker(ReceiptId), Encoding.UTF8.GetString(wire.Body.Span));

        Assert.Equal(
            ["wce-causation", "wce-contract", "wce-message-type", "wce-outbox-message", "wce-redrive-count", "wce-tenant"],
            wire.ApplicationProperties.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("1", Assert.IsType<string>(wire.ApplicationProperties["wce-contract"]));
        Assert.Equal(IngressMessageTypes.ReceiptReceived, Assert.IsType<string>(wire.ApplicationProperties["wce-message-type"]));
        Assert.Equal(Tenant.Value, Assert.IsType<Guid>(wire.ApplicationProperties["wce-tenant"]));
        Assert.Equal(OutboxId, Assert.IsType<Guid>(wire.ApplicationProperties["wce-outbox-message"]));
        Assert.Equal(ReceiptId, Assert.IsType<Guid>(wire.ApplicationProperties["wce-causation"]));
        Assert.Equal(0, Assert.IsType<int>(wire.ApplicationProperties["wce-redrive-count"]));

        // The adapter cannot alter the properties it was handed.
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, object>)wire.ApplicationProperties)["wce-tenant"] = Guid.NewGuid());
    }

    [Fact]
    public void A_wire_message_read_back_yields_the_same_envelope()
    {
        var read = TransportEnvelope.Read(Received(Envelope().ToWire()));

        Assert.True(read.IsSuccess, read.IsSuccess ? string.Empty : read.Error.Code);
        Assert.Equal(Tenant, read.Value.TenantId);
        Assert.Equal(OutboxId, read.Value.OutboxMessageId);
        Assert.Equal(Correlation, read.Value.CorrelationId);
        Assert.Equal(ReceiptId, read.Value.CausationId);
        Assert.Equal(0, read.Value.RedriveCount);
        Assert.Equal(Envelope().MessageId, read.Value.MessageId);
        Assert.Equal(Envelope().SessionId, read.Value.SessionId);
    }

    [Theory]
    [InlineData("""{"receiptId":"0199a1b2-c3d4-7e5f-8a9b-111122223333","note":"work content"}""")]
    [InlineData("""{"note":"work content"}""")]
    [InlineData("""{"receiptid":"0199a1b2-c3d4-7e5f-8a9b-111122223333"}""")]
    [InlineData("""{"receiptId":"0199A1B2-C3D4-7E5F-8A9B-111122223333"}""")]
    [InlineData("""{"receiptId":"{0199a1b2-c3d4-7e5f-8a9b-111122223333}"}""")]
    [InlineData("""{"receiptId":"0199a1b2c3d47e5f8a9b111122223333"}""")]
    [InlineData("""{"receiptId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"receiptId":"not-an-id"}""")]
    [InlineData("""{"receiptId":42}""")]
    [InlineData("""{"receiptId":null}""")]
    [InlineData("""{"receiptId":{"id":"0199a1b2-c3d4-7e5f-8a9b-111122223333"}}""")]
    [InlineData("""{"receiptId":"0199a1b2-c3d4-7e5f-8a9b-111122223333","receiptId":"0199a1b2-c3d4-7e5f-8a9b-111122223333"}""")]
    [InlineData("""["0199a1b2-c3d4-7e5f-8a9b-111122223333"]""")]
    [InlineData("""{}""")]
    [InlineData("""null""")]
    [InlineData("""42""")]
    [InlineData("""not json""")]
    [InlineData("")]
    [InlineData("   ")]
    public void Only_the_content_free_receipt_marker_can_be_relayed(string payload)
    {
        var built = TransportEnvelope.FromOutbox(OutboxId, Tenant, IngressMessageTypes.ReceiptReceived, payload, Correlation);

        Assert.True(built.IsFailure);
        Assert.Equal(TransportErrors.BodyInvalid, built.Error);
    }

    [Fact]
    public void Malformed_text_and_oversized_payloads_are_not_markers()
    {
        var loneSurrogate = "{\"receiptId\":\"" + (char)0xD800 + "\"}";
        var oversized = "{\"receiptId\":\"" + ReceiptId.ToString("D") + "\"" + new string(' ', TransportContract.MaxBodyBytes) + "}";

        foreach (var payload in new[] { loneSurrogate, oversized })
        {
            var built = TransportEnvelope.FromOutbox(OutboxId, Tenant, IngressMessageTypes.ReceiptReceived, payload, Correlation);
            Assert.Equal(TransportErrors.BodyInvalid, built.Error);
        }
    }

    [Fact]
    public void Only_a_known_type_with_full_identity_can_be_relayed()
    {
        var cases = new (string Case, Result<TransportEnvelope> Built, Error Expected)[]
        {
            ("unknown type", TransportEnvelope.FromOutbox(OutboxId, Tenant, "workcontinuity.other", Marker(ReceiptId), Correlation), TransportErrors.MessageTypeUnsupported),
            ("empty type", TransportEnvelope.FromOutbox(OutboxId, Tenant, string.Empty, Marker(ReceiptId), Correlation), TransportErrors.MessageTypeUnsupported),
            ("no tenant", TransportEnvelope.FromOutbox(OutboxId, default, IngressMessageTypes.ReceiptReceived, Marker(ReceiptId), Correlation), TransportErrors.TenantInvalid),
            ("no outbox id", TransportEnvelope.FromOutbox(Guid.Empty, Tenant, IngressMessageTypes.ReceiptReceived, Marker(ReceiptId), Correlation), TransportErrors.IdentityInvalid),
            ("no correlation", TransportEnvelope.FromOutbox(OutboxId, Tenant, IngressMessageTypes.ReceiptReceived, Marker(ReceiptId), default), TransportErrors.CorrelationInvalid),
        };

        foreach (var (name, built, expected) in cases)
        {
            Assert.True(built.IsFailure, name);
            Assert.True(expected == built.Error, $"{name}: {built.Error.Code}");
        }
    }

    [Fact]
    public void Every_malformed_received_message_is_refused_with_its_stable_reason()
    {
        var wire = Envelope().ToWire();
        var otherTenant = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-777788889999");

        var cases = new (string Case, ReceivedTransportMessage Message, Error Expected)[]
        {
            ("no contract", Received(wire, without: "wce-contract"), TransportErrors.ContractUnsupported),
            ("contract 2", Received(wire, set: ("wce-contract", "2")), TransportErrors.ContractUnsupported),
            ("contract as a number", Received(wire, set: ("wce-contract", 1)), TransportErrors.ContractUnsupported),
            ("no type", Received(wire, without: "wce-message-type"), TransportErrors.MessageTypeUnsupported),
            ("unknown type", Received(wire, set: ("wce-message-type", "workcontinuity.other")), TransportErrors.MessageTypeUnsupported),
            ("no content type", Received(wire) with { ContentType = null }, TransportErrors.ContentTypeInvalid),
            ("text content type", Received(wire) with { ContentType = "text/plain" }, TransportErrors.ContentTypeInvalid),
            ("no tenant", Received(wire, without: "wce-tenant"), TransportErrors.TenantInvalid),
            ("tenant as text", Received(wire, set: ("wce-tenant", Tenant.Value.ToString("D"))), TransportErrors.TenantInvalid),
            ("empty tenant", Received(wire, set: ("wce-tenant", Guid.Empty)), TransportErrors.TenantInvalid),
            ("no identity", Received(wire, without: "wce-outbox-message"), TransportErrors.IdentityInvalid),
            ("identity as text", Received(wire, set: ("wce-outbox-message", OutboxId.ToString("D"))), TransportErrors.IdentityInvalid),
            ("empty identity", Received(wire, set: ("wce-outbox-message", Guid.Empty)), TransportErrors.IdentityInvalid),
            ("no re-drive count", Received(wire, without: "wce-redrive-count"), TransportErrors.RedriveCountInvalid),
            ("negative re-drive count", Received(wire, set: ("wce-redrive-count", -1)), TransportErrors.RedriveCountInvalid),
            ("re-drive count past the limit", Received(wire, set: ("wce-redrive-count", TransportContract.MaxRedriveCount + 1)), TransportErrors.RedriveCountInvalid),
            ("re-drive count as a long", Received(wire, set: ("wce-redrive-count", 0L)), TransportErrors.RedriveCountInvalid),
            ("re-drive count without its message id", Received(wire, set: ("wce-redrive-count", 1)), TransportErrors.MessageIdMismatch),
            ("another message id", Received(wire) with { MessageId = Guid.NewGuid().ToString("D") }, TransportErrors.MessageIdMismatch),
            ("no message id", Received(wire) with { MessageId = null }, TransportErrors.MessageIdMismatch),
            ("uppercase message id", Received(wire) with { MessageId = OutboxId.ToString("D").ToUpperInvariant() }, TransportErrors.MessageIdMismatch),
            ("another tenant's session", Received(wire) with { SessionId = otherTenant.ToString("D") }, TransportErrors.SessionMismatch),
            ("tenant moved to another session", Received(wire, set: ("wce-tenant", otherTenant)), TransportErrors.SessionMismatch),
            ("no session", Received(wire) with { SessionId = null }, TransportErrors.SessionMismatch),
            ("no correlation", Received(wire) with { CorrelationId = null }, TransportErrors.CorrelationInvalid),
            ("correlation with a line break", Received(wire) with { CorrelationId = "corr-1234\nforged" }, TransportErrors.CorrelationInvalid),
            ("no causation", Received(wire, without: "wce-causation"), TransportErrors.CausationInvalid),
            ("causation as text", Received(wire, set: ("wce-causation", ReceiptId.ToString("D"))), TransportErrors.CausationInvalid),
            ("empty causation", Received(wire, set: ("wce-causation", Guid.Empty)), TransportErrors.CausationInvalid),
            ("causation disagreeing with the body", Received(wire, set: ("wce-causation", Guid.NewGuid())), TransportErrors.BodyInvalid),
            ("body with content", Received(wire) with { Body = Bytes($$"""{"receiptId":"{{ReceiptId:D}}","note":"x"}""") }, TransportErrors.BodyInvalid),
            ("body not json", Received(wire) with { Body = Bytes("receipt") }, TransportErrors.BodyInvalid),
            ("empty body", Received(wire) with { Body = ReadOnlyMemory<byte>.Empty }, TransportErrors.BodyInvalid),
            ("invalid utf-8 body", Received(wire) with { Body = new byte[] { 0x7B, 0x22, 0xFF, 0xFE, 0x22, 0x7D } }, TransportErrors.BodyInvalid),
            ("oversized body", Received(wire) with { Body = new byte[TransportContract.MaxBodyBytes + 1] }, TransportErrors.BodyInvalid),
        };

        foreach (var (name, message, expected) in cases)
        {
            var read = TransportEnvelope.Read(message);
            Assert.True(read.IsFailure, $"{name} was accepted");
            Assert.True(expected == read.Error, $"{name}: expected {expected.Code}, got {read.Error.Code}");
        }

        // Every stable reason in the contract is exercised above.
        Assert.Empty(TransportErrors.EnvelopeErrors.Except(cases.Select(item => item.Expected)));
    }

    [Fact]
    public void Unknown_properties_such_as_the_brokers_own_are_ignored()
    {
        var properties = new Dictionary<string, object>(Envelope().ToWire().ApplicationProperties, StringComparer.Ordinal)
        {
            ["Diagnostic-Id"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["DeadLetterReason"] = "transport.retries_exhausted",
        };

        var read = TransportEnvelope.Read(Received(Envelope().ToWire()) with { ApplicationProperties = properties });

        Assert.True(read.IsSuccess);
    }

    [Fact]
    public void A_re_drive_keeps_identity_and_advances_only_the_count_and_the_message_id()
    {
        var original = Envelope();
        var current = original;

        for (var redrive = 1; redrive <= TransportContract.MaxRedriveCount; redrive++)
        {
            var next = current.ForRedrive();
            Assert.True(next.IsSuccess);
            current = next.Value;

            Assert.Equal(redrive, current.RedriveCount);
            Assert.Equal($"{OutboxId:D}:redrive:{redrive}", current.MessageId);
            Assert.Equal(original.TenantId, current.TenantId);
            Assert.Equal(original.SessionId, current.SessionId);
            Assert.Equal(original.CorrelationId, current.CorrelationId);
            Assert.Equal(original.CausationId, current.CausationId);
            Assert.Equal(original.OutboxMessageId, current.OutboxMessageId);
            Assert.Equal(original.Body, current.Body);

            // The re-driven copy is a valid message on its own, and its message id is deterministic.
            var read = TransportEnvelope.Read(Received(current.ToWire()));
            Assert.True(read.IsSuccess);
            Assert.Equal(current.MessageId, read.Value.MessageId);
            Assert.Equal(current.MessageId, original.ForRedriveTimes(redrive).MessageId);
        }

        var refused = current.ForRedrive();
        Assert.True(refused.IsFailure);
        Assert.Equal(TransportErrors.RedriveLimitReached, refused.Error);
    }

    [Fact]
    public void Transport_errors_are_distinct_safe_tokens_with_fixed_messages()
    {
        var errors = typeof(TransportErrors)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(Error))
            .Select(property => (Error)property.GetValue(null)!)
            .ToList();

        Assert.Equal(16, errors.Count);
        Assert.Equal(errors.Count, errors.Select(error => error.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(errors, error =>
        {
            Assert.True(EventText.IsToken(error.Code), error.Code);
            Assert.True(TransportContract.IsSafeReason(error.Code), error.Code);
            Assert.StartsWith("transport.", error.Code, StringComparison.Ordinal);
        });
        Assert.Equal(11, TransportErrors.EnvelopeErrors.Count);
    }

    [Fact]
    public void Safe_reasons_are_bounded_tokens_and_free_text_is_not()
    {
        Assert.True(TransportContract.IsSafeReason("transport.retries_exhausted"));
        Assert.True(TransportContract.IsSafeReason("MaxDeliveryCountExceeded"));
        Assert.True(TransportContract.IsSafeReason("TTLExpiredException"));

        Assert.False(TransportContract.IsSafeReason(null));
        Assert.False(TransportContract.IsSafeReason(string.Empty));
        Assert.False(TransportContract.IsSafeReason("reason with spaces"));
        Assert.False(TransportContract.IsSafeReason("line\nbreak"));
        Assert.False(TransportContract.IsSafeReason("customer: Acme"));
        Assert.False(TransportContract.IsSafeReason("ünicode"));
        Assert.False(TransportContract.IsSafeReason(new string('a', TransportContract.MaxReasonLength + 1)));
    }

    [Fact]
    public void String_forms_never_echo_received_text_or_the_body()
    {
        var hostile = Received(Envelope().ToWire()) with
        {
            MessageId = "forged-message-marker",
            SessionId = "forged-session-marker",
            CorrelationId = "forged-correlation-marker",
            Body = Bytes("""{"note":"body-marker"}"""),
        };

        Assert.DoesNotContain("marker", hostile.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ReceiptId.ToString("D"), Envelope().ToWire().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("receiptId", Envelope().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dead_lettered_metadata_carries_ids_and_safe_reasons_only()
    {
        var valid = DeadLetteredMessage.From(Received(Envelope().ToWire()), "transport.retries_exhausted", DateTimeOffset.UnixEpoch);
        Assert.Equal("transport.retries_exhausted", valid.Reason);
        var envelope = Assert.IsType<DeadLetteredEnvelope>(valid.Envelope);
        Assert.Equal(OutboxId, envelope.OutboxMessageId);
        Assert.Equal(Tenant, envelope.TenantId);
        Assert.Equal(Correlation, envelope.CorrelationId);
        Assert.Equal(ReceiptId, envelope.CausationId);

        var forged = DeadLetteredMessage.From(
            Received(Envelope().ToWire()) with { SessionId = "other" },
            "free text naming a customer",
            DateTimeOffset.UnixEpoch);
        Assert.Equal(TransportContract.UnrecognizedReason, forged.Reason);
        Assert.Null(forged.Envelope);

        // No member of the metadata can hold a body or a description.
        Assert.DoesNotContain(
            typeof(DeadLetteredMessage).GetProperties().Concat(typeof(DeadLetteredEnvelope).GetProperties()),
            property => property.Name.Contains("Body", StringComparison.Ordinal)
                || property.Name.Contains("Description", StringComparison.Ordinal)
                || property.Name.Contains("Payload", StringComparison.Ordinal));
    }

    private static TransportEnvelope Envelope()
    {
        var built = TransportEnvelope.FromOutbox(
            OutboxId, Tenant, IngressMessageTypes.ReceiptReceived, Marker(ReceiptId), Correlation);
        Assert.True(built.IsSuccess, built.IsSuccess ? string.Empty : built.Error.Code);
        return built.Value;
    }

    private static ReadOnlyMemory<byte> Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static ReceivedTransportMessage Received(
        TransportWireMessage wire,
        string? without = null,
        (string Name, object Value)? set = null)
    {
        var properties = new Dictionary<string, object>(wire.ApplicationProperties, StringComparer.Ordinal);
        if (without is not null)
        {
            Assert.True(properties.Remove(without), without);
        }

        if (set is { } change)
        {
            properties[change.Name] = change.Value;
        }

        return new ReceivedTransportMessage(
            wire.MessageId, wire.SessionId, wire.CorrelationId, wire.ContentType, properties, wire.Body, DeliveryCount: 1, SequenceNumber: 7);
    }
}

/// <summary>Test helpers over the envelope.</summary>
internal static class TransportEnvelopeTestExtensions
{
    /// <summary>The envelope after <paramref name="times"/> re-drives from this one.</summary>
    public static TransportEnvelope ForRedriveTimes(this TransportEnvelope envelope, int times)
    {
        for (var index = 0; index < times; index++)
        {
            envelope = envelope.ForRedrive().Value;
        }

        return envelope;
    }
}
