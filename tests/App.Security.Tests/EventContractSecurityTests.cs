using System.Collections;
using System.Reflection;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.SourceOfTruth;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// Security properties of the canonical event contract and the Source-of-Truth rules that must hold by
/// construction (Gate 2A).
/// </summary>
public sealed class EventContractSecurityTests
{
    [Fact]
    public void The_source_kinds_are_closed_and_include_no_ai_model_or_internal_intent()
    {
        // A kind that does not exist can never be authoritative. Adding one is a deliberate change here.
        string[] expected = ["None", "SaasConnector", "LocalAgent", "Employee", "IdentityProvider", "ActionExecutor"];

        Assert.Equal(expected, Enum.GetNames<EventSourceKind>());
    }

    [Fact]
    public void The_data_types_with_authority_are_exactly_those_prd_section_5_defines()
    {
        string[] expected =
        [
            "None",
            "TaskStatus",
            "PcActivity",
            "ApprovalDecision",
            "IdentityAndPermissions",
            "Deadline",
            "ActionResult",
        ];

        Assert.Equal(expected, Enum.GetNames<FactDataType>());
        Assert.Equal(
            Enum.GetValues<FactDataType>().Where(dataType => dataType != FactDataType.None).Order(),
            AuthorityTable.Rules.Keys.Order());
    }

    [Fact]
    public void Every_authority_rule_names_a_defined_non_ai_authority()
    {
        string[] expected =
        [
            "None",
            "SourceTaskSystem",
            "LocalAgent",
            "Employee",
            "OrganizationIdentitySystem",
            "DefiningSystem",
            "AuthoritativeDestination",
        ];

        Assert.Equal(expected, Enum.GetNames<AuthorityBasis>());
        Assert.DoesNotContain(AuthorityBasis.None, AuthorityTable.Rules.Values);
    }

    [Fact]
    public void Authority_is_decided_by_static_code_with_no_injectable_decision_maker()
    {
        // A static resolver with no mutable state and no strategy, delegate or service parameter cannot
        // be handed a model, a callback or configuration that picks the authority at run time.
        foreach (var type in new[] { typeof(SourceOfTruthResolver), typeof(AuthorityTable) })
        {
            Assert.True(type.IsAbstract && type.IsSealed, $"{type.Name} must be a static class.");

            var mutableFields = type
                .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsInitOnly && !field.IsLiteral)
                .Select(field => field.Name);
            Assert.Empty(mutableFields);
        }

        Type[] allowedParameterTypes =
        [
            typeof(FactDataType),
            typeof(EventSource),
            typeof(ObjectReference),
            typeof(CanonicalEvent),
        ];
        var parameterTypes = typeof(SourceOfTruthResolver)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Distinct();
        Assert.All(parameterTypes, parameterType => Assert.Contains(parameterType, allowedParameterTypes));
    }

    [Fact]
    public void The_retained_metadata_carries_no_work_content()
    {
        // Gate 2 owner decision 5: raw content expires by 30 days while provenance may be retained, so the
        // retained half must not be able to hold content at all.
        var properties = typeof(CanonicalEventMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Type[] contentTypes = [typeof(FieldChange), typeof(EventActor), typeof(CanonicalEventContent)];
        foreach (var property in properties)
        {
            Assert.DoesNotContain(property.PropertyType, contentTypes);
            var isCollection = property.PropertyType != typeof(string)
                && typeof(IEnumerable).IsAssignableFrom(property.PropertyType);
            Assert.False(isCollection, $"{property.Name} could hold a list of content.");
        }

        string[] contentWords = ["Before", "After", "Actor", "Change", "Reference", "Payload", "Content", "Body"];
        var contentNamed = properties
            .Where(property => contentWords.Any(word => property.Name.Contains(word, StringComparison.Ordinal)))
            .Select(property => property.Name);
        Assert.Empty(contentNamed);

        string[] expectedContent = ["Actor", "Changes", "SourceReference"];
        Assert.Equal(
            expectedContent,
            typeof(CanonicalEventContent).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_canonical_event_exists_only_through_validation_and_is_immutable()
    {
        Assert.Empty(typeof(CanonicalEvent).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(EventActor).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        Type[] parts =
        [
            typeof(CanonicalEvent),
            typeof(CanonicalEventMetadata),
            typeof(CanonicalEventContent),
            typeof(EventSource),
            typeof(ObjectReference),
            typeof(EventActor),
            typeof(FieldChange),
        ];
        foreach (var part in parts)
        {
            var publicSetters = part
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.SetMethod?.IsPublic == true)
                .Select(property => $"{part.Name}.{property.Name}");
            Assert.Empty(publicSetters);
        }
    }

    [Fact]
    public void Validation_errors_are_fixed_values_that_cannot_carry_event_content()
    {
        foreach (var error in CanonicalEventErrors.All)
        {
            Assert.StartsWith("event.", error.Code, StringComparison.Ordinal);
            Assert.DoesNotContain("{", error.Message, StringComparison.Ordinal);
        }

        // Each error is one static value: reading it twice yields the same instance, so nothing can be
        // formatted into it per event.
        Assert.Same(CanonicalEventErrors.InvalidObject, CanonicalEventErrors.InvalidObject);
        Assert.Same(CanonicalEventErrors.All[0], CanonicalEventErrors.MissingEventId);
    }
}
