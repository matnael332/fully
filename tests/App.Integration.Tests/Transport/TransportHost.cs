using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Core.Transport;
using WorkContinuity.Infrastructure.Ingress;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Infrastructure.Transport;
using WorkContinuity.Integration.Tests.Database;
using WorkContinuity.Integration.Tests.Ingress;

namespace WorkContinuity.Integration.Tests.Transport;

/// <summary>What a handler saw for one message, and the tenant scope it ran in.</summary>
internal sealed record HandledMessage(
    TransportEnvelope Envelope,
    bool TenantResolved,
    TenantResolutionSource Source,
    TenantId Tenant);

/// <summary>
/// A downstream handler that records every message, the tenant scope it was handed in and its attempt
/// number, and answers as the test tells it to. Standing in for Gate 2D processing.
/// </summary>
internal sealed class RecordingHandler(ITenantContextAccessor tenantContextAccessor) : ITransportMessageHandler
{
    private readonly ConcurrentQueue<HandledMessage> _handled = new();
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();

    /// <summary>Gets or sets the answer for a message on its n-th attempt.</summary>
    public Func<TransportEnvelope, int, TransportHandlingResult> Respond { get; set; } =
        (_, _) => TransportHandlingResult.Succeeded;

    /// <summary>Gets everything handled, in order.</summary>
    public IReadOnlyList<HandledMessage> Handled => [.. _handled];

    /// <summary>How many times a message reached the handler.</summary>
    public int AttemptsFor(Guid outboxMessageId) => _attempts.GetValueOrDefault(outboxMessageId);

    public Task<TransportHandlingResult> HandleAsync(TransportEnvelope envelope, CancellationToken cancellationToken)
    {
        var context = tenantContextAccessor.Current;
        context.TryGetTenantId(out var tenant);
        _handled.Enqueue(new HandledMessage(envelope, context.IsResolved, context.Source, tenant));

        var attempt = _attempts.AddOrUpdate(envelope.OutboxMessageId, 1, (_, count) => count + 1);
        return Task.FromResult(Respond(envelope, attempt));
    }
}

/// <summary>
/// The real ingress and event transport, composed exactly as a host composes them: persistence bound to the
/// runtime role, the flow-scoped tenant accessor, a frozen clock, the synthetic source verifier, a recording
/// downstream handler and a capturing log provider, over either the in-memory broker or Service Bus.
/// </summary>
internal sealed class TransportHost : IAsyncDisposable
{
    /// <summary>A connection string for hosts that never touch the database; nothing listens there.</summary>
    public const string UnusedDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused";

    private readonly ServiceProvider _provider;

    private TransportHost(ServiceProvider provider, CapturingLoggerProvider logs)
    {
        _provider = provider;
        Logs = logs;
    }

    /// <summary>Gets every log record written so far.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>Gets the composed services, for tests that open their own scopes.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>Gets the recording downstream handler.</summary>
    public RecordingHandler Handler => _provider.GetRequiredService<RecordingHandler>();

    /// <summary>Gets the outbox relay.</summary>
    public IOutboxRelay Relay => _provider.GetRequiredService<IOutboxRelay>();

    /// <summary>Gets the transport consumer.</summary>
    public IEventTransportConsumer Consumer => _provider.GetRequiredService<IEventTransportConsumer>();

    /// <summary>Gets the publisher the relay uses.</summary>
    public IEventTransportPublisher Publisher => _provider.GetRequiredService<IEventTransportPublisher>();

    /// <summary>Gets the flow-scoped tenant accessor.</summary>
    public ITenantContextAccessor TenantContextAccessor => _provider.GetRequiredService<ITenantContextAccessor>();

    /// <summary>Gets the Service Bus dead-letter queue (Service Bus hosts only).</summary>
    public IDeadLetterQueue ServiceBusDeadLetters => _provider.GetRequiredService<IDeadLetterQueue>();

    /// <summary>Gets the Service Bus consumer host (Service Bus hosts only).</summary>
    public ServiceBusEventConsumerHost ConsumerHost => _provider.GetRequiredService<ServiceBusEventConsumerHost>();

    /// <summary>A host over the in-memory broker.</summary>
    public static TransportHost InMemory(
        DatabaseFixture? fixture,
        InMemoryTransport transport,
        Action<EventTransportOptions>? configure = null,
        Action<IServiceCollection>? handlers = null) =>
        Create(fixture, configure, handlers, serviceBusConnectionString: null, (services, _) =>
            services.AddSingleton<IEventTransportPublisher>(transport));

    /// <summary>A host over Service Bus, here the local emulator.</summary>
    public static TransportHost ServiceBus(
        DatabaseFixture? fixture,
        string connectionString,
        Action<EventTransportOptions>? configure = null,
        Action<IServiceCollection>? handlers = null) =>
        Create(fixture, configure, handlers, connectionString, (services, configuration) =>
            services.AddServiceBusEventTransport(configuration));

    /// <summary>Submits one delivery in its own dependency-injection scope, as one request would.</summary>
    public async Task<IngressOutcome> SubmitAsync(IngressSubmission submission)
    {
        await using var scope = _provider.CreateAsyncScope();
        var ingress = scope.ServiceProvider.GetRequiredService<IEventIngress>();
        return await ingress.SubmitAsync(submission, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs relay passes until every named outbox message is published. Older pending work from other tests is
    /// relayed first, so this may take several passes; it fails rather than loop without bound.
    /// </summary>
    public async Task<IReadOnlyList<OutboxRelayPass>> RelayUntilPublishedAsync(
        DatabaseFixture fixture,
        IReadOnlyCollection<Guid> outboxMessageIds,
        CancellationToken cancellationToken = default,
        int maxPasses = 100)
    {
        var passes = new List<OutboxRelayPass>();
        for (var pass = 0; pass < maxPasses; pass++)
        {
            if (await TransportData.AllPublishedAsync(fixture, outboxMessageIds, cancellationToken))
            {
                return passes;
            }

            passes.Add(await Relay.RelayPendingAsync(cancellationToken));
        }

        Assert.Fail($"The outbox messages were not all published within {maxPasses} relay passes.");
        return passes;
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    private static TransportHost Create(
        DatabaseFixture? fixture,
        Action<EventTransportOptions>? configure,
        Action<IServiceCollection>? handlers,
        string? serviceBusConnectionString,
        Action<IServiceCollection, IConfiguration> adapter)
    {
        var logs = new CapturingLoggerProvider();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = fixture?.ApplicationConnectionString ?? UnusedDatabase,
        };
        if (serviceBusConnectionString is not null)
        {
            settings["ServiceBus:ConnectionString"] = serviceBusConnectionString;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<IClock>(new FixedClock(IngressHost.Now));
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.AddSingleton<ISourceVerifier>(new SyntheticSourceVerifier());
        services.AddPersistence(configuration);
        services.AddEventIngress();
        services.AddEventTransport(configure);
        adapter(services, configuration);

        services.AddSingleton<RecordingHandler>();
        if (handlers is null)
        {
            services.AddSingleton<ITransportMessageHandler>(provider => provider.GetRequiredService<RecordingHandler>());
        }
        else
        {
            handlers(services);
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new TransportHost(provider, logs);
    }
}
