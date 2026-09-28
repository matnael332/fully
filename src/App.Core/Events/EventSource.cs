namespace WorkContinuity.Core.Events;

/// <summary>
/// The kind of source a canonical event came from.
/// </summary>
/// <remarks>
/// The set is closed and deliberately has no member for an AI model, an inference or the product's own
/// outbound intent. Authority is resolved from this kind, so a value that does not exist here can never
/// be treated as authoritative: AI output travels on a separate path from fact events (invariant 11),
/// and an intent to mutate becomes a fact only when the authoritative source itself reports the change
/// (invariant 15).
/// </remarks>
public enum EventSourceKind
{
    /// <summary>Unspecified. Never valid.</summary>
    None = 0,

    /// <summary>An external work system reached through its connector, for example a task system.</summary>
    SaasConnector = 1,

    /// <summary>The Windows Local Agent, reporting permitted activity metadata.</summary>
    LocalAgent = 2,

    /// <summary>The employee, for example approving or rejecting in the product.</summary>
    Employee = 3,

    /// <summary>The organization's identity system.</summary>
    IdentityProvider = 4,

    /// <summary>The destination or executor confirming the result of an action.</summary>
    ActionExecutor = 5,
}

/// <summary>
/// Where a canonical event came from: the kind of source, its system, and the registered channel
/// (a connector connection, an enrolled agent instance) it arrived through.
/// </summary>
/// <remarks>
/// Construction only guards against null. Whether the parts are well formed is decided by
/// <see cref="CanonicalEvent.Create"/>, which turns malformed provider data into a quarantine reason
/// instead of an exception.
/// </remarks>
public sealed record EventSource
{
    /// <summary>Initializes a new instance of the <see cref="EventSource"/> class.</summary>
    /// <exception cref="ArgumentNullException">A text component is null.</exception>
    public EventSource(EventSourceKind kind, string sourceSystem, string connectionReference)
    {
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(connectionReference);

        Kind = kind;
        SourceSystem = sourceSystem;
        ConnectionReference = connectionReference;
    }

    /// <summary>Gets the kind of source.</summary>
    public EventSourceKind Kind { get; }

    /// <summary>Gets the source system token, for example "monday".</summary>
    public string SourceSystem { get; }

    /// <summary>Gets the registered channel the event arrived through.</summary>
    public string ConnectionReference { get; }

    /// <summary>Gets a value indicating whether every part is well formed.</summary>
    internal bool IsWellFormed =>
        Kind != EventSourceKind.None
        && Enum.IsDefined(Kind)
        && EventText.IsToken(SourceSystem)
        && EventText.IsIdentifier(ConnectionReference);
}
