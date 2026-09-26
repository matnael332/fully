namespace WorkContinuity.Core.Correlation;

/// <summary>
/// The correlation state of the current logical operation.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>Gets the correlation identifier for this operation.</summary>
    CorrelationId CorrelationId { get; }

    /// <summary>
    /// Gets a value indicating whether the identifier was propagated from an inbound caller
    /// rather than generated here.
    /// </summary>
    bool WasPropagated { get; }
}

/// <summary>
/// Immutable <see cref="ICorrelationContext"/> implementation. This type carries no transport
/// concerns, so the same primitive serves HTTP, the worker, the broker, AI calls, action dispatch
/// and agent commands without coupling Core to any SDK.
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    private CorrelationContext(CorrelationId correlationId, bool wasPropagated)
    {
        CorrelationId = correlationId;
        WasPropagated = wasPropagated;
    }

    /// <inheritdoc />
    public CorrelationId CorrelationId { get; }

    /// <inheritdoc />
    public bool WasPropagated { get; }

    /// <summary>Starts a new operation with a freshly generated correlation identifier.</summary>
    public static CorrelationContext Generate() => new(CorrelationId.New(), wasPropagated: false);

    /// <summary>
    /// Continues an inbound operation when the supplied identifier is valid, and otherwise
    /// generates a fresh one. A malformed inbound value is discarded rather than trusted.
    /// </summary>
    public static CorrelationContext FromInbound(string? inboundCorrelationId) =>
        CorrelationId.TryParse(inboundCorrelationId, out var propagated)
            ? new CorrelationContext(propagated, wasPropagated: true)
            : Generate();
}

/// <summary>
/// Provides ambient access to the correlation context of the current logical operation.
/// </summary>
public interface ICorrelationContextAccessor
{
    /// <summary>Gets the current correlation context, or <see langword="null"/> if none started.</summary>
    ICorrelationContext? Current { get; }

    /// <summary>
    /// Begins a logical operation. Unlike tenant context, each operation legitimately starts its
    /// own correlation scope, so this replaces any value in the current scope.
    /// </summary>
    void Start(ICorrelationContext correlationContext);
}

/// <summary>
/// Flow-scoped <see cref="ICorrelationContextAccessor"/> usable from both Web and Worker code.
/// </summary>
public sealed class CorrelationContextAccessor : ICorrelationContextAccessor
{
    private static readonly AsyncLocal<ICorrelationContext?> Scope = new();

    /// <inheritdoc />
    public ICorrelationContext? Current => Scope.Value;

    /// <inheritdoc />
    public void Start(ICorrelationContext correlationContext)
    {
        ArgumentNullException.ThrowIfNull(correlationContext);
        Scope.Value = correlationContext;
    }
}
