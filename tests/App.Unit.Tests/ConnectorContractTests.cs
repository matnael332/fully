using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The Gate 3A provider-neutral connector contract: complete, consistent operation descriptors; identities bound to
/// tenant and connection and never an email; a closed failure taxonomy with fixed errors; a bounded retry policy that
/// honours the provider's timing; and results that always say whose authority was used.
/// </summary>
public sealed class ConnectorContractTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("0192a3b4-0000-7000-8000-0000000000a1"));
    private static readonly Guid Connection = Guid.Parse("0192a3b4-0000-7000-8000-0000000000c1");

    private static ConnectorOperationDescriptor Descriptor(
        string name = "provider.things.read",
        string purpose = "Read things.",
        ConnectorActionType action = ConnectorActionType.Read,
        ConnectorRiskLevel risk = ConnectorRiskLevel.Low,
        ConnectorSideEffect effect = ConnectorSideEffect.None,
        ConnectorRecovery recovery = ConnectorRecovery.NotRequired,
        TimeSpan? timeout = null,
        ConnectorRetryPolicy? retry = null,
        string scope = "provider.thing") =>
        new(
            name,
            purpose,
            "provider.thing-ids.v1",
            "provider.things.v1",
            action,
            risk,
            scope,
            effect,
            recovery,
            timeout ?? TimeSpan.FromSeconds(30),
            retry ?? ConnectorRetryPolicy.DefaultRead);

    [Fact]
    public void A_complete_read_descriptor_carries_every_declared_field_and_is_read_only()
    {
        var descriptor = Descriptor();

        Assert.Equal("provider.things.read", descriptor.Name);
        Assert.Equal("Read things.", descriptor.Purpose);
        Assert.Equal("provider.thing-ids.v1", descriptor.InputSchema);
        Assert.Equal("provider.things.v1", descriptor.OutputSchema);
        Assert.Equal(ConnectorActionType.Read, descriptor.ActionType);
        Assert.Equal(ConnectorRiskLevel.Low, descriptor.Risk);
        Assert.Equal("provider.thing", descriptor.ResourceScope);
        Assert.Equal(ConnectorSideEffect.None, descriptor.SideEffect);
        Assert.Equal(ConnectorRecovery.NotRequired, descriptor.Recovery);
        Assert.Equal(TimeSpan.FromSeconds(30), descriptor.Timeout);
        Assert.Same(ConnectorRetryPolicy.DefaultRead, descriptor.RetryPolicy);
        Assert.True(descriptor.IsReadOnly);
        Assert.Equal("ConnectorOperation(provider.things.read)", descriptor.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Provider.things")]
    [InlineData("provider things")]
    [InlineData("provider/things")]
    public void A_descriptor_name_must_be_a_token(string name)
    {
        Assert.Throws<ArgumentException>(() => Descriptor(name: name));
        Assert.Throws<ArgumentException>(() => Descriptor(scope: name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Two\nlines")]
    public void A_descriptor_purpose_must_be_one_line_of_text(string purpose)
    {
        Assert.Throws<ArgumentException>(() => Descriptor(purpose: purpose));
        Assert.Throws<ArgumentException>(() => Descriptor(purpose: new string('x', ConnectorOperationDescriptor.MaxPurposeLength + 1)));
    }

    [Fact]
    public void Every_classification_must_be_specified_and_defined()
    {
        Assert.Throws<ArgumentException>(() => Descriptor(action: ConnectorActionType.Unspecified));
        Assert.Throws<ArgumentException>(() => Descriptor(action: (ConnectorActionType)99));
        Assert.Throws<ArgumentException>(() => Descriptor(risk: ConnectorRiskLevel.Unspecified));
        Assert.Throws<ArgumentException>(() => Descriptor(risk: (ConnectorRiskLevel)99));
        Assert.Throws<ArgumentException>(() => Descriptor(effect: ConnectorSideEffect.Unspecified));
        Assert.Throws<ArgumentException>(() => Descriptor(recovery: ConnectorRecovery.Unspecified));
        Assert.Throws<ArgumentNullException>(() => new ConnectorOperationDescriptor(
            "provider.things.read", "Read things.", "a.v1", "b.v1", ConnectorActionType.Read, ConnectorRiskLevel.Low,
            "provider.thing", ConnectorSideEffect.None, ConnectorRecovery.NotRequired, TimeSpan.FromSeconds(1), null!));
    }

    [Fact]
    public void The_timeout_must_be_positive_and_bounded()
    {
        Assert.Throws<ArgumentException>(() => Descriptor(timeout: TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => Descriptor(timeout: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentException>(() => Descriptor(timeout: ConnectorOperationDescriptor.MaxTimeout + TimeSpan.FromTicks(1)));
        Assert.Equal(ConnectorOperationDescriptor.MaxTimeout, Descriptor(timeout: ConnectorOperationDescriptor.MaxTimeout).Timeout);
    }

    [Fact]
    public void A_read_cannot_declare_a_side_effect_and_recovery_must_match_the_effect()
    {
        Assert.Throws<ArgumentException>(() => Descriptor(effect: ConnectorSideEffect.ExternalReversible, recovery: ConnectorRecovery.Rollback));
        Assert.Throws<ArgumentException>(() => Descriptor(recovery: ConnectorRecovery.Rollback));
        Assert.Throws<ArgumentException>(() => Descriptor(
            action: ConnectorActionType.Change,
            effect: ConnectorSideEffect.ExternalReversible,
            recovery: ConnectorRecovery.NotRequired,
            retry: ConnectorRetryPolicy.None));
    }

    [Fact]
    public void Only_a_read_only_operation_may_retry()
    {
        Assert.Throws<ArgumentException>(() => Descriptor(
            action: ConnectorActionType.Change,
            effect: ConnectorSideEffect.ExternalReversible,
            recovery: ConnectorRecovery.Rollback,
            retry: ConnectorRetryPolicy.DefaultRead));

        var change = Descriptor(
            action: ConnectorActionType.Change,
            risk: ConnectorRiskLevel.Medium,
            effect: ConnectorSideEffect.ExternalReversible,
            recovery: ConnectorRecovery.Rollback,
            retry: ConnectorRetryPolicy.None);

        Assert.False(change.IsReadOnly);
        Assert.Equal(1, change.RetryPolicy.MaxAttempts);
    }

    [Fact]
    public void An_identity_is_bound_to_its_tenant_and_connection()
    {
        var identity = new ConnectorIdentity(Tenant, Connection, ConnectorIdentityKind.DelegatedUser, "58201774");

        Assert.True(identity.Serves(Tenant, Connection));
        Assert.False(identity.Serves(TenantId.New(), Connection));
        Assert.False(identity.Serves(Tenant, Guid.NewGuid()));
        Assert.Equal(ConnectorIdentityKind.DelegatedUser, identity.Kind);
        Assert.Equal("58201774", identity.Subject);
    }

    [Fact]
    public void An_identity_requires_every_component_and_has_no_default()
    {
        Assert.Throws<ArgumentException>(() => new ConnectorIdentity(default, Connection, ConnectorIdentityKind.ServiceIdentity, "svc-1"));
        Assert.Throws<ArgumentException>(() => new ConnectorIdentity(Tenant, Guid.Empty, ConnectorIdentityKind.ServiceIdentity, "svc-1"));
        Assert.Throws<ArgumentException>(() => new ConnectorIdentity(Tenant, Connection, ConnectorIdentityKind.Unspecified, "svc-1"));
        Assert.Throws<ArgumentException>(() => new ConnectorIdentity(Tenant, Connection, (ConnectorIdentityKind)7, "svc-1"));
    }

    [Theory]
    [InlineData("employee@example.com")]
    [InlineData("")]
    [InlineData(" 58201774")]
    [InlineData("5820\n1774")]
    public void An_identity_subject_is_a_provider_identifier_never_an_email(string subject)
    {
        Assert.Throws<ArgumentException>(() => new ConnectorIdentity(Tenant, Connection, ConnectorIdentityKind.DelegatedUser, subject));
    }

    [Fact]
    public void The_identity_string_form_omits_the_subject()
    {
        var identity = new ConnectorIdentity(Tenant, Connection, ConnectorIdentityKind.ServiceIdentity, "svc-support-7");

        Assert.DoesNotContain("svc-support-7", identity.ToString(), StringComparison.Ordinal);
        Assert.Contains("ServiceIdentity", identity.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_throttling_and_temporary_failures_are_transient()
    {
        var transient = new[]
        {
            ConnectorFailureKind.RateLimited, ConnectorFailureKind.ProviderTransient, ConnectorFailureKind.NetworkTransient,
        };

        foreach (var kind in Enum.GetValues<ConnectorFailureKind>().Where(kind => kind != ConnectorFailureKind.Unspecified))
        {
            var failure = new ConnectorFailure(kind, ConnectorErrors.ProviderUnavailable);
            Assert.Equal(transient.Contains(kind), failure.IsTransient);
        }
    }

    [Fact]
    public void A_failure_requires_a_kind_an_error_and_a_non_negative_wait()
    {
        Assert.Throws<ArgumentException>(() => new ConnectorFailure(ConnectorFailureKind.Unspecified, ConnectorErrors.RateLimited));
        Assert.Throws<ArgumentException>(() => new ConnectorFailure((ConnectorFailureKind)99, ConnectorErrors.RateLimited));
        Assert.Throws<ArgumentNullException>(() => new ConnectorFailure(ConnectorFailureKind.RateLimited, null!));
        Assert.Throws<ArgumentException>(() => new ConnectorFailure(ConnectorFailureKind.RateLimited, ConnectorErrors.RateLimited, TimeSpan.FromSeconds(-1)));

        var failure = new ConnectorFailure(ConnectorFailureKind.RateLimited, ConnectorErrors.RateLimited, TimeSpan.FromSeconds(3));
        Assert.Equal("ConnectorFailure(RateLimited, connector.provider.rate_limited)", failure.ToString());
    }

    [Fact]
    public void Connector_error_codes_are_unique_stable_and_their_messages_fixed()
    {
        var codes = ConnectorErrors.All.Select(error => error.Code).ToList();

        Assert.Equal(18, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ConnectorErrors.All, error =>
        {
            Assert.StartsWith("connector.", error.Code, StringComparison.Ordinal);
            Assert.True(error.Code.All(character => char.IsAsciiLetterLower(character) || character is '.' or '_'), error.Code);
            Assert.NotEqual(ErrorCategory.None, error.Category);
        });
    }

    [Fact]
    public void Permanent_failures_are_never_retried()
    {
        var policy = ConnectorRetryPolicy.DefaultRead;

        foreach (var kind in new[]
                 {
                     ConnectorFailureKind.InvalidRequest, ConnectorFailureKind.OperationNotPermitted,
                     ConnectorFailureKind.ConnectionDisabled, ConnectorFailureKind.CredentialUnavailable,
                     ConnectorFailureKind.AuthenticationFailed, ConnectorFailureKind.AuthorizationFailed,
                     ConnectorFailureKind.ProviderValidation, ConnectorFailureKind.ResourceNotFound,
                     ConnectorFailureKind.UnsupportedResponse,
                 })
        {
            var decision = policy.Decide(new ConnectorFailure(kind, ConnectorErrors.ProviderValidation), attempt: 1, jitter: 0.5);
            Assert.False(decision.Retry, kind.ToString());
        }
    }

    [Fact]
    public void Transient_failures_are_retried_with_growing_bounded_jittered_backoff_up_to_the_attempt_limit()
    {
        var policy = ConnectorRetryPolicy.DefaultRead;
        var failure = new ConnectorFailure(ConnectorFailureKind.ProviderTransient, ConnectorErrors.ProviderUnavailable);

        for (var attempt = 1; attempt < policy.MaxAttempts; attempt++)
        {
            var ceiling = TimeSpan.FromTicks(Math.Min(policy.BaseDelay.Ticks * (1L << (attempt - 1)), policy.MaxDelay.Ticks));
            var lowest = policy.Decide(failure, attempt, jitter: 0);
            var highest = policy.Decide(failure, attempt, jitter: 0.999999);

            Assert.True(lowest.Retry);
            Assert.Equal(ceiling / 2, lowest.Delay);
            Assert.InRange(highest.Delay, ceiling / 2, ceiling);
        }

        Assert.False(policy.Decide(failure, policy.MaxAttempts, jitter: 0.5).Retry);
        Assert.False(policy.Decide(failure, policy.MaxAttempts + 5, jitter: 0.5).Retry);
    }

    [Fact]
    public void A_provider_requested_wait_is_honoured_and_never_shortened()
    {
        var policy = ConnectorRetryPolicy.DefaultRead;

        var longer = policy.Decide(
            new ConnectorFailure(ConnectorFailureKind.RateLimited, ConnectorErrors.RateLimited, TimeSpan.FromSeconds(7)), 1, 0.5);
        var shorter = policy.Decide(
            new ConnectorFailure(ConnectorFailureKind.RateLimited, ConnectorErrors.RateLimited, TimeSpan.FromMilliseconds(10)), 1, 0.5);

        Assert.Equal(TimeSpan.FromSeconds(7), longer.Delay);
        Assert.Equal(policy.BackoffAfter(1, 0.5), shorter.Delay);
    }

    [Fact]
    public void A_provider_wait_beyond_the_cap_is_handed_back_instead_of_slept()
    {
        var policy = ConnectorRetryPolicy.DefaultRead;
        var failure = new ConnectorFailure(
            ConnectorFailureKind.RateLimited, ConnectorErrors.RateLimited, policy.MaxProviderDelay + TimeSpan.FromSeconds(1));

        Assert.False(policy.Decide(failure, attempt: 1, jitter: 0.5).Retry);
        Assert.True(policy.Decide(
            new ConnectorFailure(ConnectorFailureKind.RateLimited, ConnectorErrors.RateLimited, policy.MaxProviderDelay),
            attempt: 1,
            jitter: 0.5).Retry);
    }

    [Fact]
    public void The_no_retry_policy_never_retries_and_every_limit_is_validated()
    {
        var failure = new ConnectorFailure(ConnectorFailureKind.NetworkTransient, ConnectorErrors.NetworkFailure);
        Assert.False(ConnectorRetryPolicy.None.Decide(failure, 1, 0.5).Retry);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectorRetryPolicy(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectorRetryPolicy(ConnectorRetryPolicy.AttemptLimit + 1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectorRetryPolicy(2, TimeSpan.FromSeconds(-1), TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectorRetryPolicy(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectorRetryPolicy(2, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectorRetryPolicy.DefaultRead.BackoffAfter(0, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectorRetryPolicy.DefaultRead.BackoffAfter(1, 1.0));

        Assert.Equal(4, ConnectorRetryPolicy.DefaultRead.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(60), ConnectorRetryPolicy.DefaultRead.MaxProviderDelay);
    }

    [Fact]
    public void A_success_records_the_identity_and_the_value_never_renders()
    {
        var identity = new ConnectorIdentity(Tenant, Connection, ConnectorIdentityKind.DelegatedUser, "58201774");
        var operation = Descriptor();

        var success = ConnectorCallResult<string>.Succeeded(operation, identity, attempts: 2, "provider content");

        Assert.True(success.IsSuccess);
        Assert.Same(identity, success.Identity);
        Assert.Equal(2, success.Attempts);
        Assert.Equal("provider content", success.Value);
        Assert.Throws<InvalidOperationException>(() => success.Failure);
        Assert.DoesNotContain("provider content", success.ToString(), StringComparison.Ordinal);

        Assert.Throws<ArgumentNullException>(() => ConnectorCallResult<string>.Succeeded(operation, null!, 1, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectorCallResult<string>.Succeeded(operation, identity, 0, "x"));
    }

    [Fact]
    public void A_failure_carries_its_failure_and_may_lack_an_identity_when_none_was_acquired()
    {
        var failed = ConnectorCallResult<string>.Failed(
            Descriptor(),
            identity: null,
            attempts: 0,
            new ConnectorFailure(ConnectorFailureKind.ConnectionDisabled, ConnectorErrors.ConnectionDisabled));

        Assert.False(failed.IsSuccess);
        Assert.Null(failed.Identity);
        Assert.Equal(0, failed.Attempts);
        Assert.Throws<InvalidOperationException>(() => failed.Value);
        Assert.Equal("ConnectorCallResult(provider.things.read, connector.connection.disabled, 0 attempts)", failed.ToString());
    }
}
