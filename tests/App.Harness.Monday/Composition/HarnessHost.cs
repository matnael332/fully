using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Connectors;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.InitialSync;
using WorkContinuity.Core.Processing;
using WorkContinuity.Core.Reconciliation;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Core.Transport;
using WorkContinuity.Harness.Monday.Configuration;
using WorkContinuity.Harness.Monday.Diagnostics;
using WorkContinuity.Harness.Monday.Evidence;
using WorkContinuity.Harness.Monday.Hosting;
using WorkContinuity.Harness.Monday.Operator;
using WorkContinuity.Harness.Monday.Provider;
using WorkContinuity.Harness.Monday.Trust;
using WorkContinuity.Infrastructure.Connectors;
using WorkContinuity.Infrastructure.InitialSync;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Processing;
using WorkContinuity.Infrastructure.Reconciliation;
using WorkContinuity.Infrastructure.Transport;
using WorkContinuity.Web.Connectors;
using WorkContinuity.Web.Platform;

namespace WorkContinuity.Harness.Monday.Composition;

/// <summary>
/// What a composition may be given in place of the harness's fail-closed defaults.
/// </summary>
/// <remarks>
/// The standalone entry point supplies one of these only, the operator listener's certificate it creates for its run
/// (owner decision B-13), so it has no secret storage and no provider transport, and cannot run an authorization or
/// reach a provider (owner decision D3G-3). Only the harness's tests supply the others: in-memory secret storage holding
/// synthetic values, a deterministic fake of the provider's endpoints, a movable clock and timer, an in-memory broker,
/// and a certificate created in memory for the loopback listener. Nothing here is read from configuration, so no setting
/// can turn a default into a live dependency.
/// </remarks>
public sealed class HarnessSeams
{
    /// <summary>Gets no seam at all: not even a certificate, so the harness does not start.</summary>
    public static HarnessSeams None { get; } = new();

    /// <summary>Gets the secret storage; the refusing store when null.</summary>
    public IMondaySecretStore? SecretStore { get; init; }

    /// <summary>Gets the provider transport; the refusing transport when null.</summary>
    public HttpMessageHandler? ProviderTransport { get; init; }

    /// <summary>Gets the clock; the system clock when null.</summary>
    public IClock? Clock { get; init; }

    /// <summary>Gets the source of the cleanup timer's ticks; the system's when null.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Gets the wait between provider attempts; a real timer when null.</summary>
    public IMondayDelay? Delay { get; init; }

    /// <summary>Gets the broker to publish to; the local Service Bus emulator named in configuration when null.</summary>
    public IEventTransportPublisher? TransportPublisher { get; init; }

    /// <summary>Gets the operator listener's certificate. Without one the harness does not start.</summary>
    public X509Certificate2? OperatorCertificate { get; init; }

    /// <summary>Gets where log records go; the console when null.</summary>
    public IHarnessLogSink? LogSink { get; init; }

    /// <summary>Gets whether the relay and the consumer run continuously; otherwise the relay runs one pass when asked.</summary>
    public bool RunBackboneLoops { get; init; }
}

/// <summary>The outcome of starting the harness: the running host, or the closed reason there is none.</summary>
public sealed class HarnessStart
{
    private HarnessStart(HarnessHost? host, HarnessRefusal refusal)
    {
        Host = host;
        Refusal = refusal;
    }

    /// <summary>Gets the running host, when it started.</summary>
    public HarnessHost? Host { get; }

    /// <summary>Gets why it did not start.</summary>
    public HarnessRefusal Refusal { get; }

    internal static HarnessStart Started(HarnessHost host) => new(host, HarnessRefusal.None);

    internal static HarnessStart Refused(HarnessRefusal refusal) => new(null, refusal);
}

/// <summary>
/// The Gate 3G offline harness, composed and running: the existing connector parts behind two loopback listeners and a
/// console, for one configured tenant (owner decision D3G-3).
/// </summary>
/// <remarks>
/// <para>
/// Three boundaries, each with its own services. The worker holds persistence as the runtime role, the relay and the
/// consumer, processing with the monday normalizer, reconciliation and initial sync with the monday readers, and the
/// authorization lifecycle. The webhook listener holds exactly what the Web host's webhook intake registers, and maps
/// the existing endpoint and nothing else. The operator listener holds no service of the other two: it reaches the
/// authorization actions through one interface. The listeners are separate applications, so each route exists on its
/// own listener only, whatever port a request names.
/// </para>
/// <para>
/// Tenants. Operator actions and the verifier cleanup run through <see cref="TrustedFlow"/>, the one place the
/// harness assigns a tenant. The relay and the consumer are started on flows that carry no tenant and resolve each one
/// themselves, exactly as in production; the consumer takes only the configured tenant's session, and the harness
/// touches no dead-letter queue. Webhook requests get their tenant from their connection inside the ingress.
/// </para>
/// <para>
/// What "one configured tenant" covers. The operator actions, the cleanup, the evidence and the consumer act for the
/// configured tenant only. The relay and the webhook intake are the product's own, unchanged, and work for every tenant
/// the database holds: a relay pass publishes whatever outbox rows are pending, and a delivery is stored under its
/// connection's tenant. The harness shows nothing that identifies another tenant: its log passes a product record only
/// when the record is the configured tenant's, and its counters count the process's own traffic without identifiers.
/// </para>
/// <para>
/// Nothing here creates, migrates or provisions a database, and nothing schedules a sync, a reconciliation or a sweep.
/// Both listeners bind the loopback address only.
/// </para>
/// </remarks>
public sealed class HarnessHost : IAsyncDisposable
{
    private const string EnvironmentName = "Harness";
    private const string LogCategory = "WorkContinuity.Harness.Monday.Host";

    private readonly CancellationTokenSource _stopping = new();
    private readonly HarnessDiagnostics _diagnostics;
    private readonly GuardedProviderTransport _providerPath;
    private readonly HttpClient _authorizationHttp;
    private readonly HttpClient _apiHttp;
    private readonly ILogger _logger;
    private ServiceProvider? _worker;
    private WebApplication? _webhook;
    private WebApplication? _operator;
    private OperatorListener? _listener;
    private OperatorAuthorizationGate? _gate;
    private Task _cleanup = Task.CompletedTask;
    private Task _relay = Task.CompletedTask;
    private ServiceBusEventConsumerHost? _consumerHost;
    private Uri? _webhookAddress;
    private OperatorConsole? _console;
    private string? _ownSession;

    private HarnessHost(
        HarnessDiagnostics diagnostics,
        GuardedProviderTransport providerPath,
        HttpClient authorizationHttp,
        HttpClient apiHttp,
        ILoggerFactory loggers)
    {
        _diagnostics = diagnostics;
        _providerPath = providerPath;
        _authorizationHttp = authorizationHttp;
        _apiHttp = apiHttp;
        _logger = loggers.CreateLogger(LogCategory);
    }

    /// <summary>Gets the harness's diagnostics.</summary>
    public HarnessDiagnostics Diagnostics => _diagnostics;

    /// <summary>Gets the operator actions.</summary>
    public OperatorActions Operator => Worker.GetRequiredService<OperatorActions>();

    /// <summary>Gets the operator console.</summary>
    public OperatorConsole Console => _console ?? throw new InvalidOperationException("The harness has not started.");

    /// <summary>Gets the address the webhook listener is bound to, on loopback.</summary>
    public Uri WebhookAddress => _webhookAddress ?? throw new InvalidOperationException("The harness has not started.");

    private ServiceProvider Worker => _worker ?? throw new InvalidOperationException("The harness has not started.");

    /// <summary>The sessions the composed transport consumer is limited to, for the harness's own tests (owner decision B-15).</summary>
    internal IReadOnlyList<string> ConsumerSessions => [.. Worker.GetRequiredService<EventTransportOptions>().ConsumerSessionIds];

    /// <summary>Composes and starts the harness.</summary>
    /// <param name="options">The validated configuration.</param>
    /// <param name="seams">What replaces the fail-closed defaults; none for the standalone entry point.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running host, or why it did not start.</returns>
    public static async Task<HarnessStart> StartAsync(HarnessOptions options, HarnessSeams seams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(seams);

        // The operator listener is TLS only, and no certificate is ever taken from the machine's stores.
        if (seams.OperatorCertificate is not { HasPrivateKey: true } certificate)
        {
            return HarnessStart.Refused(HarnessRefusal.OperatorCertificateUnavailable);
        }

        if (seams.TransportPublisher is null && options.EmulatorConnectionString is null)
        {
            return HarnessStart.Refused(HarnessRefusal.TransportNotTheLocalEmulator);
        }

        var diagnostics = new HarnessDiagnostics();
        var loggers = new HarnessLoggerFactory(
            seams.LogSink ?? new TextWriterLogSink(System.Console.Out), diagnostics, options.Tenant, new TenantContextAccessor());
        var clock = seams.Clock ?? new SystemClock();
        var secrets = seams.SecretStore ?? new RefusingSecretStore();
        var providerPath = ProviderClients.Guard(seams.ProviderTransport, diagnostics);
        var host = new HarnessHost(diagnostics, providerPath, ProviderClients.Create(providerPath), ProviderClients.Create(providerPath), loggers);

        try
        {
            var refusal = await host.ComposeAndStartAsync(options, seams, certificate, loggers, clock, secrets, cancellationToken);
            if (refusal != HarnessRefusal.None)
            {
                await host.DisposeAsync();
                return HarnessStart.Refused(refusal);
            }

            return HarnessStart.Started(host);
        }
        catch (IOException)
        {
            // A listener's address could not be bound.
            await host.DisposeAsync();
            return HarnessStart.Refused(HarnessRefusal.ListenerUnavailable);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Anything else, a broker that cannot be reached for one: a closed refusal, and only the type is logged.
            host._logger.LogError("The harness did not start: {ExceptionType}.", exception.GetType().Name);
            await host.DisposeAsync();
            return HarnessStart.Refused(HarnessRefusal.StartFaulted);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Mints the single-use start capability and returns the address the operator opens. The connection to restore, when
    /// there is one, is the operator's own argument and never a request's.
    /// </summary>
    public string BeginAuthorization(Guid? reauthorizeConnectionId)
    {
        var gate = _gate ?? throw new InvalidOperationException("The harness has not started.");
        var listener = _listener ?? throw new InvalidOperationException("The harness has not started.");
        return listener.StartAddress(gate.MintCapability(reauthorizeConnectionId));
    }

    /// <summary>Runs one relay pass, on a flow that carries no tenant: the relay resolves each tenant itself.</summary>
    public Task<OutboxRelayPass> RelayOnceAsync(CancellationToken cancellationToken)
    {
        var relay = Worker.GetRequiredService<IOutboxRelay>();
        return DetachedFlow.Run(async () =>
        {
            var pass = await relay.RelayPendingAsync(cancellationToken);
            _diagnostics.RelayPass(pass.Published, pass.HasFailures);
            return pass;
        });
    }

    /// <summary>
    /// Hands one message of an in-process broker to the transport consumer, on a flow that carries no tenant. Only a
    /// message of the configured tenant's session is taken: any other is left untouched, and null is returned.
    /// </summary>
    public Task<TransportSettlement?> ConsumeOwnSessionAsync(ReceivedTransportMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_ownSession is null || !string.Equals(message.SessionId, _ownSession, StringComparison.Ordinal))
        {
            return Task.FromResult<TransportSettlement?>(null);
        }

        var consumer = Worker.GetRequiredService<IEventTransportConsumer>();
        return DetachedFlow.Run<TransportSettlement?>(async () => await consumer.ConsumeAsync(message, cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await QuietlyAsync(_cleanup);
        await QuietlyAsync(_relay);

        if (_consumerHost is { } consumerHost)
        {
            _consumerHost = null;
            await QuietlyAsync(consumerHost.StopAsync(CancellationToken.None));
            await consumerHost.DisposeAsync();
        }

        if (_operator is { } operatorApplication)
        {
            _operator = null;
            await QuietlyAsync(operatorApplication.StopAsync(CancellationToken.None));
            await operatorApplication.DisposeAsync();
        }

        if (_webhook is { } webhookApplication)
        {
            _webhook = null;
            await QuietlyAsync(webhookApplication.StopAsync(CancellationToken.None));
            await webhookApplication.DisposeAsync();
        }

        if (_worker is { } worker)
        {
            _worker = null;
            await worker.DisposeAsync();
        }

        _authorizationHttp.Dispose();
        _apiHttp.Dispose();
        _providerPath.Dispose();
        _stopping.Dispose();
    }

    private async Task<HarnessRefusal> ComposeAndStartAsync(
        HarnessOptions options,
        HarnessSeams seams,
        X509Certificate2 certificate,
        HarnessLoggerFactory loggers,
        IClock clock,
        IMondaySecretStore secrets,
        CancellationToken cancellationToken)
    {
        // Only validated values reach the composed services: nothing else of the ambient configuration does.
        var values = new Dictionary<string, string?>
        {
            [HarnessOptions.DatabaseKey] = options.RuntimeConnectionString,
            [HarnessOptions.WebhookBaseAddressKey] = options.WebhookBaseAddress.AbsoluteUri,
            [HarnessOptions.TransportKey] = options.EmulatorConnectionString,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        _ownSession = TransportEnvelope.SessionIdFor(options.Tenant);
        _worker = BuildWorker(options, seams, configuration, loggers, clock, secrets);
        var actions = _worker.GetRequiredService<OperatorActions>();
        var flow = _worker.GetRequiredService<TrustedFlow>();

        // The runtime role only: a role that could step around row-level security never runs the harness.
        try
        {
            var privileged = await flow.RunAsync(
                (services, _, token) => services.GetRequiredService<DatabaseRoleCheck>().IsPrivilegedAsync(token),
                cancellationToken);
            if (privileged.IsFailure)
            {
                return HarnessRefusal.DatabaseUnavailable;
            }

            if (privileged.Value)
            {
                return HarnessRefusal.PrivilegedDatabaseRole;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The database could not be asked. Only the type is logged: a driver's message can name an address.
            _logger.LogError("The database role could not be checked: {ExceptionType}.", exception.GetType().Name);
            return HarnessRefusal.DatabaseUnavailable;
        }

        // The shared local emulator (owner decision B-15): before anything is published or consumed, the configured
        // tenant's own session must be empty, and must be the harness's to take.
        if (Worker.GetService<Azure.Messaging.ServiceBus.ServiceBusClient>() is { } broker)
        {
            var session = await TransportSessionPreflight.CheckAsync(broker, options.Tenant, cancellationToken);
            if (session != HarnessRefusal.None)
            {
                return session;
            }
        }

        // Each listener starts on a flow of its own, so no request can inherit a tenant from whoever started the harness:
        // a webhook request gets its tenant from its connection inside the ingress, and an operator request has none.
        var webhook = BuildWebhookApplication(options, values, loggers, clock, secrets);
        _webhook = webhook;
        await DetachedFlow.Run(() => webhook.StartAsync(cancellationToken));
        _webhookAddress = BoundAddress(webhook);

        _gate = new OperatorAuthorizationGate(clock);
        _listener = new OperatorListener(options, _gate, actions, _diagnostics, loggers);
        var operatorApplication = BuildOperatorApplication(options, certificate, loggers, _listener);
        _operator = operatorApplication;
        await DetachedFlow.Run(() => operatorApplication.StartAsync(cancellationToken));

        _console = new OperatorConsole(actions, BeginAuthorization, RelayOnceAsync, _diagnostics);

        // The PKCE verifier cleanup: once now, before the harness reports that it started, then every five minutes.
        var cleanup = new VerifierCleanupLoop(actions.CleanUpVerifiersAsync, seams.TimeProvider ?? TimeProvider.System, _diagnostics, loggers);
        await cleanup.RunStartupAsync(cancellationToken);
        _cleanup = DetachedFlow.Run(() => cleanup.RunPeriodicallyAsync(_stopping.Token));

        if (seams.RunBackboneLoops)
        {
            await StartBackboneAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Harness started: operator listener on loopback port {OperatorPort}, webhook listener on loopback port {WebhookPort}, continuous loops {Loops}.",
            options.OperatorPort,
            _webhookAddress.Port,
            seams.RunBackboneLoops);
        return HarnessRefusal.None;
    }

    // The relay and the consumer start on flows that carry no tenant, and resolve each tenant themselves.
    private async Task StartBackboneAsync(CancellationToken cancellationToken)
    {
        var relay = Worker.GetRequiredService<IOutboxRelay>();
        _relay = DetachedFlow.Run(() => relay.RunAsync(_stopping.Token));

        if (Worker.GetService<ServiceBusEventConsumerHost>() is { } consumerHost)
        {
            _consumerHost = consumerHost;
            await DetachedFlow.Run(() => consumerHost.StartAsync(cancellationToken));
            _diagnostics.ConsumerStarted();
        }
    }

    private ServiceProvider BuildWorker(
        HarnessOptions options,
        HarnessSeams seams,
        IConfiguration configuration,
        HarnessLoggerFactory loggers,
        IClock clock,
        IMondaySecretStore secrets)
    {
        var delay = seams.Delay ?? new SystemMondayDelay();
        var oauth = new MondayOAuthOptions(options.ClientId, options.RedirectUri);

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggers);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(clock);
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.AddSingleton(_diagnostics);
        services.AddSingleton(new HarnessTrust(options));
        services.AddSingleton<TrustedFlow>();
        services.AddSingleton<OperatorActions>();

        // Persistence as the runtime role, and the Event Backbone exactly as its own registrations compose it.
        services.AddPersistence(configuration);
        services.AddEventTransport(transport =>
            // Only the configured tenant's session: the harness never takes another tenant's messages.
            transport.ConsumerSessionIds.Add(TransportEnvelope.SessionIdFor(options.Tenant)));
        if (seams.TransportPublisher is { } publisher)
        {
            services.AddSingleton(publisher);
        }
        else
        {
            services.AddServiceBusEventTransport(configuration);
        }

        services.AddEventProcessing();
        services.AddSingleton<ISourceEventNormalizer, MondayWebhookNormalizer>();
        services.AddSourceReconciliation();
        services.AddInitialSync();

        // The monday connector: the 3B lifecycle and the 3A read client over the one guarded provider path.
        services.AddScoped<ConnectorAuthorizationStore>();
        services.AddSingleton<IConnectorAuthorizationStore, PerCallAuthorizationStore>();
        services.AddSingleton<IMondayCredentialSource>(provider => new MondayConnectionCredentialSource(
            provider.GetRequiredService<IConnectorAuthorizationStore>(), secrets, oauth, _authorizationHttp, clock, delay));
        services.AddSingleton(provider => new MondayReadClient(
            _apiHttp, provider.GetRequiredService<IMondayCredentialSource>(), clock, delay));
        services.AddSingleton<IAuthoritativeSourceReader>(provider => new MondayStatusReader(provider.GetRequiredService<MondayReadClient>()));
        services.AddSingleton<IInitialSyncReader>(provider => new MondayInitialSyncReader(provider.GetRequiredService<MondayReadClient>()));
        services.AddSingleton(provider => new MondayConnectionAuthorizer(
            provider.GetRequiredService<IConnectorAuthorizationStore>(),
            secrets,
            oauth,
            _authorizationHttp,
            provider.GetRequiredService<MondayReadClient>(),
            provider.GetRequiredService<ITenantContextAccessor>(),
            clock));

        // Evidence: current work through the active views, operational metadata, and the role check.
        services.AddScoped<CurrentWorkReader>();
        services.AddScoped<OperationalEvidenceReader>();
        services.AddScoped<DatabaseRoleCheck>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    // The webhook listener: what the Web host composes for intake, and its one route.
    private WebApplication BuildWebhookApplication(
        HarnessOptions options,
        Dictionary<string, string?> values,
        HarnessLoggerFactory loggers,
        IClock clock,
        IMondaySecretStore secrets)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = EnvironmentName });
        builder.Configuration.AddInMemoryCollection(values);
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, options.WebhookPort);
        });
        builder.Services.AddRoutingCore();
        builder.Services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(loggers));
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        builder.Services.AddPersistence(builder.Configuration);
        builder.Services.AddSingleton(secrets);
        builder.Services.AddMondayWebhookIntake(builder.Configuration);

        var application = builder.Build();
        var observer = new WebhookRequestObserver(_diagnostics, new MondayWebhookOptions(options.WebhookBaseAddress));
        application.Use(observer.InvokeAsync);
        application.UseMiddleware<CorrelationMiddleware>();
        application.UseMiddleware<PlatformExceptionMiddleware>();
        application.MapMondayWebhooks();
        return application;
    }

    // The operator listener: TLS on loopback, the request checks and the three operator routes.
    private static WebApplication BuildOperatorApplication(
        HarnessOptions options,
        X509Certificate2 certificate,
        HarnessLoggerFactory loggers,
        OperatorListener listener)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = EnvironmentName });
        builder.WebHost.UseKestrelCore().UseKestrelHttpsConfiguration().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, options.OperatorPort, listen => listen.UseHttps(certificate));
        });
        builder.Services.AddRoutingCore();
        builder.Services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(loggers));

        var application = builder.Build();
        listener.Configure(application);
        return application;
    }

    private static Uri BoundAddress(WebApplication application)
    {
        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        return addresses is { Count: 1 }
            ? new Uri(addresses.Single(), UriKind.Absolute)
            : throw new InvalidOperationException("The listener reported no single bound address.");
    }

    private static async Task QuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }
    }
}
