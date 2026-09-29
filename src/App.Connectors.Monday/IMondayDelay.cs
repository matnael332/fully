namespace WorkContinuity.Connectors.Monday;

/// <summary>Waits between attempts. A seam so that tests observe the waits instead of sleeping through them.</summary>
public interface IMondayDelay
{
    /// <summary>Waits for the given time.</summary>
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>The production delay: a real timer.</summary>
public sealed class SystemMondayDelay : IMondayDelay
{
    /// <inheritdoc />
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}
