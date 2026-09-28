using WorkContinuity.Core.Results;
using WorkContinuity.Core.Transport;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The Gate 2C retry policy: success completes, a transient failure is retried with growing, bounded,
/// jittered backoff until the attempt limit dead-letters it, and a permanent failure is dead-lettered at
/// once under a safe code and a fixed description.
/// </summary>
public sealed class TransportRetryPolicyTests
{
    private static readonly Error Transient = Error.Infrastructure("processing.store.unavailable", "The store is unavailable.");

    [Fact]
    public void Success_completes_the_message()
    {
        var settlement = TransportRetryPolicy.Default.Settle(TransportHandlingResult.Succeeded, deliveryCount: 1, jitter: 0.5);

        Assert.Equal(TransportSettlementKind.Complete, settlement.Kind);
        Assert.Null(settlement.DeadLetterReason);
    }

    [Fact]
    public void A_transient_failure_is_retried_with_growing_bounded_jittered_backoff()
    {
        var policy = TransportRetryPolicy.Default;

        for (var attempt = 1; attempt < policy.MaxAttempts; attempt++)
        {
            var ceiling = TimeSpan.FromTicks(Math.Min(
                policy.BaseDelay.Ticks * (1L << (attempt - 1)), policy.MaxDelay.Ticks));

            var lowest = policy.Settle(TransportHandlingResult.RetryLater(Transient), attempt, jitter: 0);
            var highest = policy.Settle(TransportHandlingResult.RetryLater(Transient), attempt, jitter: 0.999999);

            Assert.Equal(TransportSettlementKind.Abandon, lowest.Kind);
            Assert.Equal(ceiling / 2, lowest.RetryAfter);
            Assert.InRange(highest.RetryAfter, ceiling / 2, ceiling);
            Assert.True(highest.RetryAfter > lowest.RetryAfter);
        }
    }

    [Fact]
    public void The_attempt_limit_dead_letters_a_transient_failure_as_retries_exhausted()
    {
        var policy = TransportRetryPolicy.Default;

        foreach (var deliveryCount in new[] { policy.MaxAttempts, policy.MaxAttempts + 1, 50 })
        {
            var settlement = policy.Settle(TransportHandlingResult.RetryLater(Transient), deliveryCount, jitter: 0.5);

            Assert.Equal(TransportSettlementKind.DeadLetter, settlement.Kind);
            Assert.Equal(TransportErrors.RetriesExhausted, settlement.DeadLetterReason);
        }
    }

    [Fact]
    public void A_permanent_failure_is_dead_lettered_at_once_under_its_safe_code_and_a_fixed_description()
    {
        var rejection = Error.Validation("processing.receipt.missing", "Receipt for customer Acme holds secret-marker");

        var settlement = TransportRetryPolicy.Default.Settle(TransportHandlingResult.Rejected(rejection), deliveryCount: 1, jitter: 0.5);

        Assert.Equal(TransportSettlementKind.DeadLetter, settlement.Kind);
        var reason = Assert.IsType<Error>(settlement.DeadLetterReason);
        Assert.Equal("processing.receipt.missing", reason.Code);
        Assert.Equal(TransportErrors.HandlerRejected.Message, reason.Message);
        Assert.DoesNotContain("secret-marker", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejection_code_that_is_not_a_safe_token_is_replaced()
    {
        var unsafeCode = Error.Validation("customer Acme: bad row", "Anything");

        var settlement = TransportRetryPolicy.Default.Settle(TransportHandlingResult.Rejected(unsafeCode), deliveryCount: 1, jitter: 0.5);

        Assert.Equal(TransportErrors.HandlerRejected, settlement.DeadLetterReason);
    }

    [Fact]
    public void The_default_policy_stays_inside_the_queues_delivery_and_lock_limits()
    {
        var policy = TransportRetryPolicy.Default;

        // The local emulator's event-processing queue allows 5 deliveries and Azure's 10; the consumer must
        // decide before the broker does. The longest backoff stays well inside the emulator's 1-minute lock.
        Assert.Equal(5, policy.MaxAttempts);
        Assert.True(policy.MaxDelay <= TimeSpan.FromSeconds(30));
        Assert.True(policy.DelayAfter(1000, 0.999999) <= policy.MaxDelay);
    }

    [Fact]
    public void Out_of_range_inputs_are_refused()
    {
        var policy = TransportRetryPolicy.Default;

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayAfter(0, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayAfter(1, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayAfter(1, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayAfter(1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransportRetryPolicy(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransportRetryPolicy(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => policy.Settle(new UnreportedResult().Value, 1, 0.5));
    }

    [Fact]
    public void Settlements_refuse_unsafe_reasons_and_negative_backoff()
    {
        Assert.Throws<ArgumentException>(() => TransportSettlement.DeadLetter(Error.Validation("free text reason", "x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransportSettlement.Abandon(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentNullException>(() => TransportHandlingResult.RetryLater(null!));
        Assert.Throws<ArgumentNullException>(() => TransportHandlingResult.Rejected(null!));
    }

    // A result whose status was never set, as a defective handler could produce through reflection.
    private sealed class UnreportedResult
    {
        public TransportHandlingResult Value { get; } = (TransportHandlingResult)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(TransportHandlingResult));
    }
}
