using WorkContinuity.Core.Events;

namespace WorkContinuity.Core.Connectors;

/// <summary>
/// What a connector operation does to the outside world, in the action vocabulary of PRD v1 §7.
/// </summary>
public enum ConnectorActionType
{
    /// <summary>Unspecified. Never valid.</summary>
    Unspecified = 0,

    /// <summary>Reads authorized provider data and changes nothing.</summary>
    Read = 1,

    /// <summary>Prepares something without applying it externally.</summary>
    Prepare = 2,

    /// <summary>Changes provider data.</summary>
    Change = 3,

    /// <summary>Sends something to someone outside the product.</summary>
    Send = 4,
}

/// <summary>The risk level of a connector operation, in the vocabulary of PRD v1 §7.</summary>
public enum ConnectorRiskLevel
{
    /// <summary>Unspecified. Never valid.</summary>
    Unspecified = 0,

    /// <summary>Low risk.</summary>
    Low = 1,

    /// <summary>Medium risk.</summary>
    Medium = 2,

    /// <summary>High risk.</summary>
    High = 3,
}

/// <summary>The side effects an operation may have.</summary>
public enum ConnectorSideEffect
{
    /// <summary>Unspecified. Never valid.</summary>
    Unspecified = 0,

    /// <summary>No effect anywhere: the operation only reads.</summary>
    None = 1,

    /// <summary>An effect inside the product only.</summary>
    Internal = 2,

    /// <summary>An external effect that can be rolled back.</summary>
    ExternalReversible = 3,

    /// <summary>An external effect that can be compensated but not rolled back.</summary>
    ExternalCompensable = 4,

    /// <summary>An external effect that can be neither rolled back nor compensated.</summary>
    ExternalIrreversible = 5,
}

/// <summary>How an operation's effect is recovered if it must be undone.</summary>
public enum ConnectorRecovery
{
    /// <summary>Unspecified. Never valid.</summary>
    Unspecified = 0,

    /// <summary>Nothing to recover: the operation has no external effect.</summary>
    NotRequired = 1,

    /// <summary>A verified rollback.</summary>
    Rollback = 2,

    /// <summary>A verified compensating operation.</summary>
    Compensation = 3,

    /// <summary>The effect cannot be recovered.</summary>
    Irreversible = 4,
}

/// <summary>
/// The declared contract of one typed connector operation.
/// </summary>
/// <remarks>
/// <para>
/// Every field Premium architecture §20 requires of a connector operation is mandatory here: purpose, input and output
/// schema, action type, risk, resource scope, side-effect and recovery classification, timeout and retry policy. A
/// later layer can therefore wrap an operation (policy checks, Tools, audit) from its descriptor alone, without a new
/// connector contract. An incomplete or inconsistent descriptor cannot be constructed.
/// </para>
/// <para>
/// Only a read-only operation may be retried: an operation with any effect gets exactly one attempt until an
/// idempotency rule for it is decided, so no effect can be duplicated by a retry.
/// </para>
/// </remarks>
public sealed record ConnectorOperationDescriptor
{
    /// <summary>The maximum length of an operation's purpose.</summary>
    public const int MaxPurposeLength = 200;

    /// <summary>The longest timeout an operation may declare.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Declares an operation.</summary>
    /// <param name="name">The operation's stable name, a token such as <c>provider.items.read-by-id</c>.</param>
    /// <param name="purpose">One sentence saying what the operation is for.</param>
    /// <param name="inputSchema">The token naming the input schema and its version.</param>
    /// <param name="outputSchema">The token naming the output schema and its version.</param>
    /// <param name="actionType">What the operation does to the outside world.</param>
    /// <param name="risk">The operation's risk level.</param>
    /// <param name="resourceScope">The token naming the kind of provider resource the operation addresses.</param>
    /// <param name="sideEffect">The operation's side effects.</param>
    /// <param name="recovery">How the operation's effect is recovered.</param>
    /// <param name="timeout">The limit on one attempt.</param>
    /// <param name="retryPolicy">The retry policy for transient failures.</param>
    /// <exception cref="ArgumentException">A field is missing, malformed or inconsistent with another.</exception>
    public ConnectorOperationDescriptor(
        string name,
        string purpose,
        string inputSchema,
        string outputSchema,
        ConnectorActionType actionType,
        ConnectorRiskLevel risk,
        string resourceScope,
        ConnectorSideEffect sideEffect,
        ConnectorRecovery recovery,
        TimeSpan timeout,
        ConnectorRetryPolicy retryPolicy)
    {
        RequireToken(name, nameof(name));
        RequireToken(inputSchema, nameof(inputSchema));
        RequireToken(outputSchema, nameof(outputSchema));
        RequireToken(resourceScope, nameof(resourceScope));
        RequirePurpose(purpose);
        RequireDefined(actionType, ConnectorActionType.Unspecified, nameof(actionType));
        RequireDefined(risk, ConnectorRiskLevel.Unspecified, nameof(risk));
        RequireDefined(sideEffect, ConnectorSideEffect.Unspecified, nameof(sideEffect));
        RequireDefined(recovery, ConnectorRecovery.Unspecified, nameof(recovery));
        ArgumentNullException.ThrowIfNull(retryPolicy);

        if (timeout <= TimeSpan.Zero || timeout > MaxTimeout)
        {
            throw new ArgumentException(
                $"An operation's timeout must be positive and at most {MaxTimeout.TotalMinutes} minutes.",
                nameof(timeout));
        }

        if (actionType == ConnectorActionType.Read && sideEffect != ConnectorSideEffect.None)
        {
            throw new ArgumentException("A read operation cannot have a side effect.", nameof(sideEffect));
        }

        if ((sideEffect == ConnectorSideEffect.None) != (recovery == ConnectorRecovery.NotRequired))
        {
            throw new ArgumentException(
                "Recovery is not required exactly when the operation has no side effect.",
                nameof(recovery));
        }

        var readOnly = actionType == ConnectorActionType.Read && sideEffect == ConnectorSideEffect.None;
        if (!readOnly && retryPolicy.MaxAttempts > 1)
        {
            throw new ArgumentException(
                "Only a read-only operation may be retried; an operation with an effect gets one attempt.",
                nameof(retryPolicy));
        }

        Name = name;
        Purpose = purpose;
        InputSchema = inputSchema;
        OutputSchema = outputSchema;
        ActionType = actionType;
        Risk = risk;
        ResourceScope = resourceScope;
        SideEffect = sideEffect;
        Recovery = recovery;
        Timeout = timeout;
        RetryPolicy = retryPolicy;
    }

    /// <summary>Gets the operation's stable name.</summary>
    public string Name { get; }

    /// <summary>Gets what the operation is for.</summary>
    public string Purpose { get; }

    /// <summary>Gets the token naming the input schema and its version.</summary>
    public string InputSchema { get; }

    /// <summary>Gets the token naming the output schema and its version.</summary>
    public string OutputSchema { get; }

    /// <summary>Gets what the operation does to the outside world.</summary>
    public ConnectorActionType ActionType { get; }

    /// <summary>Gets the operation's risk level.</summary>
    public ConnectorRiskLevel Risk { get; }

    /// <summary>Gets the kind of provider resource the operation addresses.</summary>
    public string ResourceScope { get; }

    /// <summary>Gets the operation's side effects.</summary>
    public ConnectorSideEffect SideEffect { get; }

    /// <summary>Gets how the operation's effect is recovered.</summary>
    public ConnectorRecovery Recovery { get; }

    /// <summary>Gets the limit on one attempt.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets the retry policy for transient failures.</summary>
    public ConnectorRetryPolicy RetryPolicy { get; }

    /// <summary>Gets a value indicating whether the operation only reads and changes nothing anywhere.</summary>
    public bool IsReadOnly =>
        ActionType == ConnectorActionType.Read
        && SideEffect == ConnectorSideEffect.None
        && Recovery == ConnectorRecovery.NotRequired;

    /// <summary>Identifies the operation.</summary>
    public override string ToString() => $"ConnectorOperation({Name})";

    private static void RequireToken(string value, string parameterName)
    {
        if (!EventText.IsToken(value))
        {
            throw new ArgumentException(
                "The value must be a token of 1-64 lowercase letters, digits, '-', '_' or '.'.",
                parameterName);
        }
    }

    private static void RequirePurpose(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > MaxPurposeLength)
        {
            throw new ArgumentException(
                $"An operation's purpose is required and must be at most {MaxPurposeLength} characters.",
                nameof(purpose));
        }

        foreach (var character in purpose)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("An operation's purpose must be a single line.", nameof(purpose));
            }
        }
    }

    private static void RequireDefined<TEnum>(TEnum value, TEnum unspecified, string parameterName)
        where TEnum : struct, Enum
    {
        if (EqualityComparer<TEnum>.Default.Equals(value, unspecified) || !Enum.IsDefined(value))
        {
            throw new ArgumentException("The value must be a defined, specified member.", parameterName);
        }
    }
}
