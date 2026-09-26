using WorkContinuity.Core.Results;
using WorkContinuity.Core.Time;

namespace WorkContinuity.Unit.Tests;

public sealed class ClockTests
{
    [Fact]
    public void The_system_clock_reports_utc()
    {
        IClock clock = new SystemClock();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }

    [Fact]
    public void The_system_clock_is_close_to_the_ambient_time()
    {
        IClock clock = new SystemClock();

        var difference = (clock.UtcNow - DateTimeOffset.UtcNow).Duration();

        Assert.True(difference < TimeSpan.FromMinutes(1), $"Clock drifted by {difference}.");
    }

    [Fact]
    public void The_system_clock_advances_monotonically_across_reads()
    {
        IClock clock = new SystemClock();

        var first = clock.UtcNow;
        var second = clock.UtcNow;

        Assert.True(second >= first);
    }

    [Fact]
    public void A_test_clock_can_replace_the_system_clock()
    {
        // The abstraction exists so business timestamps are controllable in tests rather than
        // reading the machine clock through DateTime.Now.
        var fixedInstant = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        IClock clock = new FixedClock(fixedInstant);

        Assert.Equal(fixedInstant, clock.UtcNow);
    }

    private sealed class FixedClock(DateTimeOffset instant) : IClock
    {
        public DateTimeOffset UtcNow { get; } = instant;
    }
}

public sealed class ResultTests
{
    [Fact]
    public void A_success_has_no_error()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void A_failure_carries_its_error()
    {
        var error = Error.Validation("tenant.missing", "A tenant is required.");

        var result = Result.Failure(error);

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void A_failure_requires_an_error()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void A_generic_success_carries_its_value()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void A_generic_failure_exposes_no_value()
    {
        var error = Error.NotFound("task.missing", "The task does not exist.");

        var result = Result<int>.Failure(error);

        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void A_generic_success_may_carry_a_reference_value()
    {
        var result = Result<string>.Success("ready");

        Assert.Equal("ready", result.Value);
    }
}

public sealed class ErrorTests
{
    [Fact]
    public void Every_required_failure_category_has_a_factory()
    {
        Assert.Equal(ErrorCategory.Validation, Error.Validation("c", "m").Category);
        Assert.Equal(ErrorCategory.NotFound, Error.NotFound("c", "m").Category);
        Assert.Equal(ErrorCategory.Conflict, Error.Conflict("c", "m").Category);
        Assert.Equal(ErrorCategory.Unauthorized, Error.Unauthorized("c", "m").Category);
        Assert.Equal(ErrorCategory.Forbidden, Error.Forbidden("c", "m").Category);
        Assert.Equal(ErrorCategory.Infrastructure, Error.Infrastructure("c", "m").Category);
    }

    [Fact]
    public void A_category_is_required()
    {
        Assert.Throws<ArgumentException>(() => new Error(ErrorCategory.None, "c", "m"));
    }

    [Theory]
    [InlineData("", "message")]
    [InlineData("   ", "message")]
    [InlineData("code", "")]
    [InlineData("code", "   ")]
    public void A_code_and_a_message_are_required(string code, string message)
    {
        Assert.Throws<ArgumentException>(() => Error.Validation(code, message));
    }

    [Fact]
    public void A_message_may_not_smuggle_control_characters_into_logs()
    {
        Assert.Throws<ArgumentException>(
            () => Error.Validation("code", "line one\nFAKE LOG ENTRY"));
    }

    [Fact]
    public void Overlong_codes_and_messages_are_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => Error.Validation(new string('c', Error.MaxCodeLength + 1), "message"));

        Assert.Throws<ArgumentException>(
            () => Error.Validation("code", new string('m', Error.MaxMessageLength + 1)));
    }

    [Fact]
    public void An_error_cannot_carry_an_exception_or_a_stack_trace()
    {
        // The absence of these members is the control that keeps internal detail out of anything
        // returned to a caller or written to a log.
        var members = typeof(Error).GetMembers();

        Assert.DoesNotContain(
            members,
            member => member.Name.Contains("Stack", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("InnerError", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            typeof(Error).GetProperties(),
            property => typeof(Exception).IsAssignableFrom(property.PropertyType));

        // No constructor or factory accepts an exception, so one cannot be passed in by accident.
        var parameterTypes = typeof(Error).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Concat(typeof(Error).GetMethods().SelectMany(method => method.GetParameters()))
            .Select(parameter => parameter.ParameterType);

        Assert.DoesNotContain(parameterTypes, type => typeof(Exception).IsAssignableFrom(type));
    }

    [Fact]
    public void ToString_exposes_only_the_category_and_code()
    {
        var error = Error.Forbidden("action.not_permitted", "Not permitted.");

        Assert.Equal("Forbidden:action.not_permitted", error.ToString());
    }
}
