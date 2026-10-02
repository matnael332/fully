using System.Net;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.Reconciliation;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Gate 3E's monday reader against a deterministic fake of monday's API: it rereads through the 3A client with the
/// connection's credential, binds every answer to the requested item, board, column and status type, reads the typed
/// label and the status value's own update time by exact formats, copies exactly the provider values it consumed as
/// evidence, refuses everything else explicitly, and maps every call failure onto a reconciliation outcome.
/// </summary>
public sealed class MondayStatusReaderTests
{
    private const string Board = "9001";
    private const string Documented = "\"2026-03-21T08:36:10+00:00\"";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task It_rereads_the_typed_status_of_the_requested_item_with_the_values_own_update_time()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "\"Done\""))));

        var result = await Reader(harness).ReadAsync(Request(Target()), Cancel);

        var state = Observed(result, Target());
        Assert.Equal("Done", state.Value);
        Assert.Equal(new DateTimeOffset(2026, 3, 21, 8, 36, 10, TimeSpan.Zero), state.SourceUpdatedAt);
        Assert.Equal(MondayHarness.Identity, result.Identity);
        Assert.Equal(ReconciliationOutcome.None, result.Failure);

        // One read-only request, with the credential acquired for it, the pinned version, and ids only as variables.
        var request = Assert.Single(harness.Server.Requests);
        Assert.Equal(1, harness.Credentials.Calls);
        Assert.Equal(MondayApiVersion.Pinned, request.Headers[MondayApiVersion.HeaderName]);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(MondayQueries.ReadItemStatusesById, body.RootElement.GetProperty("query").GetString());
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal(["1111"], variables.GetProperty("ids").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(1, variables.GetProperty("limit").GetInt32());
        Assert.Equal(["status"], variables.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
    }

    [Fact]
    public void The_query_reads_only_ids_state_board_and_the_typed_status_fields_never_names_text_or_the_items_own_time()
    {
        var query = MondayQueries.ReadItemStatusesById;
        Assert.True(MondayQueryGuard.IsSingleReadQuery(query));
        Assert.Contains("items(ids: $ids, limit: $limit)", query, StringComparison.Ordinal);
        Assert.Contains("column_values(ids: $columns) { id type ... on StatusValue { label index updated_at } }", query, StringComparison.Ordinal);
        Assert.DoesNotContain("exclude_nonactive", query, StringComparison.Ordinal);

        // The item's own updated_at may reflect any field, so it is not even read; neither is any display text.
        var itemLevel = query[..query.IndexOf("column_values", StringComparison.Ordinal)];
        Assert.DoesNotContain("updated_at", itemLevel, StringComparison.Ordinal);
        foreach (var never in new[] { "name", "text", "value", "group", "title", "column {", "capabilities" })
        {
            Assert.DoesNotContain(" " + never, query, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_empty_status_is_a_cleared_value_with_its_own_update_time()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "null", index: "null"))));

        var state = Observed(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target());

        Assert.Null(state.Value);
        Assert.NotNull(state.SourceUpdatedAt);
    }

    [Fact]
    public async Task A_label_at_the_bound_is_read_whole_and_one_beyond_it_is_refused_never_cut()
    {
        var longest = new string('L', EventText.MaxValueLength);
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "\"" + longest + "\""))))
            .Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "\"" + longest + "L\""))));

        Assert.Equal(longest, Observed(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target()).Value);
        Assert.Equal(ReconciliationOutcome.ValueInvalid, Refusal(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target()));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\" Done\"")]
    [InlineData("\"Done\\nDone\"")]
    [InlineData("\"Done\\u200b\"")]
    public async Task A_label_that_is_not_a_canonical_value_is_refused(string label)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Items(Item(columns: Column(label: label))));

        Assert.Equal(ReconciliationOutcome.ValueInvalid, Refusal(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target()));
    }

    public static TheoryData<string, string, ReconciliationOutcome> Refusals => new()
    {
        { "item absent", Items(), ReconciliationOutcome.ObjectMissing },
        { "item archived", Items(Item(state: "archived", columns: Column())), ReconciliationOutcome.ObjectInactive },
        { "item deleted", Items(Item(state: "deleted", columns: Column())), ReconciliationOutcome.ObjectInactive },
        { "item on another board", Items(Item(board: "{\"id\":\"9002\"}", columns: Column())), ReconciliationOutcome.BindingMismatch },
        { "item with no board", Items(Item(board: "null", columns: Column())), ReconciliationOutcome.BindingMismatch },
        { "column absent, as a rollup monday omits", Items(Item()), ReconciliationOutcome.FieldMissing },
        { "column of another type", Items(Item(columns: "{\"id\":\"status\",\"type\":\"text\"}")), ReconciliationOutcome.FieldUnsupported },
        { "status whose fragment did not match, as a battery", Items(Item(columns: "{\"id\":\"status\",\"type\":\"status\"}")), ReconciliationOutcome.FieldUnsupported },
        { "status column typed color", Items(Item(columns: Column(type: "color"))), ReconciliationOutcome.FieldUnsupported },
        { "label without its index", Items(Item(columns: Column(index: "null"))), ReconciliationOutcome.FieldUnsupported },
        { "index without a label", Items(Item(columns: Column(label: "null"))), ReconciliationOutcome.FieldUnsupported },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Anything_but_the_requested_active_item_on_its_board_with_a_status_value_is_refused_never_remapped(
        string description, string response, ReconciliationOutcome expected)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, response);

        var result = await Reader(harness).ReadAsync(Request(Target()), Cancel);

        Assert.True(expected == Refusal(result, Target()), description);
    }

    [Fact]
    public async Task Each_field_of_one_item_binds_to_its_own_column_whatever_order_monday_answers_in()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Items(Item(columns: [Column(id: "status_1", label: "\"Second\""), Column(id: "status", label: "\"First\"")])));
        var first = Target(field: "status");
        var second = Target(field: "status_1");

        var result = await Reader(harness).ReadAsync(Request(first, second), Cancel);

        Assert.Equal("First", Observed(result, first).Value);
        Assert.Equal("Second", Observed(result, second).Value);
        using var body = JsonDocument.Parse(Assert.Single(harness.Server.Requests).Body);
        Assert.Equal(["1111"], body.RootElement.GetProperty("variables").GetProperty("ids").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(["status", "status_1"], body.RootElement.GetProperty("variables").GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
    }

    [Theory]
    [InlineData("\"2026-03-21T08:41:10+00:00\"", true)]
    [InlineData("\"2026-03-21T10:41:10+02:00\"", true)]
    [InlineData("\"2026-03-21T08:40:10Z\"", true)]
    [InlineData("\"2026-03-21T07:36:10+00:00\"", true)]
    [InlineData("\"2026-03-21T08:41:11+00:00\"", false)]
    [InlineData("\"2026-03-21T08:51:10+00:00\"", false)]
    public async Task A_status_update_time_meets_the_unchanged_five_minute_rule_exactly_at_its_bound(string updatedAt, bool plausible)
    {
        // A receipt at 08:36:10 and Gate 2's tolerance of five minutes, as configured by default and unchanged by 3E: a
        // monday time up to exactly 08:41:10 is plausible, one second later is impossible, and an older one always is.
        var received = new DateTimeOffset(2026, 3, 21, 8, 36, 10, TimeSpan.Zero);
        var tolerance = TimeSpan.FromMinutes(5);
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "\"Done\"", updatedAt: updatedAt))));

        var read = Observed(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target()).SourceUpdatedAt;
        var verdict = Core.Processing.OrderingPolicy.ImpossibilityOf(new Core.Processing.OrderingEvidence(null, read, null, null), received, tolerance);

        Assert.Equal(plausible, verdict is null);
        if (!plausible)
        {
            Assert.Equal(Core.Processing.ProcessingErrors.SourceTimeAfterReceipt, verdict);
        }
    }

    [Fact]
    public async Task A_status_older_than_the_accepted_one_is_stale_and_one_at_the_same_time_is_never_called_newer()
    {
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "\"Older\"", updatedAt: "\"2026-03-21T08:30:00+00:00\""))))
            .Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "\"Renamed\"", updatedAt: "\"2026-03-21T08:36:10+00:00\""))));
        var accepted = new Core.Processing.OrderingEvidence(null, new DateTimeOffset(2026, 3, 21, 8, 36, 10, TimeSpan.Zero), null, null);

        var older = Observed(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target()).SourceUpdatedAt;
        var same = Observed(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target()).SourceUpdatedAt;

        // The Gate 2 ordering engine, unchanged: an older status is stale; the same time is ambiguous, never forced.
        Assert.Equal(Core.Processing.FieldOrderingDecision.Stale, Core.Processing.OrderingPolicy.Decide(new(null, older, null, null), accepted));
        Assert.Equal(Core.Processing.FieldOrderingDecision.Ambiguous, Core.Processing.OrderingPolicy.Decide(new(null, same, null, null), accepted));
    }

    [Fact]
    public async Task The_same_column_id_on_another_board_is_not_the_same_field()
    {
        // The item moved, or another board's "status" answered: the column id alone never binds.
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Items(Item(board: "{\"id\":\"7777\"}", columns: Column(label: "\"Done\""))));

        Assert.Equal(ReconciliationOutcome.BindingMismatch, Refusal(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target()));
    }

    public static TheoryData<string, string> UnsupportedShapes => new()
    {
        { "an unrequested item", Items(Item(id: "2222", columns: Column())) },
        { "a repeated item", Items(Item(columns: Column()), Item(columns: Column())) },
        { "an unrequested column", Items(Item(columns: Column(id: "status_9"))) },
        { "a repeated column", Items(Item(columns: [Column(), Column()])) },
        { "a fragment with only some fields", Items(Item(columns: "{\"id\":\"status\",\"type\":\"status\",\"label\":\"Done\"}")) },
        { "an index that is not an integer", Items(Item(columns: Column(index: "1.5"))) },
        { "an unknown state", Items(Item(state: "all", columns: Column())) },
        { "a board that is not an object", Items(Item(board: "\"9001\"", columns: Column())) },
        { "no items list", "{\"data\":{\"items\":null}}" },
    };

    [Theory]
    [MemberData(nameof(UnsupportedShapes))]
    public async Task A_response_of_an_unexpected_shape_binds_nothing_at_all(string description, string response)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, response);

        var result = await Reader(harness).ReadAsync(Request(Target()), Cancel);

        Assert.True(ReconciliationOutcome.UnsupportedResponse == Refusal(result, Target()), description);
    }

    [Fact]
    public async Task An_item_repeated_in_place_of_another_requested_one_binds_nothing()
    {
        // Within the count bound: two items asked for, one returned twice. No copy can be trusted over the other.
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "\"Done\"")), Item(columns: Column(label: "\"Stuck\""))));

        var result = await Reader(harness).ReadAsync(Request(Target(), Target(item: "2222")), Cancel);

        Assert.Equal(ReconciliationOutcome.UnsupportedResponse, result.Failure);
        Assert.Equal(ReconciliationOutcome.UnsupportedResponse, Refusal(result, Target()));
        Assert.Equal(ReconciliationOutcome.UnsupportedResponse, Refusal(result, Target(item: "2222")));
    }

    [Theory]
    [InlineData("\"2026-03-21T08:36:10+00:00\"", "2026-03-21T08:36:10.0000000+00:00")]
    [InlineData("\"2026-03-21T08:36:10Z\"", "2026-03-21T08:36:10.0000000+00:00")]
    [InlineData("\"2026-03-21T10:36:10+02:00\"", "2026-03-21T08:36:10.0000000+00:00")]
    [InlineData("\"2026-03-21T08:36:10.123+00:00\"", "2026-03-21T08:36:10.1230000+00:00")]
    [InlineData("\"2026-03-21T08:36:10.1234567Z\"", "2026-03-21T08:36:10.1234567+00:00")]
    public async Task The_status_values_update_time_is_read_by_exact_formats_into_utc(string updatedAt, string expected)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Items(Item(columns: Column(updatedAt: updatedAt))));

        var state = Observed(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target());

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), state.SourceUpdatedAt);
        Assert.Equal(TimeSpan.Zero, state.SourceUpdatedAt!.Value.Offset);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"2026-03-21T08:36:10\"")]
    [InlineData("\"2026-03-21 08:36:10+00:00\"")]
    [InlineData("\"2026-03-21\"")]
    [InlineData("\"21/03/2026 08:36:10\"")]
    [InlineData("\"2026-03-21T08:36:10.12345678+00:00\"")]
    [InlineData("\"yesterday\"")]
    public async Task A_missing_or_unreadable_update_time_is_reported_as_none_never_fabricated(string updatedAt)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "\"Done\"", updatedAt: updatedAt))));

        var state = Observed(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target());

        // The value is still the source's state, but with no time it cannot be ordered: processing holds it (D3E-3).
        Assert.Equal("Done", state.Value);
        Assert.Null(state.SourceUpdatedAt);
    }

    [Fact]
    public async Task The_evidence_is_exactly_the_provider_values_consumed_copied_byte_for_byte()
    {
        const string label = "\"Done \\u00e9 \\\"quoted\\\" SENTINEL\"";
        var harness = new MondayHarness();
        harness.Server.Then(
            HttpStatusCode.OK,
            Items("{\"id\":\"1111\",\"name\":\"NAME-SENTINEL\",\"state\":\"active\",\"board\":{\"id\":\"9001\",\"name\":\"BOARD-SENTINEL\"},"
                + "\"column_values\":[{\"id\":\"status\",\"type\":\"status\",\"text\":\"TEXT-SENTINEL\",\"label\":" + label
                + ",\"index\":7,\"updated_at\":" + Documented + ",\"is_done\":true}]}"));

        var evidence = Observed(await Reader(harness).ReadAsync(Request(Target()), Cancel), Target()).Evidence;

        Assert.Equal(MondayStatusReader.EvidenceSchemaVersion, evidence.SchemaVersion);
        Assert.Equal("monday.api-read.v1", evidence.SchemaVersion);
        Assert.Equal(
            "{\"operation\":\"monday.items.read-status-by-id\",\"api_version\":\"" + MondayApiVersion.Pinned + "\","
            + "\"item\":{\"id\":\"1111\",\"state\":\"active\",\"board\":{\"id\":\"9001\"},"
            + "\"column_values\":[{\"id\":\"status\",\"type\":\"status\",\"label\":" + label + ",\"index\":7,\"updated_at\":" + Documented + "}]}}",
            evidence.Document);
        foreach (var unrelated in new[] { "NAME-SENTINEL", "BOARD-SENTINEL", "TEXT-SENTINEL", "is_done" })
        {
            Assert.DoesNotContain(unrelated, evidence.Document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_target_the_reader_cannot_address_is_refused_before_anything_is_sent()
    {
        var harness = new MondayHarness();
        var reader = Reader(harness);
        ReconciliationTarget[] targets =
        [
            Target(scope: null),
            new(Object(type: "board"), "status", FactDataType.TaskStatus, Board),
            new(Object(), "due", FactDataType.Deadline, Board),
            new(Object(id: "item-1111"), "status", FactDataType.TaskStatus, Board),
        ];

        var result = await reader.ReadAsync(Request(targets), Cancel);

        Assert.Empty(harness.Server.Requests);
        Assert.Equal(0, harness.Credentials.Calls);
        Assert.Null(result.Identity);
        Assert.Equal(
            [ReconciliationOutcome.ScopeMissing, ReconciliationOutcome.FieldUnsupported, ReconciliationOutcome.FieldUnsupported, ReconciliationOutcome.FieldUnsupported],
            targets.Select(target => Refusal(result, target)));
    }

    [Fact]
    public async Task Targets_of_one_read_are_bound_independently_and_one_bad_object_misbinds_no_other()
    {
        var harness = new MondayHarness();
        harness.Server.Then(
            HttpStatusCode.OK,
            Items(Item(columns: Column(label: "\"Done\"")), Item(id: "2222", board: "{\"id\":\"9002\"}", columns: Column(label: "\"Stuck\""))));
        var good = Target();
        var moved = Target(item: "2222");
        var unscoped = Target(item: "3333", scope: null);

        var result = await Reader(harness).ReadAsync(Request(good, moved, unscoped), Cancel);

        Assert.Equal("Done", Observed(result, good).Value);
        Assert.Equal(ReconciliationOutcome.BindingMismatch, Refusal(result, moved));
        Assert.Equal(ReconciliationOutcome.ScopeMissing, Refusal(result, unscoped));
        using var body = JsonDocument.Parse(Assert.Single(harness.Server.Requests).Body);
        Assert.Equal(["1111", "2222"], body.RootElement.GetProperty("variables").GetProperty("ids").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(2, body.RootElement.GetProperty("variables").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task More_objects_than_one_read_may_address_are_never_sent()
    {
        var harness = new MondayHarness();
        var targets = Enumerable.Range(1, MondayOperations.MaxItemsPerRead + 1).Select(index => Target(item: (1000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var result = await Reader(harness).ReadAsync(Request(targets), Cancel);

        Assert.Equal(ReconciliationOutcome.UnsupportedResponse, result.Failure);
        Assert.Empty(harness.Server.Requests);
    }

    public static TheoryData<string, HttpStatusCode, string, ReconciliationOutcome> CallFailures => new()
    {
        { "server failure, retried to the limit", HttpStatusCode.InternalServerError, MondayJson.Error("Internal Server Error", statusCode: 500), ReconciliationOutcome.ProviderUnavailable },
        { "resource not found", HttpStatusCode.OK, MondayJson.Error("ResourceNotFoundException"), ReconciliationOutcome.ObjectMissing },
        { "credential rejected and not renewed", HttpStatusCode.Unauthorized, MondayJson.Error("Unauthorized", statusCode: 401), ReconciliationOutcome.ConnectionUnavailable },
        { "user not authorized", HttpStatusCode.Forbidden, MondayJson.Error("UserUnauthorizedException", statusCode: 403), ReconciliationOutcome.ConnectionUnavailable },
        { "missing permissions", HttpStatusCode.OK, MondayJson.Error("missingRequiredPermissions", statusCode: 403), ReconciliationOutcome.ConnectionUnavailable },
        { "request rejected", HttpStatusCode.OK, MondayJson.Error("InvalidArgumentException", statusCode: 400), ReconciliationOutcome.UnsupportedResponse },
        { "not json", HttpStatusCode.OK, "<html>maintenance</html>", ReconciliationOutcome.UnsupportedResponse },
    };

    [Theory]
    [MemberData(nameof(CallFailures))]
    public async Task A_failed_call_fails_every_target_with_its_outcome(string description, HttpStatusCode status, string body, ReconciliationOutcome expected)
    {
        var harness = new MondayHarness();
        harness.Server.Always(status, body);

        var result = await Reader(harness).ReadAsync(Request(Target(), Target(item: "2222")), Cancel);

        Assert.True(expected == result.Failure, description);
        Assert.Equal(expected, Refusal(result, Target()));
        Assert.Equal(expected, Refusal(result, Target(item: "2222")));
    }

    [Fact]
    public async Task A_rate_limit_longer_than_the_client_waits_leaves_the_providers_wait_for_the_retry()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, MondayJson.Error("ComplexityException", extra: ",\"retry_in_seconds\":120"));

        var result = await Reader(harness).ReadAsync(Request(Target()), Cancel);

        Assert.Equal(ReconciliationOutcome.RateLimited, result.Failure);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Empty(harness.Delay.Waits);
        Assert.Single(harness.Server.Requests);
    }

    [Fact]
    public async Task A_refused_credential_sends_nothing_and_fails_closed()
    {
        // A connection that cannot be used waits for access; a credential that cannot be obtained just now is transient.
        foreach (var (refusal, expected) in new[]
                 {
                     (MondayCredentialResult.ConnectionDisabled(), ReconciliationOutcome.ConnectionUnavailable),
                     (MondayCredentialResult.Revoked(), ReconciliationOutcome.ConnectionUnavailable),
                     (MondayCredentialResult.ReauthorizationRequired(), ReconciliationOutcome.ConnectionUnavailable),
                     (MondayCredentialResult.NotRenewed(), ReconciliationOutcome.ConnectionUnavailable),
                     (MondayCredentialResult.Unavailable(), ReconciliationOutcome.CredentialUnavailable),
                     (MondayCredentialResult.Mismatch(), ReconciliationOutcome.CredentialUnavailable),
                 })
        {
            var harness = new MondayHarness();
            harness.Credentials.Fallback = _ => refusal;

            var result = await Reader(harness).ReadAsync(Request(Target()), Cancel);

            Assert.Equal(expected, result.Failure);
            Assert.Empty(harness.Server.Requests);
        }
    }

    [Fact]
    public async Task Only_the_connections_delegated_identity_may_confirm_anything()
    {
        var harness = new MondayHarness();
        harness.Credentials.Fallback = _ => MondayCredentialResult.Acquired(MondayHarness.Credential(
            new ConnectorIdentity(MondayHarness.Tenant, MondayHarness.Connection, ConnectorIdentityKind.ServiceIdentity, "58201774")));
        harness.Server.Then(HttpStatusCode.OK, Items(Item(columns: Column(label: "\"Done\""))));

        var result = await Reader(harness).ReadAsync(Request(Target()), Cancel);

        Assert.Equal(ReconciliationOutcome.IdentityMismatch, result.Failure);
        Assert.Equal(ReconciliationOutcome.IdentityMismatch, Refusal(result, Target()));
    }

    [Fact]
    public async Task The_reader_reads_only_monday_targets()
    {
        var harness = new MondayHarness();
        var request = new AuthoritativeReadRequest(
            MondayHarness.Tenant,
            MondayHarness.Connection,
            CorrelationId.New(),
            [new ReconciliationTarget(new ObjectReference("synthetic", MondayHarness.Connection.ToString("D"), "item", "1111"), "status", FactDataType.TaskStatus, Board)]);

        await Assert.ThrowsAsync<ArgumentException>(() => Reader(harness).ReadAsync(request, Cancel));
        Assert.Equal((MondayWebhooks.SourceSystem, MondayOperations.MaxItemsPerRead), (Reader(harness).SourceSystem, Reader(harness).MaxObjectsPerRead));
    }

    private static MondayStatusReader Reader(MondayHarness harness) => new(harness.Client);

    private static AuthoritativeFieldState Observed(AuthoritativeReadResult result, ReconciliationTarget target)
    {
        var reading = result.ReadingFor(target);
        Assert.True(reading.State is not null, "refused: " + reading.Refusal);
        return reading.State!;
    }

    private static ReconciliationOutcome Refusal(AuthoritativeReadResult result, ReconciliationTarget target)
    {
        var reading = result.ReadingFor(target);
        Assert.Null(reading.State);
        return reading.Refusal;
    }

    private static AuthoritativeReadRequest Request(params ReconciliationTarget[] targets) =>
        new(MondayHarness.Tenant, MondayHarness.Connection, CorrelationId.New(), targets);

    private static ObjectReference Object(string type = "item", string id = "1111") =>
        new(MondayWebhooks.SourceSystem, MondayHarness.Connection.ToString("D"), type, id);

    private static ReconciliationTarget Target(string item = "1111", string? scope = Board, string field = "status") =>
        new(Object(id: item), field, FactDataType.TaskStatus, scope);

    private static string Column(
        string id = "status",
        string type = "status",
        string label = "\"Working on it\"",
        string index = "1",
        string updatedAt = Documented) =>
        "{\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"label\":" + label + ",\"index\":" + index + ",\"updated_at\":" + updatedAt + "}";

    private static string Item(string id = "1111", string state = "active", string board = "{\"id\":\"9001\"}", params string[] columns) =>
        "{\"id\":\"" + id + "\",\"state\":\"" + state + "\",\"board\":" + board + ",\"column_values\":[" + string.Join(",", columns) + "]}";

    private static string Items(params string[] items) => MondayJson.Items(items);
}
