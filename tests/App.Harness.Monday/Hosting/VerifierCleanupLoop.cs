using Microsoft.Extensions.Logging;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Results;
using WorkContinuity.Harness.Monday.Diagnostics;

namespace WorkContinuity.Harness.Monday.Hosting;

/// <summary>
/// Hosts the PKCE verifier cleanup as the owner's hosting decision of 2026-10-03 requires of the 3G harness: once at
/// start-up, then every five minutes while the harness is active, only under the trusted tenant context.
/// </summary>
/// <remarks>
/// <para>
/// The start-up run provides restart recovery: a verifier a completion left behind before the process stopped is
/// deleted without waiting for another authorization. The periodic run retries a deletion the secret store refused.
/// The harness gives the loop one thing to run, the operator action, so each run has a trusted execution flow and a
/// dependency scope of its own and acts for the one tenant the harness works for; nothing here enumerates tenants.
/// </para>
/// <para>
/// The interval is fixed in code. No setting, seam or argument changes it; a test replaces only the source
/// of time, to make the ticks arrive when it says, and the period the timer is asked for stays five minutes. A run that
/// fails or throws is counted and logged by its type, and the next tick runs as scheduled.
/// </para>
/// </remarks>
public sealed class VerifierCleanupLoop
{
    /// <summary>The time between cleanups while the harness is active.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private const string LogCategory = "WorkContinuity.Harness.Monday.VerifierCleanup";

    private readonly Func<CancellationToken, Task<Result<MondayVerifierCleanup>>> _cleanUp;
    private readonly TimeProvider _time;
    private readonly HarnessDiagnostics _diagnostics;
    private readonly ILogger _logger;

    /// <summary>Creates the loop.</summary>
    /// <param name="cleanUp">Runs one cleanup. The harness passes the operator action, which runs in its own trusted flow.</param>
    /// <param name="time">The source of the periodic ticks.</param>
    /// <param name="diagnostics">Counts the runs.</param>
    /// <param name="loggers">The harness's logger factory.</param>
    public VerifierCleanupLoop(
        Func<CancellationToken, Task<Result<MondayVerifierCleanup>>> cleanUp,
        TimeProvider time,
        HarnessDiagnostics diagnostics,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(cleanUp);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(loggers);

        _cleanUp = cleanUp;
        _time = time;
        _diagnostics = diagnostics;
        _logger = loggers.CreateLogger(LogCategory);
    }

    /// <summary>The start-up run. The harness awaits it before it reports that it started.</summary>
    public Task RunStartupAsync(CancellationToken cancellationToken) => RunOnceAsync(startup: true, cancellationToken);

    /// <summary>Runs a cleanup on every tick of the five-minute timer until cancelled.</summary>
    public async Task RunPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunOnceAsync(startup: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private async Task RunOnceAsync(bool startup, CancellationToken cancellationToken)
    {
        try
        {
            var cleaned = await _cleanUp(cancellationToken).ConfigureAwait(false);
            if (cleaned.IsSuccess)
            {
                _diagnostics.VerifierCleanupRan(startup, CleanupOutcome.Succeeded, cleaned.Value.Deleted, cleaned.Value.Remaining);
                _logger.LogInformation(
                    "PKCE verifier cleanup ran (start-up: {Startup}): {Deleted} deleted, {Remaining} still due.",
                    startup,
                    cleaned.Value.Deleted,
                    cleaned.Value.Remaining);
            }
            else
            {
                _diagnostics.VerifierCleanupRan(startup, CleanupOutcome.Failed, 0, 0);
                _logger.LogWarning("PKCE verifier cleanup failed (start-up: {Startup}): {ErrorCode}.", startup, cleaned.Error.Code);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Only the type: an exception's text could quote what it was working on. The next tick runs as scheduled.
            _diagnostics.VerifierCleanupRan(startup, CleanupOutcome.Faulted, 0, 0);
            _logger.LogError("PKCE verifier cleanup threw {ExceptionType} (start-up: {Startup}).", exception.GetType().Name, startup);
        }
    }
}
