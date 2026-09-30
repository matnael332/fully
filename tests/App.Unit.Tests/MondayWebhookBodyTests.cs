using System.Text;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Correlation;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// Gate 3C webhook bodies: a challenge and an event are told apart by shape alone and read strictly; the provider event
/// id is monday's <c>triggerUuid</c> only when monday supplies one; an event's submission carries the body exactly as
/// received; and nothing in the type renders content.
/// </summary>
public sealed class MondayWebhookBodyTests
{
    private const string Marker = "pulse-name-marker-51c2";

    [Fact]
    public void A_challenge_is_answered_with_exactly_the_same_value()
    {
        const string challenge = "3eZbrw1aBm2rZgRNFdxV2595E9CY3gmdALWMmHkvFXO7tYXAYM8P";

        var body = Read("""{"challenge":"CHALLENGE"}""".Replace("CHALLENGE", challenge, StringComparison.Ordinal));

        Assert.Equal(MondayWebhookBodyKind.Challenge, body.Kind);
        Assert.Null(body.SourceEventId);
        var response = body.ChallengeResponse();
        Assert.Equal("""{"challenge":"CHALLENGE"}""".Replace("CHALLENGE", challenge, StringComparison.Ordinal), Encoding.UTF8.GetString(response));
        using var parsed = JsonDocument.Parse(response);
        Assert.Equal(challenge, parsed.RootElement.GetProperty("challenge").GetString());
        Assert.Equal(["challenge"], parsed.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Every_visible_ascii_character_except_a_quote_or_backslash_is_a_valid_challenge_and_round_trips()
    {
        var value = new string(Enumerable.Range('!', '~' - '!' + 1).Select(code => (char)code).Where(character => character is not '"' and not '\\').ToArray());

        var body = Read(JsonSerializer.Serialize(new { challenge = value }));

        Assert.Equal(MondayWebhookBodyKind.Challenge, body.Kind);
        using var parsed = JsonDocument.Parse(body.ChallengeResponse());
        Assert.Equal(value, parsed.RootElement.GetProperty("challenge").GetString());
    }

    [Theory]
    [InlineData("""{"challenge":"abc","tenantId":"0191f3a0-7a44-7c1b-9d2e-3f5a6b7c8d9e"}""")]
    [InlineData("""{"challenge":"abc","event":{"type":"create_item"}}""")]
    [InlineData("""{"challenge":"abc","challenge":"abc"}""")]
    [InlineData("""{"challenge":""}""")]
    [InlineData("""{"challenge":null}""")]
    [InlineData("""{"challenge":42}""")]
    [InlineData("""{"challenge":["abc"]}""")]
    [InlineData("""{"challenge":{"value":"abc"}}""")]
    [InlineData("""{"challenge":"has space"}""")]
    [InlineData("""{"challenge":"quote\"inside"}""")]
    [InlineData("""{"challenge":"back\\slash"}""")]
    [InlineData("""{"challenge":"tab\there"}""")]
    [InlineData("""{"challenge":"café"}""")]
    [InlineData("""{"challenge":"<script>\u0000"}""")]
    [InlineData("""{"Challenge":"abc"}""")]
    public void A_malformed_challenge_or_one_with_any_other_field_is_refused(string json)
    {
        var body = Read(json);

        Assert.Equal(MondayWebhookBodyKind.Malformed, body.Kind);
        Assert.Throws<InvalidOperationException>(() => body.ChallengeResponse());
    }

    [Fact]
    public void A_challenge_is_bounded_at_256_characters()
    {
        Assert.Equal(MondayWebhookBodyKind.Challenge, Read("{\"challenge\":\"" + new string('a', 256) + "\"}").Kind);
        Assert.Equal(MondayWebhookBodyKind.Malformed, Read("{\"challenge\":\"" + new string('a', 257) + "\"}").Kind);
        Assert.Equal(256, MondayWebhookBody.MaxChallengeLength);
    }

    [Fact]
    public void An_event_with_a_trigger_uuid_carries_it_as_the_provider_event_id()
    {
        var body = Read(EventJson("\"b7e1c9a2f0d34e5a8c6b1f2e3d4c5b6a\""));

        Assert.Equal(MondayWebhookBodyKind.Event, body.Kind);
        Assert.Equal("b7e1c9a2f0d34e5a8c6b1f2e3d4c5b6a", body.SourceEventId);
        Assert.Throws<InvalidOperationException>(() => body.ChallengeResponse());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void An_event_without_a_trigger_uuid_has_no_provider_event_id_and_none_is_synthesized(string? trigger)
    {
        var body = Read(EventJson(trigger));

        Assert.Equal(MondayWebhookBodyKind.Event, body.Kind);
        Assert.Null(body.SourceEventId);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\" padded\"")]
    [InlineData("\"line\\nbreak\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("[\"x\"]")]
    public void An_event_whose_trigger_uuid_is_present_but_not_an_identifier_is_refused(string trigger)
    {
        Assert.Equal(MondayWebhookBodyKind.Malformed, Read(EventJson(trigger)).Kind);
    }

    [Fact]
    public void A_trigger_uuid_is_bounded_like_every_provider_identifier()
    {
        Assert.Equal(MondayWebhookBodyKind.Event, Read(EventJson("\"" + new string('a', 200) + "\"")).Kind);
        Assert.Equal(MondayWebhookBodyKind.Malformed, Read(EventJson("\"" + new string('a', 201) + "\"")).Kind);
    }

    [Theory]
    [InlineData("""{"event":"create_item"}""")]
    [InlineData("""{"event":null}""")]
    [InlineData("""{"event":[]}""")]
    [InlineData("""{"events":{}}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""[{"event":{}}]""")]
    [InlineData("\"event\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("""{"event":{},"event":{}}""")]
    [InlineData("""{"event":{"triggerUuid":"a","triggerUuid":"b"}}""")]
    [InlineData("""{"event":{},}""")]
    [InlineData("""{"event":{}} // comment""")]
    [InlineData("""{"event":{}""")]
    [InlineData("""{"event":{}}{"event":{}}""")]
    [InlineData("")]
    [InlineData(" ")]
    public void A_body_that_is_neither_a_challenge_nor_an_event_or_not_strict_json_is_malformed(string json)
    {
        Assert.Equal(MondayWebhookBodyKind.Malformed, Read(json).Kind);
    }

    [Fact]
    public void Bytes_that_are_not_well_formed_utf8_or_carry_a_byte_order_mark_are_malformed()
    {
        var valid = Encoding.UTF8.GetBytes(EventJson("\"t-1\""));

        Assert.Equal(MondayWebhookBodyKind.Event, MondayWebhookBody.Read(valid).Kind);
        Assert.Equal(MondayWebhookBodyKind.Malformed, MondayWebhookBody.Read([0xEF, 0xBB, 0xBF, .. valid]).Kind);
        Assert.Equal(MondayWebhookBodyKind.Malformed, MondayWebhookBody.Read([.. valid[..^2], 0xC3, 0x28, .. valid[^2..]]).Kind);
        Assert.Equal(MondayWebhookBodyKind.Malformed, MondayWebhookBody.Read([.. valid[..^2], 0xED, 0xA0, 0x80, .. valid[^2..]]).Kind);
        Assert.Equal(MondayWebhookBodyKind.Malformed, MondayWebhookBody.Read([]).Kind);
    }

    [Fact]
    public void Nesting_is_bounded_at_64_levels()
    {
        static string Nested(int depth) => "{\"event\":" + string.Concat(Enumerable.Repeat("{\"a\":", depth - 2)) + "{}" + new string('}', depth - 2) + "}";

        Assert.Equal(MondayWebhookBodyKind.Event, Read(Nested(64)).Kind);
        Assert.Equal(MondayWebhookBodyKind.Malformed, Read(Nested(65)).Kind);
    }

    [Fact]
    public void A_body_over_256_kib_is_refused_whole_and_never_truncated()
    {
        var prefix = "{\"event\":{\"type\":\"create_update\",\"body\":\"";
        const string suffix = "\"}}";
        var exactly = prefix + new string('x', MondayWebhooks.MaxBodyBytes - prefix.Length - suffix.Length) + suffix;

        Assert.Equal(MondayWebhooks.MaxBodyBytes, Encoding.UTF8.GetByteCount(exactly));
        Assert.Equal(MondayWebhookBodyKind.Event, Read(exactly).Kind);
        Assert.Equal(MondayWebhookBodyKind.Malformed, Read(exactly[..^3] + "x" + suffix).Kind);
        Assert.Equal(262_144, MondayWebhooks.MaxBodyBytes);
    }

    [Fact]
    public async Task An_event_becomes_a_submission_of_the_body_exactly_as_received()
    {
        var harness = new WebhookHarness();
        var json = "{ \"event\" : {\"pulseName\":\"" + Marker + "\",\"triggerUuid\":\"t-77\",\"changedAt\":1727697600.123456,"
            + "\"triggerTime\":\"2026-09-30T12:00:00.000Z\",\"originalTriggerUuid\":null,\"userId\":4242,\"tenantId\":\"forged\"} }";
        var authentication = await harness.AuthenticateAsync(WebhookTokens.Valid(harness.Connection));
        var correlation = CorrelationId.New();

        var submission = Read(json).ToSubmission(authentication, correlation);

        Assert.Equal(harness.Connection, submission.ConnectionId);
        Assert.Equal("monday", submission.SourceSystem);
        Assert.Equal("t-77", submission.SourceEventId);
        Assert.Equal("monday.webhook.v1", submission.SchemaVersion);
        Assert.Equal(json, submission.Payload);
        Assert.Equal(correlation, submission.CorrelationId);
        Assert.Equal(WebhookTokens.Valid(harness.Connection), submission.Proof);
        Assert.Null(submission.Validate());
        Assert.DoesNotContain(Marker, submission.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_an_authenticated_event_becomes_a_submission()
    {
        var harness = new WebhookHarness();
        var refused = await harness.AuthenticateAsync("not a token");
        var unavailable = await new WebhookHarness(withSecret: false).AuthenticateAsync(WebhookTokens.Valid(harness.Connection));
        var verified = await harness.AuthenticateAsync(WebhookTokens.Valid(harness.Connection));

        Assert.Throws<InvalidOperationException>(() => Read(EventJson("\"t-1\"")).ToSubmission(refused, CorrelationId.New()));
        Assert.Throws<InvalidOperationException>(() => Read(EventJson("\"t-1\"")).ToSubmission(unavailable, CorrelationId.New()));
        Assert.Throws<InvalidOperationException>(() => Read("""{"challenge":"abc"}""").ToSubmission(verified, CorrelationId.New()));
        Assert.Throws<InvalidOperationException>(() => Read("[]").ToSubmission(verified, CorrelationId.New()));
        Assert.Throws<ArgumentNullException>(() => Read(EventJson("\"t-1\"")).ToSubmission(null!, CorrelationId.New()));
    }

    [Fact]
    public void A_body_never_renders_its_content()
    {
        foreach (var json in new[] { EventJson("\"" + Marker + "\""), "{\"challenge\":\"" + Marker + "\"}", "{\"x\":\"" + Marker + "\"}" })
        {
            var body = Read(json);

            Assert.DoesNotContain(Marker, body.ToString(), StringComparison.Ordinal);
            Assert.StartsWith("MondayWebhookBody(", body.ToString(), StringComparison.Ordinal);
        }
    }

    private static MondayWebhookBody Read(string json) => MondayWebhookBody.Read(Encoding.UTF8.GetBytes(json));

    private static string EventJson(string? triggerJson) =>
        "{\"event\":{\"app\":\"monday\",\"type\":\"update_column_value\",\"boardId\":4401,\"pulseId\":8812,\"pulseName\":\"" + Marker + "\""
        + (triggerJson is null ? string.Empty : ",\"triggerUuid\":" + triggerJson) + "}}";
}
