using WorkContinuity.Core.Audit;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

public sealed class AuditRecordTests
{
    private static AuditRecord CreateRecord(
        AuditOutcome outcome = AuditOutcome.Succeeded,
        ResourceReference? resource = null,
        string? reason = null) =>
        new(
            TenantId.New(),
            new AuditActor(AuditActorKind.User, "01930000-0000-7000-8000-000000000001"),
            "action.approve",
            new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero),
            CorrelationId.New(),
            outcome,
            resource,
            reason);

    [Fact]
    public void A_record_represents_every_required_component()
    {
        var tenantId = TenantId.New();
        var actor = new AuditActor(AuditActorKind.User, "user-reference");
        var occurredAt = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var correlationId = CorrelationId.New();
        var resource = new ResourceReference("action", "action-42");

        var record = new AuditRecord(
            tenantId,
            actor,
            "action.approve",
            occurredAt,
            correlationId,
            AuditOutcome.Denied,
            resource,
            "approval expired");

        Assert.Equal(tenantId, record.TenantId);
        Assert.Equal(actor, record.Actor);
        Assert.Equal("action.approve", record.Operation);
        Assert.Equal(occurredAt, record.OccurredAtUtc);
        Assert.Equal(correlationId, record.CorrelationId);
        Assert.Equal(AuditOutcome.Denied, record.Outcome);
        Assert.Equal(resource, record.Resource);
        Assert.Equal("approval expired", record.Reason);
    }

    [Fact]
    public void A_resource_and_a_reason_are_optional()
    {
        var record = CreateRecord();

        Assert.Null(record.Resource);
        Assert.Null(record.Reason);
    }

    [Fact]
    public void A_tenant_is_required()
    {
        Assert.Throws<ArgumentException>(() => new AuditRecord(
            default,
            new AuditActor(AuditActorKind.System, "retention-job"),
            "retention.purge",
            DateTimeOffset.UnixEpoch,
            CorrelationId.New(),
            AuditOutcome.Succeeded));
    }

    [Fact]
    public void An_actor_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => new AuditRecord(
            TenantId.New(),
            null!,
            "retention.purge",
            DateTimeOffset.UnixEpoch,
            CorrelationId.New(),
            AuditOutcome.Succeeded));
    }

    [Fact]
    public void A_correlation_id_is_required()
    {
        Assert.Throws<ArgumentException>(() => new AuditRecord(
            TenantId.New(),
            new AuditActor(AuditActorKind.System, "retention-job"),
            "retention.purge",
            DateTimeOffset.UnixEpoch,
            default,
            AuditOutcome.Succeeded));
    }

    [Fact]
    public void An_outcome_is_required()
    {
        Assert.Throws<ArgumentException>(() => new AuditRecord(
            TenantId.New(),
            new AuditActor(AuditActorKind.System, "retention-job"),
            "retention.purge",
            DateTimeOffset.UnixEpoch,
            CorrelationId.New(),
            AuditOutcome.None));
    }

    [Fact]
    public void An_operation_is_required()
    {
        Assert.Throws<ArgumentException>(() => new AuditRecord(
            TenantId.New(),
            new AuditActor(AuditActorKind.System, "retention-job"),
            "   ",
            DateTimeOffset.UnixEpoch,
            CorrelationId.New(),
            AuditOutcome.Succeeded));
    }

    [Fact]
    public void An_unknown_outcome_is_representable_and_distinct_from_failure()
    {
        // A sent command whose result could not be established must be reconcilable rather than
        // retried as though it had failed.
        Assert.Equal(AuditOutcome.Unknown, CreateRecord(AuditOutcome.Unknown).Outcome);
        Assert.NotEqual(AuditOutcome.Failed, AuditOutcome.Unknown);
    }

    [Fact]
    public void An_overlong_reason_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => CreateRecord(reason: new string('r', AuditRecord.MaxReasonLength + 1)));
    }
}

public sealed class AuditActorTests
{
    [Fact]
    public void An_actor_keeps_its_kind_and_opaque_reference()
    {
        var actor = new AuditActor(AuditActorKind.ServiceIdentity, "worker-01");

        Assert.Equal(AuditActorKind.ServiceIdentity, actor.Kind);
        Assert.Equal("worker-01", actor.Reference);
    }

    [Fact]
    public void A_kind_is_required()
    {
        Assert.Throws<ArgumentException>(() => new AuditActor(AuditActorKind.None, "reference"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    public void A_reference_is_required(string reference)
    {
        Assert.Throws<ArgumentException>(() => new AuditActor(AuditActorKind.User, reference));
    }

    [Fact]
    public void Human_service_and_system_actors_are_all_representable()
    {
        Assert.Equal(
            ["None", "User", "ServiceIdentity", "System"],
            Enum.GetNames<AuditActorKind>());
    }
}

public sealed class ResourceReferenceTests
{
    [Fact]
    public void A_reference_keeps_its_type_and_id()
    {
        var reference = new ResourceReference("task", "task-7");

        Assert.Equal("task", reference.ResourceType);
        Assert.Equal("task-7", reference.ResourceId);
    }

    [Theory]
    [InlineData("", "id")]
    [InlineData("   ", "id")]
    [InlineData("type", "")]
    [InlineData("type", "   ")]
    public void A_type_and_an_id_are_required(string resourceType, string resourceId)
    {
        Assert.Throws<ArgumentException>(() => new ResourceReference(resourceType, resourceId));
    }

    [Fact]
    public void A_reference_is_a_pointer_and_not_a_copy_of_the_resource()
    {
        // Only a type plus an identifier: an audit trail can say which task changed without
        // becoming a duplicate of the task.
        Assert.Equal(
            ["ResourceType", "ResourceId"],
            typeof(ResourceReference)
                .GetProperties()
                .Select(property => property.Name)
                .Where(name => name != "EqualityContract")
                .ToArray());
    }
}

public sealed class AuditSinkContractTests
{
    [Fact]
    public void The_audit_sink_is_a_contract_with_no_implementation_in_core()
    {
        // Gate 1C ships contracts only. Persistence, with its own retention lifecycle separate
        // from raw work data, belongs to the storage Gate.
        var implementations = typeof(IAuditSink).Assembly
            .GetTypes()
            .Where(type => typeof(IAuditSink).IsAssignableFrom(type) && !type.IsInterface)
            .ToList();

        Assert.Empty(implementations);
    }

    [Fact]
    public void The_sink_accepts_only_the_audit_record_contract()
    {
        var method = Assert.Single(typeof(IAuditSink).GetMethods());

        Assert.Equal(nameof(IAuditSink.WriteAsync), method.Name);
        Assert.Equal(typeof(AuditRecord), method.GetParameters()[0].ParameterType);
    }
}
