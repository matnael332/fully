using Microsoft.Extensions.Configuration;
using WorkContinuity.Harness.Monday.Composition;
using WorkContinuity.Harness.Monday.Configuration;
using WorkContinuity.Harness.Monday.Operator;
using WorkContinuity.Harness.Monday.Trust;

namespace WorkContinuity.Harness.Monday;

/// <summary>
/// The standalone entry point of the harness, and why it does not run in the offline slice.
/// </summary>
/// <remarks>
/// <para>
/// It reads the harness's configuration, refuses an incomplete or invalid one or one whose redirect address does not
/// name the fixed operator callback port (owner decision B-13), and then requires a database the owner approved for a
/// standalone run. None is approved in the offline slice (owner decision D3G-4), so this entry point always stops there:
/// it creates no certificate and opens no listener, no database connection and no broker connection.
/// </para>
/// <para>
/// Past that gate it would create the operator listener's certificate for this run only, print its SHA-256 fingerprint,
/// require the owner's DEV/TEST attestation (owner decisions B-12 and B-13), and start with the fail-closed defaults for
/// everything else: the certificate is the one seam it passes, so there is no secret storage and no provider transport
/// (owner decision D3G-3), and nothing can authorize or reach a provider. There is no argument, environment variable or
/// setting that supplies a seam, enables a real provider call or opens a browser. What lifts each of those is a later,
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

    /// <summary>The process exit code when the operator did not give the DEV/TEST attestation.</summary>
    public const int NotAttested = 5;

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

        if (options.OperatorPort != HarnessOptions.OperatorCallbackPort)
        {
            await output.WriteLineAsync($"harness: not started ({HarnessRefusal.RedirectNotTheFixedCallbackPort})");
            return NotConfigured;
        }

        if (!HarnessStandaloneGate.Permits(options))
        {
            await output.WriteLineAsync($"harness: not started ({HarnessRefusal.StandaloneDatabaseNotApproved})");
            return NotApproved;
        }

        // The operator listener's certificate exists for this run only and is disposed after the host (owner decision B-13).
        // Its fingerprint is shown before anything listens, so the owner can compare it with the browser's.
        using var certificate = OperatorTlsCertificate.Create(TimeProvider.System.GetUtcNow());
        await output.WriteLineAsync($"harness: operator TLS certificate SHA-256 fingerprint {OperatorTlsCertificate.Fingerprint(certificate)}");
        await output.WriteLineAsync("harness: it is valid for this run only and is added to no certificate store; compare it before accepting the browser's warning once.");

        // The owner's per-run DEV/TEST attestation (owner decision B-12): nothing starts without it.
        foreach (var statement in OperatorAttestation.Statement)
        {
            await output.WriteLineAsync(statement);
        }

        if (!OperatorAttestation.IsGiven(await input.ReadLineAsync(cancellationToken)))
        {
            await output.WriteLineAsync($"harness: not started ({HarnessRefusal.OperatorNotAttested})");
            return NotAttested;
        }

        var started = await HarnessHost.StartAsync(options, new HarnessSeams { RunBackboneLoops = true, OperatorCertificate = certificate }, cancellationToken);
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
