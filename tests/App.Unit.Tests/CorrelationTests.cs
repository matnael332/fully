using WorkContinuity.Core.Correlation;

namespace WorkContinuity.Unit.Tests;

public sealed class CorrelationIdTests
{
    [Fact]
    public void New_produces_a_parseable_identifier()
    {
        var correlationId = CorrelationId.New();

        Assert.False(correlationId.IsEmpty);
        Assert.True(CorrelationId.TryParse(correlationId.Value, out var reparsed));
        Assert.Equal(correlationId, reparsed);
    }

    [Fact]
    public void New_produces_distinct_identifiers()
    {
        Assert.NotEqual(CorrelationId.New(), CorrelationId.New());
    }

    [Fact]
    public void Default_is_empty()
    {
        Assert.True(default(CorrelationId).IsEmpty);
    }

    [Fact]
    public void Reading_an_unset_value_throws_rather_than_returning_null()
    {
        Assert.Throws<InvalidOperationException>(() => default(CorrelationId).Value);
    }

    [Theory]
    [InlineData("abcd1234")]
    [InlineData("trace-id_1.2")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void A_safe_inbound_value_is_accepted(string candidate)
    {
        Assert.True(CorrelationId.TryParse(candidate, out var correlationId));
        Assert.Equal(candidate, correlationId.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("      ")]
    [InlineData("short")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("comma,separated")]
    [InlineData("slash/value")]
    [InlineData("angle<bracket>")]
    public void An_unsafe_or_malformed_inbound_value_is_rejected(string? candidate)
    {
        Assert.False(CorrelationId.TryParse(candidate, out var correlationId));
        Assert.True(correlationId.IsEmpty);
    }

    [Fact]
    public void A_value_longer_than_the_maximum_is_rejected()
    {
        Assert.False(CorrelationId.TryParse(new string('a', CorrelationId.MaxLength + 1), out _));
        Assert.True(CorrelationId.TryParse(new string('a', CorrelationId.MaxLength), out _));
    }
}

public sealed class CorrelationContextTests
{
    [Fact]
    public void Generate_marks_the_identifier_as_locally_created()
    {
        var context = CorrelationContext.Generate();

        Assert.False(context.WasPropagated);
        Assert.False(context.CorrelationId.IsEmpty);
    }

    [Fact]
    public void A_valid_inbound_identifier_is_propagated_unchanged()
    {
        const string inbound = "inbound-correlation-1";

        var context = CorrelationContext.FromInbound(inbound);

        Assert.True(context.WasPropagated);
        Assert.Equal(inbound, context.CorrelationId.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad value")]
    [InlineData("tiny")]
    public void A_missing_or_unsafe_inbound_identifier_is_replaced(string? inbound)
    {
        var context = CorrelationContext.FromInbound(inbound);

        Assert.False(context.WasPropagated);
        Assert.False(context.CorrelationId.IsEmpty);
    }

    [Fact]
    public void An_injection_attempt_does_not_survive_into_the_identifier()
    {
        var context = CorrelationContext.FromInbound("validpart\r\nX-Injected-Header: evil");

        Assert.False(context.WasPropagated);
        Assert.DoesNotContain("Injected", context.CorrelationId.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", context.CorrelationId.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", context.CorrelationId.Value, StringComparison.Ordinal);
    }
}

public sealed class CorrelationContextAccessorTests
{
    [Fact]
    public async Task Current_is_null_before_an_operation_starts()
    {
        await Scope.RunAsync(() =>
        {
            var accessor = new CorrelationContextAccessor();

            Assert.Null(accessor.Current);
        });
    }

    [Fact]
    public async Task Start_publishes_the_context_to_the_current_scope()
    {
        await Scope.RunAsync(() =>
        {
            var accessor = new CorrelationContextAccessor();
            var expected = CorrelationContext.Generate();

            accessor.Start(expected);

            Assert.Same(expected, accessor.Current);
        });
    }

    [Fact]
    public async Task Each_operation_may_start_its_own_correlation_scope()
    {
        await Scope.RunAsync(() =>
        {
            var accessor = new CorrelationContextAccessor();

            var first = CorrelationContext.Generate();
            accessor.Start(first);
            var second = CorrelationContext.Generate();
            accessor.Start(second);

            Assert.Same(second, accessor.Current);
            Assert.NotEqual(first.CorrelationId, second.CorrelationId);
        });
    }

    [Fact]
    public async Task Start_rejects_null()
    {
        await Scope.RunAsync(() =>
        {
            var accessor = new CorrelationContextAccessor();

            Assert.Throws<ArgumentNullException>(() => accessor.Start(null!));
        });
    }
}
