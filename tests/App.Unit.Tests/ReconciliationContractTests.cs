using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Tenancy;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Gate 3E's provider-neutral reconciliation contracts: targets carry identifiers only, a read never spans sources or
/// connections, an authoritative value is never cut to fit, a reader may report only reader outcomes, and the outcomes
/// form a closed set with fixed codes and dispositions. The source scope on a normalized event is a bounded, opaque
/// identifier.
/// </summary>
public sealed class ReconciliationContractTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("0192a3b4-0000-7000-8000-00000000a001"));
    private static readonly Guid Connection = Guid.Parse("0192a3b4-0000-7000-8000-00000000c001");

    [Fact]
    public void A_target_names_a_well_formed_object_a_token_field_a_defined_type_and_an_opaque_scope()
    {
        var target = Target();
        Assert.Equal(("1111", "status", FactDataType.TaskStatus, "9001"), (target.WorkObject.SourceObjectId, target.Field, target.DataType, target.SourceScope));
        Assert.Null(Target(scope: null).SourceScope);
        Assert.Equal("ReconciliationTarget(monday, item 1111, status)", target.ToString());

        Assert.Throws<ArgumentNullException>(() => new ReconciliationTarget(null!, "status", FactDataType.TaskStatus, "9001"));
        Assert.Throws<ArgumentException>(() => new ReconciliationTarget(Object(id: " 1111"), "status", FactDataType.TaskStatus, "9001"));
        Assert.Throws<ArgumentException>(() => Target(field: "Status Column"));
        Assert.Throws<ArgumentException>(() => Target(dataType: FactDataType.None));
        Assert.Throws<ArgumentException>(() => Target(dataType: (FactDataType)99));
        foreach (var scope in new[] { string.Empty, " 9001", "9001\n", new string('9', EventText.MaxIdentifierLength + 1) })
        {
            Assert.Throws<ArgumentException>(() => Target(scope: scope));
        }
    }

    [Fact]
    public void A_read_names_its_tenant_connection_and_correlation_and_never_spans_sources_connections_or_duplicates()
    {
        var request = new AuthoritativeReadRequest(Tenant, Connection, CorrelationId.New(), [Target(), Target(item: "2222")]);
        Assert.Equal(("monday", Connection.ToString("D"), 2), (request.SourceSystem, request.ConnectionReference, request.Targets.Count));

        Assert.Throws<ArgumentException>(() => new AuthoritativeReadRequest(default, Connection, CorrelationId.New(), [Target()]));
        Assert.Throws<ArgumentException>(() => new AuthoritativeReadRequest(Tenant, Guid.Empty, CorrelationId.New(), [Target()]));
        Assert.Throws<ArgumentException>(() => new AuthoritativeReadRequest(Tenant, Connection, default, [Target()]));
        Assert.Throws<ArgumentException>(() => new AuthoritativeReadRequest(Tenant, Connection, CorrelationId.New(), []));
        Assert.Throws<ArgumentException>(() => new AuthoritativeReadRequest(Tenant, Connection, CorrelationId.New(), [Target(), Target()]));
        Assert.Throws<ArgumentException>(() => new AuthoritativeReadRequest(
            Tenant, Connection, CorrelationId.New(), [Target(), new ReconciliationTarget(Object(system: "jira"), "status", FactDataType.TaskStatus, "9001")]));
        Assert.Throws<ArgumentException>(() => new AuthoritativeReadRequest(
            Tenant, Connection, CorrelationId.New(), [Target(), new ReconciliationTarget(Object(connection: Guid.NewGuid().ToString("D")), "status", FactDataType.TaskStatus, "9001")]));
    }

    [Fact]
    public void An_authoritative_value_is_a_valid_canonical_value_never_cut_and_never_rendered()
    {
        var evidence = new ObservationEvidence("monday.api-read.v1", "{}");
        var longest = new string('x', EventText.MaxValueLength);
        Assert.Equal(longest, new AuthoritativeFieldState(longest, null, evidence).Value);
        Assert.Null(new AuthoritativeFieldState(null, null, evidence).Value);

        foreach (var invalid in new[] { longest + "x", string.Empty, " Done", "Done\nDone" })
        {
            Assert.Throws<ArgumentException>(() => new AuthoritativeFieldState(invalid, null, evidence));
        }

        var state = new AuthoritativeFieldState("Done SENTINEL", DateTimeOffset.Parse("2026-03-21T10:36:10+02:00"), evidence);
        Assert.Equal(new DateTimeOffset(2026, 3, 21, 8, 36, 10, TimeSpan.Zero), state.SourceUpdatedAt);
        Assert.Equal(TimeSpan.Zero, state.SourceUpdatedAt!.Value.Offset);
        Assert.DoesNotContain("SENTINEL", state.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => new AuthoritativeFieldState("Done", null, null!));
    }

    [Fact]
    public void Evidence_has_a_token_schema_and_a_bounded_document_and_never_renders_it()
    {
        var evidence = new ObservationEvidence("monday.api-read.v1", "{\"label\":\"SENTINEL\"}");
        Assert.Equal("ObservationEvidence(monday.api-read.v1)", evidence.ToString());

        Assert.Throws<ArgumentException>(() => new ObservationEvidence("Monday API", "{}"));
        Assert.Throws<ArgumentException>(() => new ObservationEvidence("monday.api-read.v1", " "));
        Assert.Throws<ArgumentException>(() => new ObservationEvidence("monday.api-read.v1", new string('x', ObservationEvidence.MaxDocumentLength + 1)));
    }

    [Fact]
    public void A_reader_reports_only_reader_outcomes_and_an_unanswered_target_is_an_unsupported_response()
    {
        foreach (var outcome in ReconciliationOutcomes.All.Where(outcome => !ReconciliationOutcomes.IsReaderRefusal(outcome)))
        {
            Assert.Throws<ArgumentException>(() => TargetReading.Refused(outcome));
            Assert.Throws<ArgumentException>(() => AuthoritativeReadResult.Failed(outcome, identity: null));
        }

        Assert.Throws<ArgumentException>(() => TargetReading.Refused(ReconciliationOutcome.None));
        Assert.Throws<ArgumentException>(() => AuthoritativeReadResult.Failed(ReconciliationOutcome.RateLimited, null, TimeSpan.FromSeconds(-1)));

        var identity = new ConnectorIdentity(Tenant, Connection, ConnectorIdentityKind.DelegatedUser, "58201774");
        var observed = TargetReading.Observed(new AuthoritativeFieldState("Done", null, new ObservationEvidence("monday.api-read.v1", "{}")));
        var read = AuthoritativeReadResult.Read(identity, new Dictionary<ReconciliationTarget, TargetReading> { [Target()] = observed });
        Assert.Same(observed, read.ReadingFor(Target()));
        Assert.Equal(ReconciliationOutcome.UnsupportedResponse, read.ReadingFor(Target(item: "2222")).Refusal);

        var failed = AuthoritativeReadResult.Failed(ReconciliationOutcome.RateLimited, identity, TimeSpan.FromSeconds(120));
        Assert.Equal(ReconciliationOutcome.RateLimited, failed.ReadingFor(Target()).Refusal);
        Assert.Equal(TimeSpan.FromSeconds(120), failed.RetryAfter);

        // Only a call to the source observes a state.
        Assert.Throws<ArgumentException>(() => AuthoritativeReadResult.Refused(new Dictionary<ReconciliationTarget, TargetReading> { [Target()] = observed }));
        var refused = AuthoritativeReadResult.Refused(new Dictionary<ReconciliationTarget, TargetReading> { [Target()] = TargetReading.Refused(ReconciliationOutcome.ScopeMissing) });
        Assert.Null(refused.Identity);
        Assert.Equal(ReconciliationOutcome.ScopeMissing, refused.ReadingFor(Target()).Refusal);
    }

    [Fact]
    public void The_outcomes_are_a_closed_set_with_fixed_codes_and_dispositions()
    {
        // Transient failures retry for as long as they last, access failures wait for access to stand again, an unusable
        // answer retries up to a bound, and only what retrying cannot change is held (owner review of 3E).
        var expected = new Dictionary<ReconciliationOutcome, (string, ReconciliationDisposition)>
        {
            [ReconciliationOutcome.Current] = ("reconciliation.current", ReconciliationDisposition.Resolve),
            [ReconciliationOutcome.Repaired] = ("reconciliation.repaired", ReconciliationDisposition.Resolve),
            [ReconciliationOutcome.Superseded] = ("reconciliation.superseded", ReconciliationDisposition.Resolve),
            [ReconciliationOutcome.ProviderUnavailable] = ("reconciliation.provider.unavailable", ReconciliationDisposition.Retry),
            [ReconciliationOutcome.RateLimited] = ("reconciliation.provider.rate_limited", ReconciliationDisposition.Retry),
            [ReconciliationOutcome.CredentialUnavailable] = ("reconciliation.credential.unavailable", ReconciliationDisposition.Retry),
            [ReconciliationOutcome.ConnectionUnavailable] = ("reconciliation.connection.unavailable", ReconciliationDisposition.AwaitAccess),
            [ReconciliationOutcome.TenantInactive] = ("reconciliation.tenant.inactive", ReconciliationDisposition.AwaitAccess),
            [ReconciliationOutcome.ObjectMissing] = ("reconciliation.object.missing", ReconciliationDisposition.RetryThenHold),
            [ReconciliationOutcome.UnsupportedResponse] = ("reconciliation.provider.unsupported_response", ReconciliationDisposition.RetryThenHold),
            [ReconciliationOutcome.ObjectInactive] = ("reconciliation.object.inactive", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.BindingMismatch] = ("reconciliation.binding.mismatch", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.FieldMissing] = ("reconciliation.field.missing", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.FieldUnsupported] = ("reconciliation.field.unsupported", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.ValueInvalid] = ("reconciliation.value.invalid", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.OrderingAmbiguous] = ("reconciliation.ordering.ambiguous", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.OrderingUnevidenced] = ("reconciliation.ordering.no_evidence", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.OrderingImpossible] = ("reconciliation.ordering.impossible", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.Quarantined] = ("reconciliation.processing.quarantined", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.ScopeMissing] = ("reconciliation.scope.missing", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.ScopeConflict] = ("reconciliation.scope.conflict", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.IdentityMismatch] = ("reconciliation.identity.mismatch", ReconciliationDisposition.Hold),
            [ReconciliationOutcome.AuthorityChanged] = ("reconciliation.authority.changed", ReconciliationDisposition.AwaitAccess),
        };

        Assert.Equal(Enum.GetValues<ReconciliationOutcome>().Where(outcome => outcome != ReconciliationOutcome.None).Order(), ReconciliationOutcomes.All.Order());
        foreach (var (outcome, (code, disposition)) in expected)
        {
            Assert.Equal(code, ReconciliationOutcomes.CodeOf(outcome));
            Assert.Equal(disposition, ReconciliationOutcomes.DispositionOf(outcome));
            Assert.True(code.Length <= 100);
        }

        Assert.Equal(expected.Count, expected.Values.Select(entry => entry.Item1).Distinct().Count());
        Assert.Throws<ArgumentException>(() => ReconciliationOutcomes.CodeOf(ReconciliationOutcome.None));
        Assert.Throws<ArgumentException>(() => ReconciliationOutcomes.DispositionOf((ReconciliationOutcome)99));

        // A recorded code names its outcome again, and nothing else names one.
        foreach (var (outcome, (code, _)) in expected)
        {
            Assert.True(ReconciliationOutcomes.TryParse(code, out var parsed));
            Assert.Equal(outcome, parsed);
        }

        foreach (var code in new[] { null, string.Empty, "reconciliation", "Reconciliation.current", "reconciliation.current ", "processing.normalizer.rejected" })
        {
            Assert.False(ReconciliationOutcomes.TryParse(code, out var parsed));
            Assert.Equal(ReconciliationOutcome.None, parsed);
        }

        // Readers may report provider facts only, never a resolution or processing's verdicts.
        Assert.Equal(
            [
                ReconciliationOutcome.ProviderUnavailable, ReconciliationOutcome.RateLimited, ReconciliationOutcome.CredentialUnavailable,
                ReconciliationOutcome.ConnectionUnavailable, ReconciliationOutcome.ObjectMissing, ReconciliationOutcome.UnsupportedResponse,
                ReconciliationOutcome.ObjectInactive, ReconciliationOutcome.BindingMismatch, ReconciliationOutcome.FieldMissing,
                ReconciliationOutcome.FieldUnsupported, ReconciliationOutcome.ValueInvalid, ReconciliationOutcome.ScopeMissing,
                ReconciliationOutcome.IdentityMismatch,
            ],
            ReconciliationOutcomes.All.Where(ReconciliationOutcomes.IsReaderRefusal).Order());
    }

    [Fact]
    public void A_run_is_one_tenant_source_and_connection_with_a_bounded_number_of_targets()
    {
        var run = new ReconciliationRun(Tenant, "monday", Connection, 100);
        Assert.Equal((Tenant, "monday", Connection, 100), (run.Tenant, run.SourceSystem, run.ConnectionId, run.MaxTargets));

        Assert.Throws<ArgumentException>(() => new ReconciliationRun(default, "monday", Connection, 1));
        Assert.Throws<ArgumentException>(() => new ReconciliationRun(Tenant, "Monday", Connection, 1));
        Assert.Throws<ArgumentException>(() => new ReconciliationRun(Tenant, "monday", Guid.Empty, 1));
        Assert.Throws<ArgumentException>(() => new ReconciliationRun(Tenant, "monday", Connection, 0));
        Assert.Throws<ArgumentException>(() => new ReconciliationRun(Tenant, "monday", Connection, ReconciliationRun.MaxTargetsPerRun + 1));
    }

    [Fact]
    public void A_normalized_event_states_its_source_scope_as_a_bounded_opaque_identifier_or_none()
    {
        Assert.Equal("1771812698", Normalized("1771812698").SourceScope);
        Assert.Null(Normalized(null).SourceScope);
        foreach (var scope in new[] { string.Empty, " 1771812698", "1771812698\r", new string('1', EventText.MaxIdentifierLength + 1) })
        {
            Assert.Throws<ArgumentException>(() => Normalized(scope));
        }

        // The scope is the argument before the confirmation, which stays last and required.
        var parameters = typeof(NormalizedSourceEvent).GetConstructors().Single().GetParameters();
        Assert.Equal(("sourceScope", typeof(string)), (parameters[^2].Name, parameters[^2].ParameterType));
        Assert.False(parameters[^2].HasDefaultValue);
    }

    private static NormalizedSourceEvent Normalized(string? scope) =>
        new(
            CanonicalEventSchemas.ObjectChangedType,
            CanonicalEventSchemas.ObjectChangedVersion1,
            Object(),
            sourceRevision: null,
            occurredAt: null,
            sourceUpdatedAt: null,
            new CanonicalEventContent([new FieldChange("status", FactDataType.TaskStatus, null, "Done")], actor: null, sourceReference: null),
            scope,
            SourceValueConfirmation.Unconfirmed);

    private static ObjectReference Object(string system = "monday", string? connection = null, string type = "item", string id = "1111") =>
        new(system, connection ?? Connection.ToString("D"), type, id);

    private static ReconciliationTarget Target(
        string item = "1111",
        string field = "status",
        FactDataType dataType = FactDataType.TaskStatus,
        string? scope = "9001") =>
        new(Object(id: item), field, dataType, scope);
}
