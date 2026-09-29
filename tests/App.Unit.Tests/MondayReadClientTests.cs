using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The Gate 3A monday read client against a deterministic fake monday server: what it sends (one fixed endpoint, the
/// pinned API version, the credential, a single read query with provider values as variables), what it reads (every
/// documented item field exactly as received), and what it refuses (invalid requests before sending, and any response
/// shape it does not expect).
/// </summary>
public sealed class MondayReadClientTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_request_is_a_post_to_the_fixed_endpoint_with_the_pinned_version_and_the_credential()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(harness.Server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://api.monday.com/v2"), request.Uri);
        Assert.Equal(MondayEndpoints.Api, request.Uri);
        Assert.Equal("2026-07", request.Headers["API-Version"]);
        Assert.Equal(MondayApiVersion.Pinned, request.Headers[MondayApiVersion.HeaderName]);
        Assert.Equal(MondayHarness.Token, request.Headers["Authorization"]);
        Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Equal("application/json", request.Headers["Accept"]);
    }

    [Fact]
    public async Task The_pinned_version_and_a_fresh_credential_accompany_every_attempt_including_retries()
    {
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.ServiceUnavailable, string.Empty)
            .Then(HttpStatusCode.BadGateway, string.Empty)
            .Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, harness.Server.Requests.Count);
        Assert.Equal(3, harness.Credentials.Calls);
        Assert.All(harness.Server.Requests, request =>
        {
            Assert.Equal(MondayApiVersion.Pinned, request.Headers[MondayApiVersion.HeaderName]);
            Assert.Equal(MondayHarness.Token, request.Headers["Authorization"]);
        });
    }

    [Fact]
    public async Task Ids_travel_as_variables_and_the_limit_equals_the_number_of_ids()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, MondayJson.Items(
            MondayJson.Item("1111"), MondayJson.Item("2222"), MondayJson.Item("3333")));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111", "2222", "3333"], Cancel);

        Assert.True(result.IsSuccess);
        using var body = JsonDocument.Parse(Assert.Single(harness.Server.Requests).Body);
        Assert.Equal(MondayQueries.ReadItemsById, body.RootElement.GetProperty("query").GetString());
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal(new[] { "1111", "2222", "3333" }, variables.GetProperty("ids").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(3, variables.GetProperty("limit").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("operationName", out _));
        Assert.DoesNotContain("1111", body.RootElement.GetProperty("query").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_board_page_and_a_next_page_send_their_variables()
    {
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.OK, MondayJson.BoardPage("\"cursor-1\"", MondayJson.Item()))
            .Then(HttpStatusCode.OK, MondayJson.NextPage("null", MondayJson.Item("2222")));

        var first = await harness.Client.ReadBoardItemsPageAsync(MondayHarness.Context(), "9001", 200, Cancel);
        var next = await harness.Client.ReadNextItemsPageAsync(MondayHarness.Context(), first.Value.Cursor!, 200, Cancel);

        Assert.True(first.IsSuccess);
        Assert.True(next.IsSuccess);
        using var firstBody = JsonDocument.Parse(harness.Server.Requests[0].Body);
        using var nextBody = JsonDocument.Parse(harness.Server.Requests[1].Body);
        Assert.Equal(MondayQueries.ReadBoardItemsPage, firstBody.RootElement.GetProperty("query").GetString());
        Assert.Equal("9001", firstBody.RootElement.GetProperty("variables").GetProperty("boardId").GetString());
        Assert.Equal(200, firstBody.RootElement.GetProperty("variables").GetProperty("limit").GetInt32());
        Assert.Equal(MondayQueries.ReadNextItemsPage, nextBody.RootElement.GetProperty("query").GetString());
        Assert.Equal("cursor-1", nextBody.RootElement.GetProperty("variables").GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task Items_are_read_with_every_documented_field_exactly_as_received()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        var item = Assert.Single(result.Value.Items);
        Assert.Equal("1111", item.Id);
        Assert.Equal("Quarterly plan", item.Name);
        Assert.Equal(MondayItemState.Active, item.State);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero), item.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 17, 45, 12, TimeSpan.Zero), item.UpdatedAt);
        Assert.Equal("9001", item.BoardId);
        Assert.Equal("topics", item.GroupId);
        Assert.Equal(2, item.ColumnValues.Count);
        Assert.Equal("status", item.ColumnValues[0].ColumnId);
        Assert.Equal("status", item.ColumnValues[0].ColumnType);
        Assert.Equal("Working on it", item.ColumnValues[0].Text);
        Assert.Equal("\"{\\\"index\\\":0}\"", item.ColumnValues[0].RawValue);
        Assert.Null(item.ColumnValues[1].Text);
        Assert.Null(item.ColumnValues[1].RawValue);
        Assert.Equal("MondayItem(1111)", item.ToString());
    }

    [Fact]
    public async Task Archived_and_deleted_items_are_reported_with_their_state_and_a_missing_id_is_absent_not_invented()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, MondayJson.Items(
            MondayJson.Item("1111", state: "archived"), MondayJson.Item("2222", state: "deleted", updatedAt: "null")));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111", "2222", "3333"], Cancel);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { MondayItemState.Archived, MondayItemState.Deleted }, result.Value.Items.Select(item => item.State));
        Assert.Null(result.Value.Items[1].UpdatedAt);
        Assert.DoesNotContain(result.Value.Items, item => item.Id == "3333");
    }

    [Fact]
    public async Task A_page_reports_its_cursor_and_the_last_page_has_none()
    {
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.OK, MondayJson.BoardPage("\"MSw5MDAxLDEw\"", MondayJson.Item()))
            .Then(HttpStatusCode.OK, MondayJson.BoardPage("null"));

        var first = await harness.Client.ReadBoardItemsPageAsync(MondayHarness.Context(), "9001", 500, Cancel);
        var last = await harness.Client.ReadBoardItemsPageAsync(MondayHarness.Context(), "9001", 500, Cancel);

        Assert.True(first.Value.HasMore);
        Assert.Equal("MSw5MDAxLDEw", first.Value.Cursor);
        Assert.DoesNotContain("MSw5MDAxLDEw", first.Value.ToString(), StringComparison.Ordinal);
        Assert.False(last.Value.HasMore);
        Assert.Empty(last.Value.Items);
    }

    [Fact]
    public async Task A_board_that_is_absent_or_not_visible_is_not_found_and_not_retried()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, """{"data":{"boards":[]}}""");

        var result = await harness.Client.ReadBoardItemsPageAsync(MondayHarness.Context(), "9001", 10, Cancel);

        Assert.Equal(ConnectorFailureKind.ResourceNotFound, result.Failure.Kind);
        Assert.Equal(ConnectorErrors.ResourceNotFound, result.Failure.Error);
        Assert.Equal(1, result.Attempts);
    }

    public static TheoryData<string, Func<MondayReadClient, Task<ConnectorFailure>>> InvalidRequests => new()
    {
        { "no ids", client => FailureOf(client.ReadItemsByIdAsync(MondayHarness.Context(), [], Cancel)) },
        { "101 ids", client => FailureOf(client.ReadItemsByIdAsync(MondayHarness.Context(), Enumerable.Range(1, 101).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), Cancel)) },
        { "a non-numeric id", client => FailureOf(client.ReadItemsByIdAsync(MondayHarness.Context(), ["12a"], Cancel)) },
        { "an id of 21 digits", client => FailureOf(client.ReadItemsByIdAsync(MondayHarness.Context(), [new string('9', 21)], Cancel)) },
        { "duplicate ids", client => FailureOf(client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111", "1111"], Cancel)) },
        { "a non-numeric board", client => FailureOf(client.ReadBoardItemsPageAsync(MondayHarness.Context(), "board-9", 10, Cancel)) },
        { "a zero page", client => FailureOf(client.ReadBoardItemsPageAsync(MondayHarness.Context(), "9001", 0, Cancel)) },
        { "a page of 501", client => FailureOf(client.ReadBoardItemsPageAsync(MondayHarness.Context(), "9001", 501, Cancel)) },
        { "an empty cursor", client => FailureOf(client.ReadNextItemsPageAsync(MondayHarness.Context(), string.Empty, 10, Cancel)) },
        { "a cursor with a line break", client => FailureOf(client.ReadNextItemsPageAsync(MondayHarness.Context(), "abc\ndef", 10, Cancel)) },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_requests_fail_before_a_credential_is_acquired_or_anything_is_sent(
        string label,
        Func<MondayReadClient, Task<ConnectorFailure>> call)
    {
        var harness = new MondayHarness();

        var failure = await call(harness.Client);

        Assert.Equal(ConnectorFailureKind.InvalidRequest, failure.Kind);
        Assert.Equal(ConnectorErrors.InvalidRequest, failure.Error);
        Assert.Empty(harness.Server.Requests);
        Assert.Equal(0, harness.Credentials.Calls);
        Assert.NotNull(label);
    }

    [Fact]
    public async Task The_result_records_the_identity_whose_authority_was_used_and_the_attempts()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(MondayHarness.Identity, result.Identity);
        Assert.Equal(1, result.Attempts);
        Assert.Same(MondayOperations.ReadItemsById, result.Operation);
    }

    public static TheoryData<string, string> UnsupportedBodies => new()
    {
        { "no data", """{"account_id":1}""" },
        { "null data", """{"data":null}""" },
        { "items not a list", """{"data":{"items":{}}}""" },
        { "an item without an id", """{"data":{"items":[{"name":"x"}]}}""" },
        { "a numeric id", MondayJson.Items(MondayJson.Item().Replace("\"id\":\"1111\"", "\"id\":1111", StringComparison.Ordinal)) },
        { "an unknown state", MondayJson.Items(MondayJson.Item(state: "frozen")) },
        { "an unreadable time", MondayJson.Items(MondayJson.Item(updatedAt: "\"yesterday\"")) },
        { "no column values", MondayJson.Items(MondayJson.Item(columnValues: "null")) },
        { "a column without a type", MondayJson.Items(MondayJson.Item(columnValues: """[{"id":"status","text":null,"value":null}]""")) },
        { "an item nobody asked for", MondayJson.Items(MondayJson.Item("7777")) },
        { "a root that is not an object", "[1,2,3]" },
        { "not JSON at all", "<html>ok</html>" },
    };

    [Theory]
    [MemberData(nameof(UnsupportedBodies))]
    public async Task Unexpected_shapes_are_unsupported_and_never_retried(string label, string body)
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, body);

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.False(result.IsSuccess, label);
        Assert.Equal(ConnectorFailureKind.UnsupportedResponse, result.Failure.Kind);
        Assert.Equal(ConnectorErrors.UnsupportedResponse, result.Failure.Error);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task More_items_than_asked_for_and_malformed_pages_are_unsupported()
    {
        var harness = new MondayHarness();
        harness.Server
            .Then(HttpStatusCode.OK, MondayJson.BoardPage("null", MondayJson.Item("1111"), MondayJson.Item("2222")))
            .Then(HttpStatusCode.OK, MondayJson.BoardPage("42", MondayJson.Item()))
            .Then(HttpStatusCode.OK, """{"data":{"boards":[{"items_page":{"cursor":null,"items":[]}},{"items_page":{"cursor":null,"items":[]}}]}}""")
            .Then(HttpStatusCode.OK, """{"data":{"next_items_page":null}}""");

        var tooMany = await harness.Client.ReadBoardItemsPageAsync(MondayHarness.Context(), "9001", 1, Cancel);
        var numericCursor = await harness.Client.ReadBoardItemsPageAsync(MondayHarness.Context(), "9001", 10, Cancel);
        var twoBoards = await harness.Client.ReadBoardItemsPageAsync(MondayHarness.Context(), "9001", 10, Cancel);
        var nullPage = await harness.Client.ReadNextItemsPageAsync(MondayHarness.Context(), "cursor-1", 10, Cancel);

        Assert.All(
            new[] { tooMany.Failure, numericCursor.Failure, twoBoards.Failure, nullPage.Failure },
            failure => Assert.Equal(ConnectorFailureKind.UnsupportedResponse, failure.Kind));
    }

    [Fact]
    public async Task A_success_that_is_not_declared_as_json_is_unsupported()
    {
        var harness = new MondayHarness();
        harness.Server.Then(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()), response =>
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html"));

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorFailureKind.UnsupportedResponse, result.Failure.Kind);
    }

    [Fact]
    public async Task An_oversized_response_is_refused_whether_or_not_it_declares_its_length()
    {
        // Both answers are otherwise valid successes, so only the size bound can refuse them.
        var valid = MondayJson.Items(MondayJson.Item());
        var harness = new MondayHarness();
        harness.Server
            .Then(_ =>
            {
                var response = ScriptedMondayServer.Respond(HttpStatusCode.OK, valid);
                response.Content.Headers.ContentLength = MondayReadClient.MaxResponseBytes + 1L;
                return response;
            })
            .Then(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new PaddedStream(System.Text.Encoding.UTF8.GetBytes(valid), MondayReadClient.MaxResponseBytes + 1L)),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return response;
            });

        var declared = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);
        var streamed = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorFailureKind.UnsupportedResponse, declared.Failure.Kind);
        Assert.Equal(ConnectorFailureKind.UnsupportedResponse, streamed.Failure.Kind);
    }

    [Fact]
    public async Task A_redirected_answer_is_refused()
    {
        var harness = new MondayHarness();
        harness.Server.Then(_ =>
        {
            var response = ScriptedMondayServer.Respond(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://elsewhere.example/v2");
            return response;
        });

        var result = await harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorFailureKind.UnsupportedResponse, result.Failure.Kind);
    }

    [Fact]
    public async Task A_caller_cancellation_propagates_and_is_not_classified()
    {
        var harness = new MondayHarness();
        harness.Server.Always(HttpStatusCode.OK, MondayJson.Items(MondayJson.Item()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], cancelled.Token));
    }

    [Fact]
    public async Task A_timeout_and_a_network_failure_are_transient_and_retried_to_the_limit()
    {
        var timeouts = new MondayHarness();
        timeouts.Server.AlwaysThrow(() => new TaskCanceledException("timed out", new TimeoutException()));
        var unreachable = new MondayHarness();
        unreachable.Server.AlwaysThrow(() => new HttpRequestException("unreachable"));

        var timedOut = await timeouts.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);
        var failed = await unreachable.Client.ReadItemsByIdAsync(MondayHarness.Context(), ["1111"], Cancel);

        Assert.Equal(ConnectorFailureKind.NetworkTransient, timedOut.Failure.Kind);
        Assert.Equal(ConnectorErrors.Timeout, timedOut.Failure.Error);
        Assert.Equal(ConnectorRetryPolicy.DefaultRead.MaxAttempts, timedOut.Attempts);
        Assert.Equal(ConnectorFailureKind.NetworkTransient, failed.Failure.Kind);
        Assert.Equal(ConnectorErrors.NetworkFailure, failed.Failure.Error);
        Assert.Equal(ConnectorRetryPolicy.DefaultRead.MaxAttempts, failed.Attempts);
    }

    private static async Task<ConnectorFailure> FailureOf<TValue>(Task<ConnectorCallResult<TValue>> call) =>
        (await call).Failure;

    /// <summary>A valid JSON prefix followed by whitespace up to the given length, never held in memory at once.</summary>
    private sealed class PaddedStream(byte[] prefix, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = (int)Math.Min(count, length - _position);
            for (var index = 0; index < remaining; index++)
            {
                var position = _position + index;
                buffer[offset + index] = position < prefix.Length ? prefix[position] : (byte)' ';
            }

            _position += remaining;
            return remaining;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
