using Microsoft.Extensions.DependencyInjection;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Results;
using WorkContinuity.Harness.Monday.Diagnostics;
using WorkContinuity.Harness.Monday.Evidence;
using WorkContinuity.Harness.Monday.Trust;

namespace WorkContinuity.Harness.Monday.Operator;

/// <summary>The fixed errors of the harness's own operator actions. None echoes an argument.</summary>
public static class HarnessErrors
{
    /// <summary>An argument of an operator action is not well formed.</summary>
    public static Error InvalidInput { get; } =
        Error.Validation("harness.operator.invalid_input", "The operator action's arguments are not valid.");
}

/// <summary>
/// The explicit operator actions of the harness: each one bounded, each one run once, each in its own trusted flow
/// (owner decision D3G-3).
/// </summary>
/// <remarks>
/// <para>
/// An action names a connection and, for a scope, a resource id, both typed by the operator; it never names a tenant or
/// a user. The trusted flow supplies those from the harness's configuration, and the production services then check
/// every id against that tenant: a connection of another tenant is not found, under forced row-level security, before
/// anything is read from the provider.
/// </para>
/// <para>
/// Nothing here schedules anything. Initial sync, reconciliation, sweep and scope configuration run only when the
/// operator asks, once per request, within the bounds below. No action writes to the provider: the connector it calls
/// reads and ingests only.
/// </para>
/// </remarks>
public sealed class OperatorActions : IOperatorAuthorization
{
    /// <summary>The most targets one reconciliation run or one sweep request takes.</summary>
    public const int MaxTargetsPerAction = 100;

    private static readonly Dictionary<string, AuthorizationStartOutcome> StartOutcomes = new(StringComparer.Ordinal)
    {
        [ConnectorAuthorizationErrors.TenantUnresolved.Code] = AuthorizationStartOutcome.TenantUnresolved,
        [ConnectorAuthorizationErrors.UserNotEligible.Code] = AuthorizationStartOutcome.UserNotEligible,
        [ConnectorAuthorizationErrors.NotReauthorizable.Code] = AuthorizationStartOutcome.NotReauthorizable,
        [ConnectorAuthorizationErrors.InvalidRequest.Code] = AuthorizationStartOutcome.InvalidRequest,
        [ConnectorAuthorizationErrors.StorageFailed.Code] = AuthorizationStartOutcome.StorageFailed,
    };

    private static readonly Dictionary<string, AuthorizationCompletionOutcome> CompletionOutcomes = new(StringComparer.Ordinal)
    {
        [ConnectorAuthorizationErrors.TransactionNotFound.Code] = AuthorizationCompletionOutcome.TransactionNotFound,
        [ConnectorAuthorizationErrors.TransactionExpired.Code] = AuthorizationCompletionOutcome.TransactionExpired,
        [ConnectorAuthorizationErrors.BindingMismatch.Code] = AuthorizationCompletionOutcome.BindingMismatch,
        [ConnectorAuthorizationErrors.StateMismatch.Code] = AuthorizationCompletionOutcome.StateMismatch,
        [ConnectorAuthorizationErrors.Replayed.Code] = AuthorizationCompletionOutcome.Replayed,
        [ConnectorAuthorizationErrors.Denied.Code] = AuthorizationCompletionOutcome.Denied,
        [ConnectorAuthorizationErrors.IssuerMismatch.Code] = AuthorizationCompletionOutcome.IssuerMismatch,
        [ConnectorAuthorizationErrors.CodeMissing.Code] = AuthorizationCompletionOutcome.CodeMissing,
        [ConnectorAuthorizationErrors.RedirectMismatch.Code] = AuthorizationCompletionOutcome.RedirectMismatch,
        [ConnectorAuthorizationErrors.ClientUnavailable.Code] = AuthorizationCompletionOutcome.ClientUnavailable,
        [ConnectorAuthorizationErrors.ExchangeRejected.Code] = AuthorizationCompletionOutcome.ExchangeRejected,
        [ConnectorAuthorizationErrors.ExchangeFailed.Code] = AuthorizationCompletionOutcome.ExchangeFailed,
        [ConnectorAuthorizationErrors.GrantUnusable.Code] = AuthorizationCompletionOutcome.GrantUnusable,
        [ConnectorAuthorizationErrors.ScopeRefused.Code] = AuthorizationCompletionOutcome.ScopeRefused,
        [ConnectorAuthorizationErrors.IdentityUnverified.Code] = AuthorizationCompletionOutcome.IdentityUnverified,
        [ConnectorAuthorizationErrors.IdentityMismatch.Code] = AuthorizationCompletionOutcome.IdentityMismatch,
        [ConnectorAuthorizationErrors.StorageFailed.Code] = AuthorizationCompletionOutcome.StorageFailed,
        [ConnectorAuthorizationErrors.NotReauthorizable.Code] = AuthorizationCompletionOutcome.NotReauthorizable,
        [ConnectorAuthorizationErrors.UserNotEligible.Code] = AuthorizationCompletionOutcome.UserNotEligible,
        [ConnectorAuthorizationErrors.TenantUnresolved.Code] = AuthorizationCompletionOutcome.TenantUnresolved,
        [ConnectorAuthorizationErrors.InvalidRequest.Code] = AuthorizationCompletionOutcome.InvalidRequest,
    };

    private readonly TrustedFlow _flow;
    private readonly HarnessDiagnostics _diagnostics;

    /// <summary>Creates the actions.</summary>
    /// <param name="flow">Runs each action as the trusted tenant, in its own flow and scope.</param>
    /// <param name="diagnostics">Counts authorization outcomes.</param>
    public OperatorActions(TrustedFlow flow, HarnessDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _flow = flow;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public async Task<OperatorStart> StartAsync(Guid? reauthorizeConnectionId, CancellationToken cancellationToken)
    {
        var started = await _flow.RunAsync(
            (services, trust, token) => services.GetRequiredService<MondayConnectionAuthorizer>()
                .StartAsync(trust.Operator, reauthorizeConnectionId, token),
            cancellationToken);

        var outcome = started.IsSuccess
            ? AuthorizationStartOutcome.Started
            : StartOutcomes.GetValueOrDefault(started.Error.Code, AuthorizationStartOutcome.Other);
        _diagnostics.AuthorizationStart(outcome);
        return new OperatorStart(outcome, started.IsSuccess ? started.Value.AuthorizationUri : null);
    }

    /// <inheritdoc />
    public async Task<AuthorizationCompletionOutcome> CompleteAsync(MondayAuthorizationCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var completed = await _flow.RunAsync(
            (services, trust, token) => services.GetRequiredService<MondayConnectionAuthorizer>()
                .CompleteAsync(trust.Operator, callback, token),
            cancellationToken);

        var outcome = completed.IsSuccess
            ? AuthorizationCompletionOutcome.Completed
            : CompletionOutcomes.GetValueOrDefault(completed.Error.Code, AuthorizationCompletionOutcome.Other);
        _diagnostics.AuthorizationCompletion(outcome);
        return outcome;
    }

    /// <summary>Disconnects a connection of the trusted tenant.</summary>
    public Task<Result<MondayDisconnection>> DisconnectAsync(Guid connectionId, CancellationToken cancellationToken) =>
        _flow.RunAsync(
            (services, trust, token) => services.GetRequiredService<MondayConnectionAuthorizer>()
                .DisconnectAsync(trust.Operator, connectionId, token),
            cancellationToken);

    /// <summary>Configures one explicit source scope of a connection of the trusted tenant: the resource id is the operator's, never discovered.</summary>
    public Task<Result<ConnectorSourceScope>> ConfigureScopeAsync(Guid connectionId, string resourceId, bool enabled, CancellationToken cancellationToken) =>
        _flow.RunAsync(
            async (services, trust, token) =>
            {
                ConnectorSourceScope scope;
                try
                {
                    scope = new ConnectorSourceScope(trust.Tenant, connectionId, MondayConnectionAuthorizer.SourceSystem, resourceId, enabled);
                }
                catch (ArgumentException)
                {
                    return Result<ConnectorSourceScope>.Failure(HarnessErrors.InvalidInput);
                }

                return await services.GetRequiredService<IConnectorSourceScopes>().ConfigureAsync(scope, token);
            },
            cancellationToken);

    /// <summary>Starts one initial-sync generation for a connection of the trusted tenant.</summary>
    public Task<Result<InitialSyncSummary>> StartInitialSyncAsync(Guid connectionId, CancellationToken cancellationToken) =>
        SyncAsync(connectionId, (sync, run, token) => sync.StartAsync(run, token), cancellationToken);

    /// <summary>Advances the current initial-sync generation of a connection of the trusted tenant, once.</summary>
    public Task<Result<InitialSyncSummary>> RunInitialSyncAsync(Guid connectionId, CancellationToken cancellationToken) =>
        SyncAsync(connectionId, (sync, run, token) => sync.RunAsync(run, token), cancellationToken);

    /// <summary>Runs one bounded reconciliation of a connection of the trusted tenant.</summary>
    public Task<Result<ReconciliationRunSummary>> ReconcileAsync(Guid connectionId, CancellationToken cancellationToken) =>
        _flow.RunAsync(
            async (services, trust, token) =>
            {
                if (connectionId == Guid.Empty)
                {
                    return Result<ReconciliationRunSummary>.Failure(HarnessErrors.InvalidInput);
                }

                var run = new ReconciliationRun(trust.Tenant, MondayConnectionAuthorizer.SourceSystem, connectionId, MaxTargetsPerAction);
                return await services.GetRequiredService<ISourceReconciler>().RunAsync(run, token);
            },
            cancellationToken);

    /// <summary>Requests one bounded sweep of a connection of the trusted tenant: a reread of targets already reconciled once.</summary>
    public Task<Result<int>> RequestSweepAsync(Guid connectionId, CancellationToken cancellationToken) =>
        _flow.RunAsync(
            async (services, trust, token) =>
            {
                if (connectionId == Guid.Empty)
                {
                    return Result<int>.Failure(HarnessErrors.InvalidInput);
                }

                var run = new ReconciliationRun(trust.Tenant, MondayConnectionAuthorizer.SourceSystem, connectionId, MaxTargetsPerAction);
                return await services.GetRequiredService<ISourceReconciler>().RequestSweepAsync(run, token);
            },
            cancellationToken);

    /// <summary>Runs one PKCE verifier cleanup for the trusted tenant.</summary>
    public Task<Result<MondayVerifierCleanup>> CleanUpVerifiersAsync(CancellationToken cancellationToken) =>
        _flow.RunAsync(
            (services, _, token) => services.GetRequiredService<MondayConnectionAuthorizer>().CleanUpVerifiersAsync(token),
            cancellationToken);

    /// <summary>Reads the trusted tenant's current work through the active views.</summary>
    public Task<Result<CurrentWork>> ReadCurrentWorkAsync(CancellationToken cancellationToken) =>
        _flow.RunAsync(
            (services, _, token) => services.GetRequiredService<CurrentWorkReader>().ReadAsync(token),
            cancellationToken);

    /// <summary>Reads the trusted tenant's operational metadata.</summary>
    public Task<Result<OperationalEvidence>> ReadEvidenceAsync(CancellationToken cancellationToken) =>
        _flow.RunAsync(
            (services, _, token) => services.GetRequiredService<OperationalEvidenceReader>().ReadAsync(token),
            cancellationToken);

    private Task<Result<InitialSyncSummary>> SyncAsync(
        Guid connectionId,
        Func<IInitialSync, InitialSyncRun, CancellationToken, Task<Result<InitialSyncSummary>>> step,
        CancellationToken cancellationToken) =>
        _flow.RunAsync(
            async (services, trust, token) =>
            {
                if (connectionId == Guid.Empty)
                {
                    return Result<InitialSyncSummary>.Failure(HarnessErrors.InvalidInput);
                }

                var run = new InitialSyncRun(trust.Tenant, MondayConnectionAuthorizer.SourceSystem, connectionId);
                return await step(services.GetRequiredService<IInitialSync>(), run, token);
            },
            cancellationToken);
}
