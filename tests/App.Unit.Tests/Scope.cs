namespace WorkContinuity.Unit.Tests;

internal static class Scope
{
    /// <summary>
    /// Runs assertions inside a fresh execution-context scope.
    /// </summary>
    /// <remarks>
    /// The tenant and correlation accessors are flow-scoped. Assigning ambient state directly in a
    /// test method would mutate the execution context the test framework called it on, so scope
    /// isolation is asserted from inside a child scope, exactly as a request or worker pass does.
    /// </remarks>
    internal static Task RunAsync(Action action) =>
        Task.Run(action, TestContext.Current.CancellationToken);
}
