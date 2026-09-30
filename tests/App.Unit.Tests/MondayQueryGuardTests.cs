using WorkContinuity.Connectors.Monday;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The Gate 3A read-only guard: a document is sent only when it holds exactly one operation definition and that
/// definition is a query. Comments and strings cannot hide or fake an operation.
/// </summary>
public sealed class MondayQueryGuardTests
{
    [Fact]
    public void Every_document_the_connector_sends_is_a_single_read_query()
    {
        Assert.Equal(4, MondayQueries.All.Count);
        Assert.All(MondayQueries.All, document => Assert.True(MondayQueryGuard.IsSingleReadQuery(document), document));
    }

    [Theory]
    [InlineData("query Q { a }")]
    [InlineData("query { a }")]
    [InlineData("  query Q($ids: [ID!]!, $limit: Int!) { items(ids: $ids, limit: $limit) { id } }  ")]
    [InlineData("# a comment\nquery Q { a }")]
    [InlineData("query Q($s: String = \"mutation M { b }\") { a }")]
    [InlineData("query Q { a(text: \"} mutation X { y }\") }")]
    [InlineData("query Q { a(text: \"\"\"} subscription S { y }\"\"\") }")]
    [InlineData("query Q { mutation subscription }")]
    public void Single_read_queries_are_accepted(string document)
    {
        Assert.True(MondayQueryGuard.IsSingleReadQuery(document));
    }

    [Theory]
    [InlineData("mutation { create_item(item_name: \"x\") { id } }")]
    [InlineData("mutation M { archive_item(item_id: 1) { id } }")]
    [InlineData("subscription S { a }")]
    [InlineData("query A { a } mutation B { b }")]
    [InlineData("query A { a } query B { b }")]
    [InlineData("query A { a } { b }")]
    [InlineData("query { a } { b }")]
    [InlineData("query A { a } # trailing\n mutation B { b }")]
    [InlineData("{ a }")]
    [InlineData("fragment F on Item { id } query Q { items { ...F } }")]
    [InlineData("query Q @include(if: true) { a }")]
    [InlineData("query Q { a")]
    [InlineData("query Q { a } }")]
    [InlineData("query Q($s: String = \"unterminated) { a }")]
    [InlineData("query Q")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_one_read_query_is_refused(string? document)
    {
        Assert.False(MondayQueryGuard.IsSingleReadQuery(document));
    }

    [Fact]
    public void An_oversized_document_is_refused()
    {
        var document = "query Q { " + new string('a', MondayQueryGuard.MaxDocumentLength) + " }";

        Assert.False(MondayQueryGuard.IsSingleReadQuery(document));
    }
}
