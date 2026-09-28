using WorkContinuity.Core.Events;

namespace WorkContinuity.Unit.Tests;

/// <summary>
/// The shared identifier rule decides what a canonical event and an ingress delivery may carry as a
/// provider identifier or reference. It reads whole code points, so text that cannot be stored, or that
/// hides an invisible character in a surrogate pair, is refused before it reaches a key or a log.
/// </summary>
public sealed class EventTextTests
{
    private static readonly string LoneHighSurrogate = ((char)0xD800).ToString();
    private static readonly string LoneLowSurrogate = ((char)0xDC00).ToString();

    [Fact]
    public void A_well_formed_character_outside_the_basic_plane_is_accepted()
    {
        var grinningFace = char.ConvertFromUtf32(0x1F600);

        Assert.True(EventText.IsIdentifier("evt-" + grinningFace));
        Assert.True(EventText.IsReference("ref-" + grinningFace));
    }

    [Fact]
    public void Malformed_or_invisible_text_is_neither_an_identifier_nor_a_reference()
    {
        var cases = new (string Case, string Value)[]
        {
            ("lone high surrogate at the end", "evt-" + LoneHighSurrogate),
            ("lone high surrogate at the start", LoneHighSurrogate + "-evt"),
            ("lone low surrogate", "evt-" + LoneLowSurrogate + "-1"),
            ("surrogates in the wrong order", "evt-" + LoneLowSurrogate + LoneHighSurrogate),
            ("invisible tag character outside the basic plane", "evt-" + char.ConvertFromUtf32(0xE0041)),
            ("zero-width space", "evt-" + char.ConvertFromUtf32(0x200B) + "1"),
            ("line separator", "evt-" + char.ConvertFromUtf32(0x2028) + "1"),
            ("control character", "evt-" + char.ConvertFromUtf32(0x0007) + "1"),
        };

        foreach (var (name, value) in cases)
        {
            Assert.False(EventText.IsIdentifier(value), $"identifier accepted: {name}");
            Assert.False(EventText.IsReference(value), $"reference accepted: {name}");
            Assert.False(EventText.IsValue(value), $"value accepted: {name}");
        }
    }

    [Fact]
    public void A_field_value_is_bounded_single_line_text_and_a_cleared_field_is_never_empty_text()
    {
        Assert.True(EventText.IsValue("done"));
        Assert.True(EventText.IsValue("In progress"));
        Assert.True(EventText.IsValue("2026-10-01T09:00:00Z"));
        Assert.True(EventText.IsValue("ok-" + char.ConvertFromUtf32(0x1F600)));
        Assert.True(EventText.IsValue(new string('v', EventText.MaxValueLength)));

        Assert.False(EventText.IsValue(null));
        Assert.False(EventText.IsValue(string.Empty));
        Assert.False(EventText.IsValue(new string('v', EventText.MaxValueLength + 1)));
        Assert.False(EventText.IsValue("two\nlines"));
        Assert.False(EventText.IsValue("tab\tinside"));
        Assert.False(EventText.IsValue(" leading"));
        Assert.False(EventText.IsValue("trailing "));
        Assert.Equal(1024, EventText.MaxValueLength);
    }
}
