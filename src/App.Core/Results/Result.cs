namespace WorkContinuity.Core.Results;

/// <summary>
/// The outcome of an operation that returns no value.
/// </summary>
public readonly record struct Result
{
    private readonly Error? _error;

    private Result(Error? error) => _error = error;

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess => _error is null;

    /// <summary>Gets a value indicating whether the operation failed.</summary>
    public bool IsFailure => _error is not null;

    /// <summary>Gets the failure.</summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public Error Error => _error
        ?? throw new InvalidOperationException("A successful result has no error.");

    /// <summary>Creates a successful result.</summary>
    public static Result Success() => new(error: null);

    /// <summary>Creates a failed result.</summary>
    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }
}

/// <summary>
/// The outcome of an operation that returns a value on success.
/// </summary>
/// <typeparam name="TValue">The success value type.</typeparam>
public readonly record struct Result<TValue>
{
    private readonly TValue? _value;
    private readonly Error? _error;

    private Result(TValue? value, Error? error)
    {
        _value = value;
        _error = error;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess => _error is null;

    /// <summary>Gets a value indicating whether the operation failed.</summary>
    public bool IsFailure => _error is not null;

    /// <summary>Gets the success value.</summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    /// <summary>Gets the failure.</summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public Error Error => _error
        ?? throw new InvalidOperationException("A successful result has no error.");

    /// <summary>Creates a successful result.</summary>
    public static Result<TValue> Success(TValue value) => new(value, error: null);

    /// <summary>Creates a failed result.</summary>
    public static Result<TValue> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<TValue>(value: default, error);
    }
}
