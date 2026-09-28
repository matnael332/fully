using WorkContinuity.Core.Events;
using WorkContinuity.Core.Identity;
using WorkContinuity.Core.SourceOfTruth;

namespace WorkContinuity.Unit.Tests;

public sealed class AuthorityTableTests
{
    [Fact]
    public void The_table_is_exactly_the_prd_section_5_authority_list()
    {
        var expected = new Dictionary<FactDataType, AuthorityBasis>
        {
            [FactDataType.TaskStatus] = AuthorityBasis.SourceTaskSystem,
            [FactDataType.PcActivity] = AuthorityBasis.LocalAgent,
            [FactDataType.ApprovalDecision] = AuthorityBasis.Employee,
            [FactDataType.IdentityAndPermissions] = AuthorityBasis.OrganizationIdentitySystem,
            [FactDataType.Deadline] = AuthorityBasis.DefiningSystem,
            [FactDataType.ActionResult] = AuthorityBasis.AuthoritativeDestination,
        };

        Assert.Equal(expected.OrderBy(rule => rule.Key), AuthorityTable.Rules.OrderBy(rule => rule.Key));
    }

    [Fact]
    public void Every_defined_data_type_has_a_rule_and_a_type_without_one_has_no_authority()
    {
        foreach (var dataType in Enum.GetValues<FactDataType>())
        {
            var basis = AuthorityTable.BasisFor(dataType);
            if (dataType == FactDataType.None)
            {
                Assert.Equal(AuthorityBasis.None, basis);
            }
            else
            {
                Assert.NotEqual(AuthorityBasis.None, basis);
            }
        }

        Assert.Equal(AuthorityBasis.None, AuthorityTable.BasisFor((FactDataType)99));
    }
}

public sealed class SourceOfTruthResolverTests
{
    private static readonly ObjectReference OwnedTask = EventBuilder.Task();

    [Theory]
    [InlineData(FactDataType.TaskStatus)]
    [InlineData(FactDataType.Deadline)]
    public void A_saas_owned_field_is_authoritative_only_from_the_connector_that_owns_the_object(FactDataType dataType)
    {
        Assert.Equal(AuthorityDecision.Authoritative, Resolve(dataType, From(EventSourceKind.SaasConnector)));
        Assert.Equal(
            AuthorityDecision.NotAuthoritative,
            Resolve(dataType, From(EventSourceKind.SaasConnector, connection: "connection-2")));
        Assert.Equal(
            AuthorityDecision.NotAuthoritative,
            Resolve(dataType, From(EventSourceKind.SaasConnector, system: "other-system")));

        EventSourceKind[] others =
        [
            EventSourceKind.LocalAgent,
            EventSourceKind.Employee,
            EventSourceKind.IdentityProvider,
            EventSourceKind.ActionExecutor,
        ];
        foreach (var kind in others)
        {
            Assert.Equal(AuthorityDecision.NotAuthoritative, Resolve(dataType, From(kind)));
        }
    }

    [Theory]
    [InlineData(FactDataType.PcActivity, EventSourceKind.LocalAgent)]
    [InlineData(FactDataType.ApprovalDecision, EventSourceKind.Employee)]
    [InlineData(FactDataType.IdentityAndPermissions, EventSourceKind.IdentityProvider)]
    [InlineData(FactDataType.ActionResult, EventSourceKind.ActionExecutor)]
    public void Any_other_data_type_is_authoritative_only_from_its_own_kind_of_source(
        FactDataType dataType,
        EventSourceKind authority)
    {
        foreach (var kind in Enum.GetValues<EventSourceKind>().Where(kind => kind != EventSourceKind.None))
        {
            var expected = kind == authority ? AuthorityDecision.Authoritative : AuthorityDecision.NotAuthoritative;
            Assert.Equal(expected, Resolve(dataType, From(kind)));
        }
    }

    [Fact]
    public void A_data_type_without_a_rule_is_never_authoritative()
    {
        foreach (var kind in Enum.GetValues<EventSourceKind>())
        {
            Assert.Equal(AuthorityDecision.NotAuthoritative, Resolve(FactDataType.None, From(kind)));
            Assert.Equal(AuthorityDecision.NotAuthoritative, Resolve((FactDataType)99, From(kind)));
        }
    }

    [Fact]
    public void An_undefined_or_malformed_source_or_object_is_never_authoritative()
    {
        foreach (var dataType in Enum.GetValues<FactDataType>())
        {
            Assert.Equal(AuthorityDecision.NotAuthoritative, Resolve(dataType, From(EventSourceKind.None)));
            Assert.Equal(AuthorityDecision.NotAuthoritative, Resolve(dataType, From((EventSourceKind)42)));
        }

        // Source and object agree with each other, but the shared connection is malformed.
        const string malformedConnection = "connection‮1";
        Assert.Equal(
            AuthorityDecision.NotAuthoritative,
            SourceOfTruthResolver.Resolve(
                FactDataType.TaskStatus,
                new EventSource(EventSourceKind.SaasConnector, "synthetic", malformedConnection),
                new ObjectReference("synthetic", malformedConnection, "task", "task-42")));

        // Source is the home connector, but the object reference is malformed.
        Assert.Equal(
            AuthorityDecision.NotAuthoritative,
            SourceOfTruthResolver.Resolve(
                FactDataType.TaskStatus,
                EventBuilder.HomeConnector(),
                new ObjectReference("synthetic", "connection-1", "Task", "task-42")));
    }

    [Fact]
    public void Resolving_an_event_decides_each_change_in_order()
    {
        var created = new EventBuilder
        {
            Source = new EventSource(EventSourceKind.LocalAgent, "windows-agent", "device-1"),
            Changes =
            [
                new FieldChange("activity", FactDataType.PcActivity, null, "editing"),
                new FieldChange("status", FactDataType.TaskStatus, "working", "done"),
            ],
        }.Valid();

        AuthorityDecision[] expected = [AuthorityDecision.Authoritative, AuthorityDecision.NotAuthoritative];
        Assert.Equal(expected, SourceOfTruthResolver.ResolveChanges(created));
    }

    [Fact]
    public void The_actor_never_changes_authority()
    {
        EventSource[] sources =
        [
            EventBuilder.HomeConnector(),
            new EventSource(EventSourceKind.SaasConnector, "synthetic", "connection-2"),
            new EventSource(EventSourceKind.LocalAgent, "windows-agent", "device-1"),
        ];

        foreach (var source in sources)
        {
            EventActor?[] actors =
            [
                null,
                EventActor.Unresolved("user-7"),
                EventActor.Resolved("user-7", UserId.New()),
            ];

            var decisions = actors
                .Select(actor => SourceOfTruthResolver.ResolveChanges(
                    new EventBuilder { Source = source, Actor = actor }.Valid()))
                .ToArray();

            Assert.All(decisions, decision => Assert.Equal(decisions[0], decision));
        }
    }

    private static EventSource From(
        EventSourceKind kind,
        string system = "synthetic",
        string connection = "connection-1") => new(kind, system, connection);

    private static AuthorityDecision Resolve(FactDataType dataType, EventSource source) =>
        SourceOfTruthResolver.Resolve(dataType, source, OwnedTask);
}
