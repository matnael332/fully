using System.Collections;
using System.Reflection;
using WorkContinuity.Core.Audit;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Raw work data and security audit data have separate purposes and separate retention
/// lifecycles. These tests assert the audit contracts cannot become shadow storage for work
/// content.
/// </summary>
public sealed class AuditContentSeparationTests
{
    private static readonly string[] ForbiddenMemberFragments =
    [
        "PAYLOAD",
        "CONTENT",
        "BODY",
        "DOCUMENT",
        "SNAPSHOT",
        "RAW",
        "ATTACHMENT",
        "TEXT",
        "NOTE",
        "COMMENT",
        "DESCRIPTION",
        "SUBJECT",
        "PROPERTIES",
        "METADATA",
        "BEFORE",
        "AFTER",
    ];

    private static readonly Type[] AuditContractTypes =
    [
        typeof(AuditRecord),
        typeof(AuditActor),
        typeof(ResourceReference),
    ];

    [Fact]
    public void No_audit_contract_exposes_a_work_content_member()
    {
        var offenders = new List<string>();

        foreach (var type in AuditContractTypes)
        {
            foreach (var property in type.GetProperties())
            {
                var upper = property.Name.ToUpperInvariant();

                if (ForbiddenMemberFragments.Any(
                    fragment => upper.Contains(fragment, StringComparison.Ordinal)))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_audit_record_exposes_exactly_the_approved_components()
    {
        var propertyNames = typeof(AuditRecord)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => name != "EqualityContract")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Actor",
                "CorrelationId",
                "OccurredAtUtc",
                "Operation",
                "Outcome",
                "Reason",
                "Resource",
                "TenantId",
            ],
            propertyNames);
    }

    [Fact]
    public void No_audit_contract_has_an_open_ended_member_that_could_carry_arbitrary_data()
    {
        // A dictionary, an object or a byte array would let any caller attach work content while
        // still satisfying the named-member checks above.
        var offenders = new List<string>();

        foreach (var type in AuditContractTypes)
        {
            foreach (var property in type.GetProperties())
            {
                var propertyType = property.PropertyType;

                var isOpenEnded = propertyType == typeof(object)
                    || propertyType == typeof(byte[])
                    || typeof(IDictionary).IsAssignableFrom(propertyType)
                    || (typeof(IEnumerable).IsAssignableFrom(propertyType)
                        && propertyType != typeof(string));

                if (isOpenEnded)
                {
                    offenders.Add($"{type.Name}.{property.Name} : {propertyType.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_audit_string_member_is_length_bounded()
    {
        // Bounded lengths stop an unbounded blob of work content being written into a reason or an
        // operation name.
        Assert.Equal(200, AuditRecord.MaxOperationLength);
        Assert.Equal(300, AuditRecord.MaxReasonLength);
        Assert.Equal(200, AuditActor.MaxReferenceLength);
        Assert.Equal(200, ResourceReference.MaxLength);
    }

    [Fact]
    public void A_resource_is_referenced_by_pointer_only()
    {
        var reference = new ResourceReference("task", "task-7");

        Assert.Equal("task", reference.ResourceType);
        Assert.Equal("task-7", reference.ResourceId);
        Assert.Equal(2, typeof(ResourceReference)
            .GetProperties()
            .Count(property => property.Name != "EqualityContract"));
    }

    [Fact]
    public void The_audit_contracts_carry_no_exception_or_stack_detail()
    {
        var offenders = new List<string>();

        foreach (var type in AuditContractTypes)
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                if (member.Name.Contains("Stack", StringComparison.OrdinalIgnoreCase)
                    || member.Name.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{type.Name}.{member.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }
}
