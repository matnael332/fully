using System.Collections.Concurrent;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Ingress;

/// <summary>
/// The Gate 3C extension of the Gate 2 verifier contract, against the real local PostgreSQL: after verification and
/// only once the tenant is set from the connection, the verifier confirms the delivery against the connection's own
/// records, before anything is written. A refusal stores nothing and is permanent; an unavailable answer stores nothing
/// and fails the delivery so the source retries; an exception is a refusal logged by type only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class IngressConfirmationTests(DatabaseFixture fixture)
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Confirmation_runs_after_verification_in_the_connections_tenant_and_before_anything_is_written()
    {
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var receiptsSeenByConfirmation = -1;
        var verifier = new ConfirmingVerifier(async _ =>
        {
            receiptsSeenByConfirmation = (await IngressData.ReceiptsAsync(fixture, tenant, Token)).Count;
            return SourceConfirmation.Confirmed;
        });
        await using var host = new IngressHost(fixture, verifier);

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-confirmed", IngressData.PayloadWith(IngressData.NewMarker())));

        Assert.Equal(IngressStatus.Accepted, outcome.Status);
        Assert.Equal(["verify", "confirm"], verifier.Calls);

        // Verification saw no tenant; confirmation saw exactly the connection's, set server-side, and nothing was stored yet.
        var (atVerify, atConfirm) = (verifier.Tenants.First(), verifier.Tenants.Last());
        Assert.False(atVerify.IsResolved);
        Assert.True(atConfirm.TryGetTenantId(out var confirmedTenant));
        Assert.Equal(tenant, confirmedTenant);
        Assert.Equal(TenantResolutionSource.TrustedServiceIdentity, atConfirm.Source);
        Assert.Equal(0, receiptsSeenByConfirmation);
        Assert.Single(await IngressData.ReceiptsAsync(fixture, tenant, Token));
        Assert.False(host.TenantContextAccessor.Current.IsResolved);
    }

    [Fact]
    public async Task A_refused_confirmation_is_a_permanent_rejection_that_stores_nothing()
    {
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();
        await using var host = new IngressHost(fixture, new ConfirmingVerifier(_ => ValueTask.FromResult(SourceConfirmation.Refused)));

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-refused", IngressData.PayloadWith(marker)));

        AssertRejected(outcome);
        await AssertNothingStoredAsync(tenant, marker);
    }

    [Theory]
    [InlineData(SourceConfirmation.Unspecified)]
    [InlineData((SourceConfirmation)99)]
    public async Task An_unspecified_or_undefined_confirmation_is_a_refusal(SourceConfirmation answer)
    {
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();
        await using var host = new IngressHost(fixture, new ConfirmingVerifier(_ => ValueTask.FromResult(answer)));

        AssertRejected(await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-undefined", IngressData.PayloadWith(marker))));
        await AssertNothingStoredAsync(tenant, marker);
    }

    [Fact]
    public async Task An_unavailable_confirmation_fails_the_delivery_so_the_source_retries_and_the_retry_converges()
    {
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();
        var answer = SourceConfirmation.Unavailable;
        await using var host = new IngressHost(fixture, new ConfirmingVerifier(_ => ValueTask.FromResult(answer)));
        var delivery = SyntheticSource.Delivery(connection, "evt-retried", IngressData.PayloadWith(marker));

        var failed = await host.SubmitAsync(delivery);
        Assert.Equal(IngressStatus.Failed, failed.Status);
        Assert.False(failed.IsAcknowledged);
        Assert.Equal(IngressErrors.Unavailable, failed.Error);
        await AssertNothingStoredAsync(tenant, marker);

        answer = SourceConfirmation.Confirmed;
        var accepted = await host.SubmitAsync(delivery);
        var repeated = await host.SubmitAsync(delivery);

        Assert.Equal(IngressStatus.Accepted, accepted.Status);
        Assert.Equal(IngressStatus.Duplicate, repeated.Status);
        Assert.Equal(accepted.ReceiptId, repeated.ReceiptId);
        Assert.Single(await IngressData.ReceiptsAsync(fixture, tenant, Token));
        Assert.Single(await IngressData.MarkersAsync(fixture, tenant, Token));
    }

    [Fact]
    public async Task A_confirmation_that_throws_is_a_refusal_logged_by_type_only()
    {
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var marker = IngressData.NewMarker();
        await using var host = new IngressHost(
            fixture,
            new ConfirmingVerifier(submission => throw new InvalidOperationException("Could not confirm " + submission.Proof)));
        var delivery = SyntheticSource.Delivery(connection, "evt-throws", IngressData.PayloadWith(marker));

        AssertRejected(await host.SubmitAsync(delivery));
        await AssertNothingStoredAsync(tenant, marker);

        Assert.Contains(host.Logs.Entries, entry => entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.All(host.Logs.Entries, entry =>
        {
            Assert.DoesNotContain(delivery.Proof!, entry.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, entry.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Confirmation_is_never_reached_for_an_unverified_unknown_or_mismatched_delivery()
    {
        var (_, connection) = await IngressData.NewConnectionAsync(fixture, Token);
        var (_, otherSource) = await IngressData.NewConnectionAsync(fixture, Token, sourceSystem: "other-source");
        var verifier = new ConfirmingVerifier(_ => ValueTask.FromResult(SourceConfirmation.Confirmed));
        await using var host = new IngressHost(fixture, verifier);

        AssertRejected(await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-bad-proof", "{}", proof: "not-the-proof")));
        Assert.Equal(["verify"], verifier.Calls);

        verifier.Calls.Clear();
        Assert.Equal(
            IngressErrors.UnknownConnection,
            (await host.SubmitAsync(SyntheticSource.Delivery(Guid.CreateVersion7(), "evt-unknown", "{}"))).Error);
        Assert.Equal(
            IngressErrors.SourceMismatch,
            (await host.SubmitAsync(SyntheticSource.Delivery(otherSource, "evt-mismatch", "{}"))).Error);
        Assert.Empty(verifier.Calls);
    }

    [Fact]
    public async Task A_confirmed_delivery_still_meets_the_tenant_status_check()
    {
        var (tenant, connection) = await IngressData.NewConnectionAsync(fixture, Token, tenantStatus: "Suspended");
        var marker = IngressData.NewMarker();
        var verifier = new ConfirmingVerifier(_ => ValueTask.FromResult(SourceConfirmation.Confirmed));
        await using var host = new IngressHost(fixture, verifier);

        var outcome = await host.SubmitAsync(SyntheticSource.Delivery(connection, "evt-suspended", IngressData.PayloadWith(marker)));

        Assert.Equal(IngressStatus.Rejected, outcome.Status);
        Assert.Equal(IngressErrors.TenantInactive, outcome.Error);
        Assert.Equal(["verify", "confirm"], verifier.Calls);
        await AssertNothingStoredAsync(tenant, marker);
    }

    private static void AssertRejected(IngressOutcome outcome)
    {
        Assert.Equal(IngressStatus.Rejected, outcome.Status);
        Assert.False(outcome.IsAcknowledged);
        Assert.Null(outcome.ReceiptId);
        Assert.Equal(IngressErrors.SourceNotVerified, outcome.Error);
        Assert.Equal(QuarantineReason.InvalidAuthentication, outcome.RejectionReason);
    }

    private async Task AssertNothingStoredAsync(TenantId tenant, string marker)
    {
        Assert.Empty(await IngressData.ReceiptsAsync(fixture, tenant, Token));
        Assert.Empty(await IngressData.MarkersAsync(fixture, tenant, Token));
        Assert.Equal(0, await IngressData.CountStoredContentAsync(fixture, marker, Token));
    }

    /// <summary>
    /// Verifies like the synthetic source and confirms as the test decides, recording each call and the tenant context
    /// it ran in.
    /// </summary>
    private sealed class ConfirmingVerifier(Func<IngressSubmission, ValueTask<SourceConfirmation>> confirm) : ISourceVerifier
    {
        private readonly SyntheticSourceVerifier _synthetic = new();
        private readonly TenantContextAccessor _tenants = new();

        public string SourceSystem => SyntheticSource.System;

        public ConcurrentQueue<string> Calls { get; } = new();

        public ConcurrentQueue<ITenantContext> Tenants { get; } = new();

        public ValueTask<bool> VerifyAsync(IngressSubmission submission, CancellationToken cancellationToken)
        {
            Calls.Enqueue("verify");
            Tenants.Enqueue(_tenants.Current);
            return _synthetic.VerifyAsync(submission, cancellationToken);
        }

        public ValueTask<SourceConfirmation> ConfirmAsync(IngressSubmission submission, CancellationToken cancellationToken)
        {
            Calls.Enqueue("confirm");
            Tenants.Enqueue(_tenants.Current);
            return confirm(submission);
        }
    }
}
