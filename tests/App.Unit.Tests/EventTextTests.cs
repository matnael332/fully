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
        }
    }
}
