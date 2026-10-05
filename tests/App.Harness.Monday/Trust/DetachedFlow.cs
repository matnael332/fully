namespace WorkContinuity.Harness.Monday.Trust;

/// <summary>
/// Starts work on an execution flow of its own: nothing the caller's flow carries, a tenant least of all, flows into it.
/// </summary>
/// <remarks>
/// The tenant context is flow-scoped. The relay and the consumer refuse to run on a flow that already carries a tenant,
/// because they resolve each tenant themselves, and a trusted run must never inherit or lend one. Starting each of them
/// through this type makes that a property of how they are started rather than of who starts them.
/// </remarks>
internal static class DetachedFlow
{
    /// <summary>Runs work that returns a value on a new, empty flow.</summary>
    internal static Task<TResult> Run<TResult>(Func<Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (ExecutionContext.IsFlowSuppressed())
        {
            return Task.Run(work, CancellationToken.None);
        }

        // Suppressed only while the task is queued, so the new flow starts empty.
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, CancellationToken.None);
        }
    }

    /// <summary>Runs work on a new, empty flow.</summary>
    internal static Task Run(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (ExecutionContext.IsFlowSuppressed())
        {
            return Task.Run(work, CancellationToken.None);
        }

        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, CancellationToken.None);
        }
    }
}
