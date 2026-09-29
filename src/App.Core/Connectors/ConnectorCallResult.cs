namespace WorkContinuity.Core.Connectors;

/// <summary>
/// The outcome of one connector call: its value or its failure, the operation called, the identity whose authority it
/// used and the number of provider requests it made.
/// </summary>
/// <remarks>
/// Every provider call records whose authority it used (Premium architecture §20), so a successful result always
/// carries the identity. A failed result carries it too once a credential was acquired; it is absent only when the call
/// failed before any credential existed, for example because the connection was disabled.
/// </remarks>
/// <typeparam name="TValue">The success value type.</typeparam>
public sealed class ConnectorCallResult<TValue>
{
    private readonly TValue? _value;
    private readonly ConnectorFailure? _failure;

    private ConnectorCallResult(
        ConnectorOperationDescriptor operation,
        ConnectorIdentity? identity,
        int attempts,
        TValue? value,
        ConnectorFailure? failure)
    {
        Operation = operation;
        Identity = identity;
        Attempts = attempts;
        _value = value;
        _failure = failure;
    }

    /// <summary>Gets the operation called.</summary>
    public ConnectorOperationDescriptor Operation { get; }

    /// <summary>Gets the identity whose authority the call used, when a credential was acquired.</summary>
    public ConnectorIdentity? Identity { get; }

    /// <summary>Gets the number of requests sent to the provider.</summary>
    public int Attempts { get; }

    /// <summary>Gets a value indicating whether the call succeeded.</summary>
    public bool IsSuccess => _failure is null;

    /// <summary>Gets the success value.</summary>
    /// <exception cref="InvalidOperationException">The call failed.</exception>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed call has no value.");

    /// <summary>Gets the failure.</summary>
    /// <exception cref="InvalidOperationException">The call succeeded.</exception>
    public ConnectorFailure Failure => _failure
        ?? throw new InvalidOperationException("A successful call has no failure.");

    /// <summary>Creates a successful result.</summary>
    /// <exception cref="ArgumentException">A component is missing or out of range.</exception>
    public static ConnectorCallResult<TValue> Succeeded(
        ConnectorOperationDescriptor operation,
        ConnectorIdentity identity,
        int attempts,
        TValue value)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        return new ConnectorCallResult<TValue>(operation, identity, attempts, value, failure: null);
    }

    /// <summary>Creates a failed result.</summary>
    /// <exception cref="ArgumentException">A component is missing or out of range.</exception>
    public static ConnectorCallResult<TValue> Failed(
        ConnectorOperationDescriptor operation,
        ConnectorIdentity? identity,
        int attempts,
        ConnectorFailure failure)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 0);

        return new ConnectorCallResult<TValue>(operation, identity, attempts, value: default, failure);
    }

    /// <summary>Identifies the result without any value.</summary>
    public override string ToString() => IsSuccess
        ? $"ConnectorCallResult({Operation.Name}, succeeded, {Attempts} attempts)"
        : $"ConnectorCallResult({Operation.Name}, {Failure.Error.Code}, {Attempts} attempts)";
}
