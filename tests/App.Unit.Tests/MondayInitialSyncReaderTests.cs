using System.Net;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Events;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Reconciliation;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Gate 3F's monday initial-sync reader against a deterministic fake of monday's API: it validates only the configured
/// board, by id, state, type, hierarchy and the ids and types of its columns; it pages the board's active items with only
/// their ids, states, boards and the typed status values of the board's status columns; it binds every item and column
/// exactly as the 3E reread does, with evidence naming the initial-sync read; it hands back monday's cursor only as an
/// opaque continuation; and it maps every refusal and call failure onto an explicit scope outcome.
/// </summary>
public sealed class MondayInitialSyncReaderTests
{
    private const string Board = "9001";
    private const string Documented = "\"2026-03-21T08:36:10+00:00\"";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task It_validates_the_configured_board_and_finds_each_status_column_by_id_and_type()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Boards(SyncBoard(columns: [Col("status_1", "status"), Col("name", "name"), Col("status", "status"), Col("text0", "text")])));

        var validation = await Reader(harness).ValidateScopeAsync(Scope(), Cancel);

        Assert.Equal(SyncScopeOutcome.None, validation.Refusal);
        Assert.Equal(MondayHarness.Identity, validation.Identity);
        Assert.Equal("item", validation.ObjectType);
        Assert.Equal(["status", "status_1"], validation.Fields.Select(field => field.Field));
        Assert.All(validation.Fields, field => Assert.Equal(FactDataType.TaskStatus, field.DataType));

        // One read-only request for exactly the configured board, with the credential acquired for it and the pinned version.
        var request = Assert.Single(harness.Server.Requests);
        Assert.Equal(1, harness.Credentials.Calls);
        Assert.Equal(MondayApiVersion.Pinned, request.Headers[MondayApiVersion.HeaderName]);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(MondayQueries.ReadSyncBoard, body.RootElement.GetProperty("query").GetString());
        Assert.Equal(Board, body.RootElement.GetProperty("variables").GetProperty("boardId").GetString());
    }

    [Fact]
    public void The_board_query_reads_ids_state_type_hierarchy_and_column_ids_and_types_only()
    {
        var query = MondayQueries.ReadSyncBoard;
        Assert.True(MondayQueryGuard.IsSingleReadQuery(query));
        Assert.Contains("boards(ids: [$boardId], state: all) { id state type hierarchy_type columns { id type } }", query, StringComparison.Ordinal);

        // Never a name, a title, a description, a workspace, an owner or an item: a board is identified by its id alone.
        foreach (var never in new[] { "name", "title", "description", "workspace", "owner", "items", "settings", "text", "value" })
        {
            Assert.DoesNotContain(never, query, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string, string, SyncScopeOutcome> BoardRefusals => new()
    {
        { "board absent or not visible", Boards(), SyncScopeOutcome.ScopeMissing },
        { "another board answered", Boards(SyncBoard(id: "9002")), SyncScopeOutcome.BindingMismatch },
        { "board archived", Boards(SyncBoard(state: "archived")), SyncScopeOutcome.ScopeInactive },
        { "board deleted", Boards(SyncBoard(state: "deleted")), SyncScopeOutcome.ScopeInactive },
        { "a subitems board", Boards(SyncBoard(type: "\"sub_items_board\"")), SyncScopeOutcome.ScopeUnsupported },
        { "a document", Boards(SyncBoard(type: "\"document\"")), SyncScopeOutcome.ScopeUnsupported },
        { "a custom object", Boards(SyncBoard(type: "\"custom_object\"")), SyncScopeOutcome.ScopeUnsupported },
        { "no type", Boards(SyncBoard(type: "null")), SyncScopeOutcome.ScopeUnsupported },
        { "a multi-level board", Boards(SyncBoard(hierarchy: "\"multi_level\"")), SyncScopeOutcome.ScopeUnsupported },
        { "no hierarchy", Boards(SyncBoard(hierarchy: "null")), SyncScopeOutcome.ScopeUnsupported },
        { "an unknown hierarchy", Boards(SyncBoard(hierarchy: "\"nested\"")), SyncScopeOutcome.ScopeUnsupported },
        { "no status column", Boards(SyncBoard(columns: [Col("name", "name"), Col("text0", "text")])), SyncScopeOutcome.NoSupportedField },
        { "a column titled status that is text", Boards(SyncBoard(columns: [Col("status", "text")])), SyncScopeOutcome.NoSupportedField },
        { "a mirrored status", Boards(SyncBoard(columns: [Col("mirror0", "mirror")])), SyncScopeOutcome.NoSupportedField },
        { "a status column whose id is not a field token", Boards(SyncBoard(columns: [Col("Status One", "status")])), SyncScopeOutcome.ScopeUnsupported },
    };

    [Theory]
    [MemberData(nameof(BoardRefusals))]
    public async Task A_board_that_is_not_the_active_classic_board_asked_for_with_a_status_column_is_refused_explicitly(
        string description, string response, SyncScopeOutcome expected)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, response);

        var validation = await Reader(harness).ValidateScopeAsync(Scope(), Cancel);

        Assert.True(expected == validation.Refusal, description + ": " + validation.Refusal);
        Assert.Empty(validation.Fields);
        Assert.Null(validation.ObjectType);
    }

    [Fact]
    public async Task More_status_columns_than_one_read_may_address_are_refused_never_cut()
    {
        var columns = Enumerable.Range(1, MondayOperations.MaxColumnsPerRead + 1).Select(index => Col("status_" + index, "status")).ToArray();
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, Boards(SyncBoard(columns: columns)));

        Assert.Equal(SyncScopeOutcome.ScopeUnsupported, (await Reader(harness).ValidateScopeAsync(Scope(), Cancel)).Refusal);
    }

    public static TheoryData<string, string> UnsupportedBoardShapes => new()
    {
        { "two boards", Boards(SyncBoard(), SyncBoard()) },
        { "a repeated column", Boards(SyncBoard(columns: [Col("status", "status"), Col("status", "status")])) },
        { "an unknown state", Boards(SyncBoard(state: "all")) },
        { "columns not a list", "{\"data\":{\"boards\":[{\"id\":\"9001\",\"state\":\"active\",\"type\":\"board\",\"hierarchy_type\":\"classic\",\"columns\":null}]}}" },
        { "a board that is not an object", "{\"data\":{\"boards\":[\"9001\"]}}" },
        { "no boards list", "{\"data\":{\"boards\":null}}" },
    };

    [Theory]
    [MemberData(nameof(UnsupportedBoardShapes))]
    public async Task A_board_answer_of_an_unexpected_shape_is_an_unsupported_response(string description, string response)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, response);

        var validation = await Reader(harness).ValidateScopeAsync(Scope(), Cancel);

        Assert.True(SyncScopeOutcome.UnsupportedResponse == validation.Refusal, description + ": " + validation.Refusal);
    }

    [Fact]
    public async Task A_configured_resource_that_cannot_be_a_monday_board_id_is_refused_before_anything_is_sent()
    {
        var harness = new MondayHarness();

        var validation = await Reader(harness).ValidateScopeAsync(Scope(resource: "board-9001"), Cancel);

        Assert.Equal(SyncScopeOutcome.ScopeUnsupported, validation.Refusal);
        Assert.Empty(harness.Server.Requests);
        Assert.Equal(0, harness.Credentials.Calls);
    }

    public static TheoryData<string, HttpStatusCode, string, SyncScopeOutcome> CallFailures => new()
    {
        { "server failure, retried to the limit", HttpStatusCode.InternalServerError, MondayJson.Error("Internal Server Error", statusCode: 500), SyncScopeOutcome.ProviderUnavailable },
        { "resource not found", HttpStatusCode.OK, MondayJson.Error("ResourceNotFoundException"), SyncScopeOutcome.ScopeMissing },
        { "credential rejected and not renewed", HttpStatusCode.Unauthorized, MondayJson.Error("Unauthorized", statusCode: 401), SyncScopeOutcome.ConnectionUnavailable },
        { "user not authorized", HttpStatusCode.Forbidden, MondayJson.Error("UserUnauthorizedException", statusCode: 403), SyncScopeOutcome.ConnectionUnavailable },
        { "missing permissions", HttpStatusCode.OK, MondayJson.Error("missingRequiredPermissions", statusCode: 403), SyncScopeOutcome.ConnectionUnavailable },
        { "request rejected", HttpStatusCode.OK, MondayJson.Error("InvalidArgumentException", statusCode: 400), SyncScopeOutcome.UnsupportedResponse },
        { "not json", HttpStatusCode.OK, "<html>maintenance</html>", SyncScopeOutcome.UnsupportedResponse },
    };

    [Theory]
    [MemberData(nameof(CallFailures))]
    public async Task A_failed_board_read_is_classified(string description, HttpStatusCode status, string body, SyncScopeOutcome expected)
    {
        var harness = new MondayHarness();
        harness.Server.Always(status, body);

        var validation = await Reader(harness).ValidateScopeAsync(Scope(), Cancel);

        Assert.True(expected == validation.Refusal, description + ": " + validation.Refusal);
    }

    [Fact]
    public async Task A_rate_limit_longer_than_the_client_waits_leaves_the_providers_wait_for_the_retry()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, MondayJson.Error("ComplexityException", extra: ",\"retry_in_seconds\":120"));

        var validation = await Reader(harness).ValidateScopeAsync(Scope(), Cancel);

        Assert.Equal(SyncScopeOutcome.RateLimited, validation.Refusal);
        Assert.Equal(TimeSpan.FromSeconds(120), validation.RetryAfter);
        Assert.Single(harness.Server.Requests);
    }

    [Fact]
    public async Task A_refused_credential_sends_nothing_and_fails_closed()
    {
        foreach (var (refusal, expected) in new[]
                 {
                     (MondayCredentialResult.ConnectionDisabled(), SyncScopeOutcome.ConnectionUnavailable),
                     (MondayCredentialResult.Revoked(), SyncScopeOutcome.ConnectionUnavailable),
                     (MondayCredentialResult.ReauthorizationRequired(), SyncScopeOutcome.ConnectionUnavailable),
                     (MondayCredentialResult.NotRenewed(), SyncScopeOutcome.ConnectionUnavailable),
                     (MondayCredentialResult.Unavailable(), SyncScopeOutcome.CredentialUnavailable),
                     (MondayCredentialResult.Mismatch(), SyncScopeOutcome.CredentialUnavailable),
                 })
        {
            var harness = new MondayHarness();
            harness.Credentials.Fallback = _ => refusal;

            Assert.Equal(expected, (await Reader(harness).ValidateScopeAsync(Scope(), Cancel)).Refusal);
            Assert.Equal(expected, (await Reader(harness).ReadPageAsync(Page(), Cancel)).Failure);
            Assert.Empty(harness.Server.Requests);
        }
    }

    [Fact]
    public async Task Only_the_connections_delegated_identity_may_validate_or_read_a_scope()
    {
        var harness = new MondayHarness();
        harness.Credentials.Fallback = _ => MondayCredentialResult.Acquired(MondayHarness.Credential(
            new ConnectorIdentity(MondayHarness.Tenant, MondayHarness.Connection, ConnectorIdentityKind.ServiceIdentity, "58201774")));
        harness.Server
            .Then(HttpStatusCode.OK, Boards(SyncBoard()))
            .Then(HttpStatusCode.OK, FirstPage("null", Item(columns: Column())));

        Assert.Equal(SyncScopeOutcome.IdentityMismatch, (await Reader(harness).ValidateScopeAsync(Scope(), Cancel)).Refusal);
        var page = await Reader(harness).ReadPageAsync(Page(), Cancel);
        Assert.Equal(SyncScopeOutcome.IdentityMismatch, page.Failure);
        Assert.Empty(page.Objects);
    }

    [Fact]
    public async Task The_first_page_reads_the_boards_active_items_with_their_typed_status_values_only()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, FirstPage("\"cursor-abc\"", Item(columns: [Column(label: "\"Done\""), Column(id: "status_1", label: "\"Stuck\"")]), Item(id: "2222", columns: [Column(label: "null", index: "null"), Column(id: "status_1")])));

        var page = await Reader(harness).ReadPageAsync(Page(fields: ["status", "status_1"]), Cancel);

        Assert.Equal(SyncScopeOutcome.None, page.Failure);
        Assert.Equal(MondayHarness.Identity, page.Identity);
        Assert.Equal(["1111", "2222"], page.Objects.Select(item => item.WorkObject.SourceObjectId));
        Assert.All(page.Objects, item => Assert.Equal(new ObjectReference(MondayWebhooks.SourceSystem, MondayHarness.Connection.ToString("D"), "item", item.WorkObject.SourceObjectId), item.WorkObject));
        Assert.Equal("Done", Observed(page, "1111", "status").Value);
        Assert.Equal("Stuck", Observed(page, "1111", "status_1").Value);
        Assert.Null(Observed(page, "2222", "status").Value);
        Assert.Equal(new DateTimeOffset(2026, 3, 21, 8, 36, 10, TimeSpan.Zero), Observed(page, "2222", "status").SourceUpdatedAt);
        Assert.Equal("cursor-abc", page.Next!.Token);

        // One read-only request: the configured board's id, 500 items, and only the status columns, as variables.
        using var body = JsonDocument.Parse(Assert.Single(harness.Server.Requests).Body);
        Assert.Equal(MondayQueries.ReadSyncStatusPage, body.RootElement.GetProperty("query").GetString());
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal(Board, variables.GetProperty("boardId").GetString());
        Assert.Equal(MondayOperations.MaxPageSize, variables.GetProperty("limit").GetInt32());
        Assert.Equal(["status", "status_1"], variables.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
    }

    [Fact]
    public async Task The_next_page_follows_the_continuation_and_the_last_page_has_none()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, NextPage("null", Item(id: "3333", columns: Column(label: "\"Done\""))));

        var page = await Reader(harness).ReadPageAsync(Page(continuation: "cursor-abc"), Cancel);

        Assert.Equal("Done", Observed(page, "3333", "status").Value);
        Assert.Null(page.Next);
        using var body = JsonDocument.Parse(Assert.Single(harness.Server.Requests).Body);
        Assert.Equal(MondayQueries.ReadNextSyncStatusPage, body.RootElement.GetProperty("query").GetString());
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal("cursor-abc", variables.GetProperty("cursor").GetString());
        Assert.Equal(MondayOperations.MaxPageSize, variables.GetProperty("limit").GetInt32());
        Assert.Equal(["status"], variables.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
    }

    [Fact]
    public void The_page_queries_read_ids_state_board_and_typed_status_fields_never_names_text_or_the_items_own_time()
    {
        foreach (var query in new[] { MondayQueries.ReadSyncStatusPage, MondayQueries.ReadNextSyncStatusPage })
        {
            Assert.True(MondayQueryGuard.IsSingleReadQuery(query));
            Assert.Contains("items { id state board { id } column_values(ids: $columns) { id type ... on StatusValue { label index updated_at } } }", query, StringComparison.Ordinal);
            Assert.Contains("limit: $limit)", query, StringComparison.Ordinal);
            var itemLevel = query[..query.IndexOf("column_values", StringComparison.Ordinal)];
            Assert.DoesNotContain("updated_at", itemLevel, StringComparison.Ordinal);
            foreach (var never in new[] { " name", " text", " value", " group", " title", " updates", " creator", " email", " subitems", "capabilities", "query_params" })
            {
                Assert.DoesNotContain(never, query, StringComparison.Ordinal);
            }
        }

        Assert.Contains("boards(ids: [$boardId]) { id items_page(limit: $limit) { cursor items {", MondayQueries.ReadSyncStatusPage, StringComparison.Ordinal);
        Assert.Contains("next_items_page(cursor: $cursor, limit: $limit) { cursor items {", MondayQueries.ReadNextSyncStatusPage, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, ReconciliationOutcome> ItemRefusals => new()
    {
        { "item archived", Item(state: "archived", columns: Column()), ReconciliationOutcome.ObjectInactive },
        { "item deleted", Item(state: "deleted", columns: Column()), ReconciliationOutcome.ObjectInactive },
        { "item on another board", Item(board: "{\"id\":\"9002\"}", columns: Column()), ReconciliationOutcome.BindingMismatch },
        { "item with no board", Item(board: "null", columns: Column()), ReconciliationOutcome.BindingMismatch },
        { "column absent", Item(), ReconciliationOutcome.FieldMissing },
        { "column of another type", Item(columns: "{\"id\":\"status\",\"type\":\"text\"}"), ReconciliationOutcome.FieldUnsupported },
        { "status whose fragment did not match, as a battery", Item(columns: "{\"id\":\"status\",\"type\":\"status\"}"), ReconciliationOutcome.FieldUnsupported },
        { "label without its index", Item(columns: Column(index: "null")), ReconciliationOutcome.FieldUnsupported },
        { "a label that is not a canonical value", Item(columns: Column(label: "\"Done\\nDone\"")), ReconciliationOutcome.ValueInvalid },
    };

    [Theory]
    [MemberData(nameof(ItemRefusals))]
    public async Task An_item_or_column_that_does_not_bind_is_refused_explicitly_and_misbinds_no_other(
        string description, string item, ReconciliationOutcome expected)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, FirstPage("null", item, Item(id: "2222", columns: Column(label: "\"Done\""))));

        var page = await Reader(harness).ReadPageAsync(Page(), Cancel);

        Assert.Equal(SyncScopeOutcome.None, page.Failure);
        var reading = Reading(page, "1111", "status");
        Assert.True(reading.State is null && expected == reading.Refusal, description + ": " + reading.Refusal);
        Assert.Equal("Done", Observed(page, "2222", "status").Value);
    }

    [Fact]
    public async Task A_unicode_label_and_one_at_the_bound_are_read_whole_and_one_beyond_it_is_refused_never_cut()
    {
        var longest = new string('L', EventText.MaxValueLength);
        var harness = new MondayHarness();
        harness.Server.Then(
            HttpStatusCode.OK,
            FirstPage(
                "null",
                Item(columns: Column(label: "\"Erledigt \\u2713 \\u05d1\\u05d5\\u05e6\\u05e2\"")),
                Item(id: "2222", columns: Column(label: "\"" + longest + "\"")),
                Item(id: "3333", columns: Column(label: "\"" + longest + "L\""))));

        var page = await Reader(harness).ReadPageAsync(Page(), Cancel);

        Assert.Equal("Erledigt ✓ בוצע", Observed(page, "1111", "status").Value);
        Assert.Equal(longest, Observed(page, "2222", "status").Value);
        Assert.Equal(ReconciliationOutcome.ValueInvalid, Reading(page, "3333", "status").Refusal);
    }

    [Fact]
    public async Task The_evidence_names_the_initial_sync_read_and_holds_exactly_the_provider_values_consumed()
    {
        const string label = "\"Done \\u00e9 SENTINEL\"";
        var harness = new MondayHarness();
        harness.Server
            .Then(
                HttpStatusCode.OK,
                "{\"data\":{\"boards\":[{\"id\":\"9001\",\"name\":\"BOARD-SENTINEL\",\"items_page\":{\"cursor\":\"c1\",\"items\":[{\"id\":\"1111\",\"name\":\"NAME-SENTINEL\",\"state\":\"active\",\"board\":{\"id\":\"9001\"},"
                + "\"column_values\":[{\"id\":\"status\",\"type\":\"status\",\"text\":\"TEXT-SENTINEL\",\"label\":" + label + ",\"index\":7,\"updated_at\":" + Documented + "}]}]}}]}}")
            .Then(HttpStatusCode.OK, NextPage("null", Item(columns: Column(label: label))));

        var first = Observed(await Reader(harness).ReadPageAsync(Page(), Cancel), "1111", "status").Evidence;
        var next = Observed(await Reader(harness).ReadPageAsync(Page(continuation: "c1"), Cancel), "1111", "status").Evidence;

        // The 3E evidence schema, unchanged; its operation names the initial-sync read that produced it.
        Assert.Equal(MondayStatusReader.EvidenceSchemaVersion, first.SchemaVersion);
        Assert.Equal(
            "{\"operation\":\"" + MondayOperations.ReadSyncStatusPage.Name + "\",\"api_version\":\"" + MondayApiVersion.Pinned + "\","
            + "\"item\":{\"id\":\"1111\",\"state\":\"active\",\"board\":{\"id\":\"9001\"},"
            + "\"column_values\":[{\"id\":\"status\",\"type\":\"status\",\"label\":" + label + ",\"index\":7,\"updated_at\":" + Documented + "}]}}",
            first.Document);
        Assert.Contains("\"operation\":\"" + MondayOperations.ReadNextSyncStatusPage.Name + "\"", next.Document, StringComparison.Ordinal);
        Assert.NotEqual(MondayOperations.ReadItemStatusesById.Name, MondayOperations.ReadSyncStatusPage.Name);
        foreach (var unrelated in new[] { "NAME-SENTINEL", "BOARD-SENTINEL", "TEXT-SENTINEL", "c1" })
        {
            Assert.DoesNotContain(unrelated, first.Document, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string, string, SyncScopeOutcome> FirstPageFailures => new()
    {
        { "board no longer returned", "{\"data\":{\"boards\":[]}}", SyncScopeOutcome.ScopeMissing },
        { "another board answered", BoardPage("9002", "null", Item(columns: Column())), SyncScopeOutcome.BindingMismatch },
        { "a repeated item", FirstPage("null", Item(columns: Column()), Item(columns: Column())), SyncScopeOutcome.UnsupportedResponse },
        { "an unrequested column", FirstPage("null", Item(columns: Column(id: "status_9"))), SyncScopeOutcome.UnsupportedResponse },
        { "a cursor with a space", FirstPage("\"cursor abc\"", Item(columns: Column())), SyncScopeOutcome.UnsupportedResponse },
        { "a cursor beyond its bound", FirstPage("\"" + new string('c', ScopeContinuation.MaxLength + 1) + "\"", Item(columns: Column())), SyncScopeOutcome.UnsupportedResponse },
        { "a cursor that is not a string", FirstPage("42", Item(columns: Column())), SyncScopeOutcome.UnsupportedResponse },
        { "two boards", "{\"data\":{\"boards\":[{\"id\":\"9001\",\"items_page\":{\"cursor\":null,\"items\":[]}},{\"id\":\"9001\",\"items_page\":{\"cursor\":null,\"items\":[]}}]}}", SyncScopeOutcome.UnsupportedResponse },
        { "no items page", "{\"data\":{\"boards\":[{\"id\":\"9001\"}]}}", SyncScopeOutcome.UnsupportedResponse },
    };

    [Theory]
    [MemberData(nameof(FirstPageFailures))]
    public async Task A_first_page_that_does_not_bind_as_a_whole_applies_nothing(string description, string response, SyncScopeOutcome expected)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, response);

        var page = await Reader(harness).ReadPageAsync(Page(), Cancel);

        Assert.True(expected == page.Failure, description + ": " + page.Failure);
        Assert.Empty(page.Objects);
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task More_items_than_the_page_size_bind_nothing()
    {
        var items = Enumerable.Range(1, MondayOperations.MaxPageSize + 1).Select(index => Item(id: (100000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture), columns: Column())).ToArray();
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, FirstPage("null", items));

        Assert.Equal(SyncScopeOutcome.UnsupportedResponse, (await Reader(harness).ReadPageAsync(Page(), Cancel)).Failure);
    }

    public static TheoryData<string, HttpStatusCode, string, SyncScopeOutcome> ContinuationFailures => new()
    {
        { "continuation refused by monday", HttpStatusCode.OK, MondayJson.Error("InvalidArgumentException", statusCode: 400), SyncScopeOutcome.ContinuationRejected },
        { "continuation not found", HttpStatusCode.OK, MondayJson.Error("ResourceNotFoundException"), SyncScopeOutcome.ContinuationRejected },
        { "no next page object, as an expired cursor may answer", HttpStatusCode.OK, "{\"data\":{\"next_items_page\":null}}", SyncScopeOutcome.ContinuationRejected },
        { "a next page of another shape", HttpStatusCode.OK, "{\"data\":{\"next_items_page\":{\"items\":[]}}}", SyncScopeOutcome.ContinuationRejected },
        { "server failure is not a rejected continuation", HttpStatusCode.InternalServerError, MondayJson.Error("Internal Server Error", statusCode: 500), SyncScopeOutcome.ProviderUnavailable },
        { "a credential rejection is not a rejected continuation", HttpStatusCode.Unauthorized, MondayJson.Error("Unauthorized", statusCode: 401), SyncScopeOutcome.ConnectionUnavailable },
    };

    [Theory]
    [MemberData(nameof(ContinuationFailures))]
    public async Task A_continuation_monday_refuses_is_reported_so_the_board_is_read_again_from_its_first_page(
        string description, HttpStatusCode status, string body, SyncScopeOutcome expected)
    {
        var harness = new MondayHarness();
        harness.Server.Always(status, body);

        var page = await Reader(harness).ReadPageAsync(Page(continuation: "cursor-abc"), Cancel);

        Assert.True(expected == page.Failure, description + ": " + page.Failure);
        Assert.Empty(page.Objects);
    }

    [Fact]
    public async Task More_fields_than_one_read_may_address_are_never_sent()
    {
        var harness = new MondayHarness();
        var fields = Enumerable.Range(1, MondayOperations.MaxColumnsPerRead + 1).Select(index => "status_" + index).ToArray();

        var page = await Reader(harness).ReadPageAsync(Page(fields: fields), Cancel);

        Assert.Equal(SyncScopeOutcome.ScopeUnsupported, page.Failure);
        Assert.Empty(harness.Server.Requests);
    }

    [Fact]
    public async Task The_reader_reads_only_monday_item_status_scopes()
    {
        var harness = new MondayHarness();
        var reader = Reader(harness);
        var other = new ScopeReadRequest(MondayHarness.Tenant, MondayHarness.Connection, MondayHarness.Connection.ToString("D"), "synthetic", Board, CorrelationId.New());

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ValidateScopeAsync(other, Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadPageAsync(new ScopePageRequest(other, "item", [new SyncField("status", FactDataType.TaskStatus)], null), Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadPageAsync(new ScopePageRequest(Scope(), "board", [new SyncField("status", FactDataType.TaskStatus)], null), Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadPageAsync(new ScopePageRequest(Scope(), "item", [new SyncField("due", FactDataType.Deadline)], null), Cancel));
        Assert.Empty(harness.Server.Requests);
        Assert.Equal(MondayWebhooks.SourceSystem, reader.SourceSystem);
    }

    [Fact]
    public void A_continuation_is_never_rendered()
    {
        var continuation = new ScopeContinuation("cursor-SENTINEL");

        Assert.DoesNotContain("SENTINEL", continuation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", new ScopePageRequest(Scope(), "item", [new SyncField("status", FactDataType.TaskStatus)], continuation).ToString(), StringComparison.Ordinal);
        Assert.False(ScopeContinuation.IsValid("cursor abc"));
        Assert.False(ScopeContinuation.IsValid(new string('c', ScopeContinuation.MaxLength + 1)));
        Assert.True(ScopeContinuation.IsValid(new string('c', ScopeContinuation.MaxLength)));
    }

    private static MondayInitialSyncReader Reader(MondayHarness harness) => new(harness.Client);

    private static ScopeReadRequest Scope(string resource = Board) =>
        new(MondayHarness.Tenant, MondayHarness.Connection, MondayHarness.Connection.ToString("D"), MondayWebhooks.SourceSystem, resource, CorrelationId.New());

    private static ScopePageRequest Page(string[]? fields = null, string? continuation = null) =>
        new(
            Scope(),
            "item",
            (fields ?? ["status"]).Select(field => new SyncField(field, FactDataType.TaskStatus)).ToArray(),
            continuation is null ? null : new ScopeContinuation(continuation));

    private static TargetReading Reading(ScopePage page, string item, string field)
    {
        var synced = Assert.Single(page.Objects, candidate => candidate.WorkObject.SourceObjectId == item);
        return Assert.Single(synced.Fields, pair => pair.Key.Field == field).Value;
    }

    private static AuthoritativeFieldState Observed(ScopePage page, string item, string field)
    {
        var reading = Reading(page, item, field);
        Assert.True(reading.State is not null, "refused: " + reading.Refusal);
        return reading.State!;
    }

    private static string Col(string id, string type) => "{\"id\":\"" + id + "\",\"type\":\"" + type + "\"}";

    private static string SyncBoard(
        string id = Board,
        string state = "active",
        string type = "\"board\"",
        string hierarchy = "\"classic\"",
        string[]? columns = null) =>
        "{\"id\":\"" + id + "\",\"state\":\"" + state + "\",\"type\":" + type + ",\"hierarchy_type\":" + hierarchy
        + ",\"columns\":[" + string.Join(",", columns ?? [Col("name", "name"), Col("status", "status")]) + "]}";

    private static string Boards(params string[] boards) => "{\"data\":{\"boards\":[" + string.Join(",", boards) + "]},\"account_id\":1}";

    private static string FirstPage(string cursor, params string[] items) => BoardPage(Board, cursor, items);

    private static string BoardPage(string board, string cursor, params string[] items) =>
        "{\"data\":{\"boards\":[{\"id\":\"" + board + "\",\"items_page\":{\"cursor\":" + cursor + ",\"items\":[" + string.Join(",", items) + "]}}]}}";

    private static string NextPage(string cursor, params string[] items) =>
        "{\"data\":{\"next_items_page\":{\"cursor\":" + cursor + ",\"items\":[" + string.Join(",", items) + "]}}}";

    private static string Column(
        string id = "status",
        string type = "status",
        string label = "\"Working on it\"",
        string index = "1",
        string updatedAt = Documented) =>
        "{\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"label\":" + label + ",\"index\":" + index + ",\"updated_at\":" + updatedAt + "}";

    private static string Item(string id = "1111", string state = "active", string board = "{\"id\":\"9001\"}", params string[] columns) =>
        "{\"id\":\"" + id + "\",\"state\":\"" + state + "\",\"board\":" + board + ",\"column_values\":[" + string.Join(",", columns) + "]}";
}
