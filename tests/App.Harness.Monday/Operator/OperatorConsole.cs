using System.Globalization;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Results;
using WorkContinuity.Core.Transport;
using WorkContinuity.Harness.Monday.Diagnostics;

namespace WorkContinuity.Harness.Monday.Operator;

/// <summary>
/// The operator's console: a closed set of commands, each one explicit action, each answered in fixed words.
/// </summary>
/// <remarks>
/// <para>
/// Every operator action is started here and nowhere else: no route starts one except the authorization start, which
/// needs the capability this console mints. A command takes at most a connection id and a resource id, both typed by the
/// operator and both validated before use; it cannot name whose connection it is, which the harness's configuration
/// alone decides.
/// </para>
/// <para>
/// An answer holds ids, states, counts, times, closed outcome names and fixed outcome codes. It never holds a value
/// of current work, a token, a code, a state, a header or provider text: current work is shown by object, field and the
/// length of its value. The one secret an answer carries is the single-use start capability, inside the address the
/// operator must open, shown once and dead after two minutes or one use.
/// </para>
/// </remarks>
public sealed class OperatorConsole
{
    private readonly OperatorActions _actions;
    private readonly Func<Guid?, string> _beginAuthorization;
    private readonly Func<CancellationToken, Task<OutboxRelayPass>> _relayOnce;
    private readonly HarnessDiagnostics _diagnostics;

    internal OperatorConsole(
        OperatorActions actions,
        Func<Guid?, string> beginAuthorization,
        Func<CancellationToken, Task<OutboxRelayPass>> relayOnce,
        HarnessDiagnostics diagnostics)
    {
        _actions = actions;
        _beginAuthorization = beginAuthorization;
        _relayOnce = relayOnce;
        _diagnostics = diagnostics;
    }

    /// <summary>The commands, as <c>help</c> lists them.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        "authorize",
        "reauthorize <connection-id>",
        "disconnect <connection-id>",
        "scope <connection-id> <resource-id> enable|disable",
        "sync-start <connection-id>",
        "sync-run <connection-id>",
        "reconcile <connection-id>",
        "sweep <connection-id>",
        "cleanup",
        "relay",
        "work",
        "evidence",
        "diagnostics",
        "help",
        "quit",
    ];

    /// <summary>Runs one command line and writes its answer.</summary>
    /// <param name="line">The line the operator typed.</param>
    /// <param name="output">Where the answer goes.</param>
    /// <param name="cancellationToken">Cancels the action.</param>
    /// <returns>False when the operator asked to quit.</returns>
    public async Task<bool> ExecuteAsync(string? line, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        var words = (line ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return true;
        }

        switch (words[0])
        {
            case "quit" when words.Length == 1:
                return false;

            case "help" when words.Length == 1:
                foreach (var command in Commands)
                {
                    await output.WriteLineAsync(command);
                }

                break;

            case "authorize" when words.Length == 1:
                await output.WriteLineAsync("Open this address in your browser within 2 minutes; it works once:");
                await output.WriteLineAsync(_beginAuthorization(null));
                break;

            case "reauthorize" when words.Length == 2 && TryConnection(words[1], out var connection):
                await output.WriteLineAsync("Open this address in your browser within 2 minutes; it works once:");
                await output.WriteLineAsync(_beginAuthorization(connection));
                break;

            case "disconnect" when words.Length == 2 && TryConnection(words[1], out var connection):
                await AnswerAsync(output, "disconnect", await _actions.DisconnectAsync(connection, cancellationToken), disconnection =>
                    $"already-disconnected={disconnection.WasAlreadyDisconnected} provider-revocation-confirmed={disconnection.ProviderRevocationConfirmed}");
                break;

            case "scope" when words.Length == 4 && TryConnection(words[1], out var connection) && IsResource(words[2]) && words[3] is "enable" or "disable":
                await AnswerAsync(
                    output,
                    "scope",
                    await _actions.ConfigureScopeAsync(connection, words[2], words[3] == "enable", cancellationToken),
                    scope => $"resource={scope.ResourceId} enabled={scope.Enabled}");
                break;

            case "sync-start" when words.Length == 2 && TryConnection(words[1], out var connection):
                await AnswerAsync(output, "sync-start", await _actions.StartInitialSyncAsync(connection, cancellationToken), Describe);
                break;

            case "sync-run" when words.Length == 2 && TryConnection(words[1], out var connection):
                await AnswerAsync(output, "sync-run", await _actions.RunInitialSyncAsync(connection, cancellationToken), Describe);
                break;

            case "reconcile" when words.Length == 2 && TryConnection(words[1], out var connection):
                await AnswerAsync(output, "reconcile", await _actions.ReconcileAsync(connection, cancellationToken), run =>
                    $"claimed={run.Claimed} not-applied={run.NotApplied} outcomes=[{string.Join(", ", run.Outcomes.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}:{entry.Value}"))}]");
                break;

            case "sweep" when words.Length == 2 && TryConnection(words[1], out var connection):
                await AnswerAsync(output, "sweep", await _actions.RequestSweepAsync(connection, cancellationToken), requested => $"requested={requested}");
                break;

            case "cleanup" when words.Length == 1:
                await AnswerAsync(output, "cleanup", await _actions.CleanUpVerifiersAsync(cancellationToken), cleanup =>
                    $"deleted={cleanup.Deleted} remaining={cleanup.Remaining}");
                break;

            case "relay" when words.Length == 1:
                var pass = await _relayOnce(cancellationToken);
                await output.WriteLineAsync(Invariant(
                    $"relay: published={pass.Published} unrelayable={pass.Unrelayable} publish-failures={pass.PublishFailures} commit-failures={pass.CommitFailures} discovery-failed={pass.DiscoveryFailed} more-work={pass.MoreWork}"));
                break;

            case "work" when words.Length == 1:
                await WriteWorkAsync(output, cancellationToken);
                break;

            case "evidence" when words.Length == 1:
                await WriteEvidenceAsync(output, cancellationToken);
                break;

            case "diagnostics" when words.Length == 1:
                foreach (var entry in _diagnostics.Render())
                {
                    await output.WriteLineAsync(entry);
                }

                break;

            default:
                // The line is never echoed: it may hold a mistyped secret.
                await output.WriteLineAsync("Not a command. Type help.");
                break;
        }

        return true;
    }

    private async Task WriteWorkAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var read = await _actions.ReadCurrentWorkAsync(cancellationToken);
        if (read.IsFailure)
        {
            await output.WriteLineAsync("work: failed " + read.Error.Code);
            return;
        }

        await output.WriteLineAsync(Invariant($"work: objects={read.Value.Objects.Count} fields={read.Value.Fields.Count}"));
        foreach (var field in read.Value.Fields)
        {
            // The value itself is work content: its length is shown, never its text.
            await output.WriteLineAsync(Invariant(
                $"work-field {field.ObjectType} {field.ObjectId} {field.Field} type={field.DataType} value-length={(field.Value is null ? "null" : field.Value.Length.ToString(CultureInfo.InvariantCulture))} source-updated={field.SourceUpdatedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none"} version={field.FieldVersion}"));
        }
    }

    private async Task WriteEvidenceAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var read = await _actions.ReadEvidenceAsync(cancellationToken);
        if (read.IsFailure)
        {
            await output.WriteLineAsync("evidence: failed " + read.Error.Code);
            return;
        }

        var evidence = read.Value;
        foreach (var count in evidence.Receipts)
        {
            await output.WriteLineAsync(Invariant($"receipts {string.Join(' ', count.Codes)} = {count.Count}"));
        }

        await output.WriteLineAsync(Invariant($"receipts highest-ingress-sequence = {evidence.HighestIngressSequence?.ToString(CultureInfo.InvariantCulture) ?? "none"}"));
        foreach (var count in evidence.Ledger)
        {
            await output.WriteLineAsync(Invariant($"ledger {string.Join(' ', count.Codes.Where(code => code.Length > 0))} = {count.Count}"));
        }

        foreach (var count in evidence.Targets)
        {
            await output.WriteLineAsync(Invariant($"targets {string.Join(' ', count.Codes.Where(code => code.Length > 0))} = {count.Count}"));
        }

        foreach (var sync in evidence.Syncs)
        {
            await output.WriteLineAsync(Invariant(
                $"sync {sync.ConnectionId:D} generation={sync.Generation} state={sync.State} last-outcome={Shown(sync.LastOutcome)} scope-version={sync.ScopeVersion} replay-floor={sync.ReplayFloor} cutover-fence={sync.CutoverFence?.ToString(CultureInfo.InvariantCulture) ?? "none"} live-at={sync.LiveAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none"}"));
        }

        foreach (var scope in evidence.SyncScopes)
        {
            await output.WriteLineAsync(Invariant(
                $"sync-scope {scope.ConnectionId:D} generation={scope.Generation} resource={scope.ResourceId} state={scope.State} last-outcome={Shown(scope.LastOutcome)} pages={scope.PagesRead} objects={scope.ObjectsRead} fields={scope.FieldsApplied}"));
        }

        foreach (var scope in evidence.SourceScopes)
        {
            await output.WriteLineAsync(Invariant(
                $"source-scope {scope.ConnectionId:D} resource={scope.ResourceId} enabled={scope.Enabled} baseline-generation={scope.BaselineGeneration?.ToString(CultureInfo.InvariantCulture) ?? "none"}"));
        }

        foreach (var authorization in evidence.Authorizations)
        {
            await output.WriteLineAsync(Invariant(
                $"authorization {authorization.ConnectionId:D} state={authorization.State} generation={authorization.Generation} authorized-at={authorization.AuthorizedAtUtc:O} refreshed-at={authorization.RefreshedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none"} revoked-at={authorization.RevokedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none"} provider-revocation-confirmed={authorization.ProviderRevocationConfirmed}"));
        }

        await output.WriteLineAsync(Invariant(
            $"authorization-transactions open={evidence.Transactions.Open} verifier-due={evidence.Transactions.VerifierDue} verifier-deleted={evidence.Transactions.VerifierDeleted}"));
    }

    private static async Task AnswerAsync<TValue>(TextWriter output, string action, Result<TValue> result, Func<TValue, FormattableString> describe)
    {
        await output.WriteLineAsync(result.IsSuccess
            ? action + ": " + Invariant(describe(result.Value))
            : action + ": failed " + result.Error.Code);
    }

    private static FormattableString Describe(InitialSyncSummary summary) =>
        $"generation={summary.Generation} state={summary.State} fields-applied={summary.FieldsApplied} fields-held={summary.FieldsHeld} receipts-replayed={summary.ReceiptsReplayed} scope-outcomes=[{string.Join(", ", summary.ScopeOutcomes.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}:{entry.Value}"))}]";

    private static string Shown(string code) => code.Length == 0 ? "none" : code;

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    // The canonical form only, as a webhook address names a connection: lowercase with hyphens, never the empty id.
    private static bool TryConnection(string word, out Guid connectionId) => MondayWebhooks.TryParseConnectionId(word, out connectionId);

    // A monday board id: decimal digits only.
    private static bool IsResource(string word) => word.Length is > 0 and <= 20 && word.All(char.IsAsciiDigit);
}
