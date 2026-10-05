using Microsoft.Extensions.Configuration;
using WorkContinuity.Harness.Monday.Composition;
using WorkContinuity.Harness.Monday.Configuration;

namespace WorkContinuity.Harness.Monday;

/// <summary>
/// The standalone entry point of the harness, and why it does not run in the offline slice.
/// </summary>
/// <remarks>
/// <para>
/// It reads the harness's configuration, refuses an incomplete or invalid one, and then requires a database the owner
/// approved for a standalone run. None is approved in the offline slice (owner decision D3G-4), so this entry point
/// always stops there: it opens no listener, no database connection and no broker connection.
/// </para>
/// <para>
/// Even past that gate it would start with the fail-closed defaults only, because it passes no seam: no secret storage,
/// no provider transport and no operator certificate (owner decision D3G-3). There is no argument, environment variable
/// or setting that supplies one, enables a real provider call or opens a browser. What lifts each of those is a later,
/// separate owner decision and a reviewed change, not configuration.
/// </para>
/// </remarks>
public static class HarnessProgram
{
    /// <summary>The process exit code when the configuration is incomplete or invalid.</summary>
    public const int NotConfigured = 2;

    /// <summary>The process exit code when no database is approved for a standalone run.</summary>
    public const int NotApproved = 3;

    /// <summary>The process exit code when the composition refused to start.</summary>
    public const int NotStarted = 4;

    /// <summary>Runs the standalone harness until the operator quits or the token is cancelled.</summary>
    /// <param name="configuration">The harness's configuration.</param>
    /// <param name="input">The operator's command lines.</param>
    /// <param name="output">Where answers go.</param>
    /// <param name="cancellationToken">Stops the harness.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(IConfiguration configuration, TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var read = HarnessOptions.Read(configuration);
        if (read.Options is not { } options)
        {
            await output.WriteLineAsync($"harness: not started ({read.Refusal})");
            return NotConfigured;
        }

        if (!HarnessStandaloneGate.Permits(options))
        {
            await output.WriteLineAsync($"harness: not started ({HarnessRefusal.StandaloneDatabaseNotApproved})");
            return NotApproved;
        }

        var started = await HarnessHost.StartAsync(options, new HarnessSeams { RunBackboneLoops = true }, cancellationToken);
        if (started.Host is not { } host)
        {
            await output.WriteLineAsync($"harness: not started ({started.Refusal})");
            return NotStarted;
        }

        await using (host)
        {
            await output.WriteLineAsync("harness: started. Type help.");
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(cancellationToken);
                if (line is null || !await host.Console.ExecuteAsync(line, output, cancellationToken))
                {
                    break;
                }
            }
        }

        return 0;
    }
}
