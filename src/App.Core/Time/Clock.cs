namespace WorkContinuity.Core.Time;

/// <summary>
/// Supplies the current time. Business timestamps depend on this abstraction rather than
/// <see cref="DateTime.Now"/>, which keeps startup-time measurement, approval expiry, retention
/// windows and learning periods deterministic and testable.
/// </summary>
public interface IClock
{
    /// <summary>Gets the current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// The production <see cref="IClock"/>, reading the system clock in UTC.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
