using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkContinuity.Core.Correlation;
using WorkContinuity.Core.Ingress;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Core.Time;
using WorkContinuity.Infrastructure.Ingress;
using WorkContinuity.Infrastructure.Persistence;
using WorkContinuity.Integration.Tests.Database;

namespace WorkContinuity.Integration.Tests.Ingress;

/// <summary>
/// The deterministic synthetic source approved for Gate 2 tests (owner decision 3). Nothing here is
/// source-specific: the proof is an HMAC of the connection id and the payload under a fixed key that
/// exists only in tests. Binding the proof to the connection models what every real verifier must do.
/// </summary>
internal static class SyntheticSource
{
    /// <summary>The synthetic source system token.</summary>
    public const string System = "synthetic";

    /// <summary>The synthetic payload schema version.</summary>
    public const string SchemaVersion = "1";

    // Test-only signing material. It protects nothing and is not a credential.
    private static readonly byte[] SigningKey = Encoding.UTF8.GetBytes("synthetic-source-test-key");

    /// <summary>Computes the proof the synthetic source attaches to a payload on one connection.</summary>
    public static string Sign(Guid connectionId, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            SigningKey,
            Encoding.UTF8.GetBytes(connectionId.ToString("D") + ":" + payload)));

    /// <summary>A signed synthetic delivery.</summary>
    public static IngressSubmission Delivery(
        Guid connectionId,
        string? sourceEventId,
        string payload,
        CorrelationId? correlationId = null,
        string? proof = null,
        string sourceSystem = System,
        string schemaVersion = SchemaVersion) =>
        new(
            connectionId,
            sourceSystem,
            sourceEventId,
            schemaVersion,
            payload,
            correlationId ?? CorrelationId.New(),
            proof ?? Sign(connectionId, payload));
}

/// <summary>
/// Verifies the synthetic source's HMAC proof in constant time. The proof already binds the connection and the payload,
/// and the synthetic source records nothing more about a connection, so there is nothing further to confirm.
/// </summary>
internal sealed class SyntheticSourceVerifier : ISourceVerifier
{
    public string SourceSystem => SyntheticSource.System;

    public ValueTask<bool> VerifyAsync(IngressSubmission submission, CancellationToken cancellationToken)
    {
        if (submission.Proof is null)
        {
            return ValueTask.FromResult(false);
        }

        var expected = Encoding.ASCII.GetBytes(SyntheticSource.Sign(submission.ConnectionId, submission.Payload));
        var presented = Encoding.ASCII.GetBytes(submission.Proof);
        return ValueTask.FromResult(CryptographicOperations.FixedTimeEquals(expected, presented));
    }

    public ValueTask<SourceConfirmation> ConfirmAsync(IngressSubmission submission, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SourceConfirmation.Confirmed);
}

/// <summary>A verifier that accepts and confirms everything for its source system, to isolate other checks.</summary>
internal sealed class AcceptingVerifier(string sourceSystem) : ISourceVerifier
{
    public string SourceSystem { get; } = sourceSystem;

    public ValueTask<bool> VerifyAsync(IngressSubmission submission, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    public ValueTask<SourceConfirmation> ConfirmAsync(IngressSubmission submission, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SourceConfirmation.Confirmed);
}

/// <summary>A verifier that fails with a message quoting the proof, as a careless one might.</summary>
internal sealed class ThrowingVerifier : ISourceVerifier
{
    public string SourceSystem => SyntheticSource.System;

    public ValueTask<bool> VerifyAsync(IngressSubmission submission, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The verifier could not check proof " + submission.Proof);

    public ValueTask<SourceConfirmation> ConfirmAsync(IngressSubmission submission, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The verifier could not confirm proof " + submission.Proof);
}

/// <summary>A clock frozen at one instant.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>One captured log record, with everything a log sink would emit for it.</summary>
internal sealed record CapturedLog(string Category, LogLevel Level, string Message, string State, string? Exception)
{
    /// <summary>Gets the message, the structured values and the exception text together.</summary>
    public string Text => string.Join('\n', Message, State, Exception ?? string.Empty);
}

/// <summary>Captures every log record at every level, including Entity Framework Core's.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyList<CapturedLog> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
        // Nothing to release.
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join('\n', pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : state?.ToString() ?? string.Empty;

            entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), values, exception?.ToString()));
        }
    }
}

/// <summary>
/// The real ingress, composed exactly as a host composes it: persistence bound to the runtime role,
/// the flow-scoped tenant accessor, a frozen clock, the given verifiers, and a capturing log provider.
/// </summary>
internal sealed class IngressHost : IAsyncDisposable
{
    /// <summary>The instant the frozen clock reports.</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    public IngressHost(DatabaseFixture fixture, params ISourceVerifier[] verifiers)
        : this(fixture.ApplicationConnectionString, verifiers)
    {
    }

    private IngressHost(string connectionString, ISourceVerifier[] verifiers)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        foreach (var verifier in verifiers)
        {
            services.AddSingleton(verifier);
        }

        services.AddPersistence(configuration);
        services.AddEventIngress();

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    /// <summary>Gets every log record written so far.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Gets the flow-scoped tenant accessor the ingress uses.</summary>
    public ITenantContextAccessor TenantContextAccessor =>
        _provider.GetRequiredService<ITenantContextAccessor>();

    /// <summary>The real ingress composed over a database that cannot be reached.</summary>
    public static IngressHost Unreachable(params ISourceVerifier[] verifiers) =>
        new("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=2", verifiers);

    /// <summary>Submits one delivery in its own dependency-injection scope, as one request would.</summary>
    public async Task<IngressOutcome> SubmitAsync(IngressSubmission submission)
    {
        await using var scope = _provider.CreateAsyncScope();
        var ingress = scope.ServiceProvider.GetRequiredService<IEventIngress>();
        return await ingress.SubmitAsync(submission, TestContext.Current.CancellationToken);
    }

    /// <summary>Checks a connection in its own dependency-injection scope, as one challenge request would (Gate 3C).</summary>
    public async Task<IngressConnectionStatus> CheckAsync(Guid connectionId, string sourceSystem)
    {
        await using var scope = _provider.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<IIngressConnectionCheck>();
        return await check.CheckAsync(connectionId, sourceSystem, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
