using System.Reflection;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Results;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The Gate 2B ingress contract: what a delivery must look like before any tenant is known, and what
/// each settled outcome promises the caller about acknowledging the source.
/// </summary>
public sealed class IngressContractTests
{
    private static readonly Guid ConnectionId = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    [Fact]
    public void A_well_formed_delivery_passes_validation_with_or_without_a_source_event_id()
    {
        Assert.Null(Delivery().Validate());
        Assert.Null(Delivery(sourceEventId: null).Validate());
    }

    [Fact]
    public void Each_malformed_part_is_refused_with_its_own_stable_code()
    {
        var cases = new (string Case, IngressSubmission Delivery, Error Expected)[]
        {
            ("empty connection id", Delivery(connectionId: Guid.Empty), IngressErrors.InvalidConnectionId),
            ("uppercase source system", Delivery(sourceSystem: "Synthetic"), IngressErrors.InvalidSourceSystem),
            ("empty source system", Delivery(sourceSystem: string.Empty), IngressErrors.InvalidSourceSystem),
            ("blank source event id", Delivery(sourceEventId: " "), IngressErrors.InvalidSourceEventId),
            ("padded source event id", Delivery(sourceEventId: " evt-1"), IngressErrors.InvalidSourceEventId),
            ("lone surrogate in the source event id", Delivery(sourceEventId: "evt-" + (char)0xD800), IngressErrors.InvalidSourceEventId),
            ("overlong source event id", Delivery(sourceEventId: new string('e', EventText.MaxIdentifierLength + 1)), IngressErrors.InvalidSourceEventId),
            ("schema version with a space", Delivery(schemaVersion: "v 1"), IngressErrors.InvalidSchemaVersion),
            ("empty payload", Delivery(payload: string.Empty), IngressErrors.EmptyPayload),
            ("whitespace payload", Delivery(payload: "   "), IngressErrors.EmptyPayload),
        };

        foreach (var (name, delivery, expected) in cases)
        {
            var actual = delivery.Validate();
            Assert.True(Equals(expected, actual), $"{name}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void A_delivery_requires_its_text_parts_to_be_present()
    {
        Assert.Throws<ArgumentNullException>(() => new IngressSubmission(
            ConnectionId, null!, "evt-1", "1", "{}", CorrelationId.New(), proof: null));
        Assert.Throws<ArgumentNullException>(() => new IngressSubmission(
            ConnectionId, "synthetic", "evt-1", null!, "{}", CorrelationId.New(), proof: null));
        Assert.Throws<ArgumentNullException>(() => new IngressSubmission(
            ConnectionId, "synthetic", "evt-1", "1", null!, CorrelationId.New(), proof: null));
    }

    [Fact]
    public void Only_a_stored_delivery_may_be_acknowledged()
    {
        var correlation = CorrelationId.New();
        var receipt = Guid.CreateVersion7();

        var accepted = IngressOutcome.Accepted(receipt, correlation);
        var duplicate = IngressOutcome.Duplicate(receipt, correlation);
        var rejected = IngressOutcome.Rejected(IngressErrors.UnknownConnection, correlation);
        var failed = IngressOutcome.Failed(correlation);

        Assert.Equal(IngressStatus.Accepted, accepted.Status);
        Assert.True(accepted.IsAcknowledged);
        Assert.Equal(receipt, accepted.ReceiptId);
        Assert.Null(accepted.Error);
        Assert.Equal(QuarantineReason.None, accepted.RejectionReason);

        Assert.Equal(IngressStatus.Duplicate, duplicate.Status);
        Assert.True(duplicate.IsAcknowledged);
        Assert.Equal(receipt, duplicate.ReceiptId);
        Assert.Null(duplicate.Error);

        Assert.Equal(IngressStatus.Rejected, rejected.Status);
        Assert.False(rejected.IsAcknowledged);
        Assert.Null(rejected.ReceiptId);
        Assert.Equal(IngressErrors.UnknownConnection, rejected.Error);
        Assert.Equal(QuarantineReason.UnknownTenant, rejected.RejectionReason);

        Assert.Equal(IngressStatus.Failed, failed.Status);
        Assert.False(failed.IsAcknowledged);
        Assert.Null(failed.ReceiptId);
        Assert.Equal(IngressErrors.Unavailable, failed.Error);
        Assert.Equal(QuarantineReason.None, failed.RejectionReason);

        Assert.All([accepted, duplicate, rejected, failed], outcome => Assert.Equal(correlation, outcome.CorrelationId));
    }

    [Fact]
    public void An_outcome_cannot_be_built_without_its_evidence()
    {
        Assert.Throws<ArgumentException>(() => IngressOutcome.Accepted(Guid.Empty, CorrelationId.New()));
        Assert.Throws<ArgumentException>(() => IngressOutcome.Duplicate(Guid.Empty, CorrelationId.New()));
        Assert.Throws<ArgumentException>(() => IngressOutcome.Accepted(Guid.CreateVersion7(), default));
        Assert.Throws<ArgumentException>(() => IngressOutcome.Rejected(IngressErrors.EmptyPayload, default));
        Assert.Throws<ArgumentException>(() => IngressOutcome.Failed(default));

        // A failure must be retried, so it can never be dressed up as a permanent rejection.
        Assert.Throws<ArgumentException>(() => IngressOutcome.Rejected(IngressErrors.Unavailable, CorrelationId.New()));
        Assert.Throws<ArgumentException>(() => IngressOutcome.Rejected(
            Error.Validation("other.code", "Another error."), CorrelationId.New()));
    }

    [Fact]
    public void Every_rejection_maps_to_its_canonical_quarantine_reason()
    {
        var expected = new Dictionary<Error, QuarantineReason>
        {
            [IngressErrors.InvalidConnectionId] = QuarantineReason.BadSchema,
            [IngressErrors.InvalidSourceSystem] = QuarantineReason.BadSchema,
            [IngressErrors.InvalidSourceEventId] = QuarantineReason.BadSchema,
            [IngressErrors.InvalidSchemaVersion] = QuarantineReason.BadSchema,
            [IngressErrors.EmptyPayload] = QuarantineReason.BadSchema,
            [IngressErrors.PayloadNotStorable] = QuarantineReason.BadSchema,
            [IngressErrors.UnknownConnection] = QuarantineReason.UnknownTenant,
            [IngressErrors.TenantInactive] = QuarantineReason.UnknownTenant,
            [IngressErrors.SourceMismatch] = QuarantineReason.InvalidAuthentication,
            [IngressErrors.SourceNotVerified] = QuarantineReason.InvalidAuthentication,
        };

        Assert.Equal(
            expected.Keys.Select(error => error.Code).Order(StringComparer.Ordinal),
            IngressErrors.Rejections.Select(error => error.Code).Order(StringComparer.Ordinal));

        foreach (var (error, reason) in expected)
        {
            Assert.Equal(reason, IngressErrors.RejectionReasonFor(error));
        }
    }

    [Fact]
    public void Every_declared_ingress_error_is_either_a_listed_rejection_or_the_one_retryable_failure()
    {
        var declared = typeof(IngressErrors)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(Error))
            .Select(property => (Error)property.GetValue(null)!)
            .ToList();

        Assert.Equal(declared.Count, declared.Select(error => error.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            declared.Where(error => error != IngressErrors.Unavailable)
                .Select(error => error.Code)
                .Order(StringComparer.Ordinal),
            IngressErrors.Rejections.Select(error => error.Code).Order(StringComparer.Ordinal));
        Assert.Equal(ErrorCategory.Infrastructure, IngressErrors.Unavailable.Category);
    }

    private static IngressSubmission Delivery(
        Guid? connectionId = null,
        string sourceSystem = "synthetic",
        string? sourceEventId = "evt-1",
        string schemaVersion = "1",
        string payload = """{"id":1}""",
        string? proof = "proof") =>
        new(connectionId ?? ConnectionId, sourceSystem, sourceEventId, schemaVersion, payload, CorrelationId.New(), proof);
}
