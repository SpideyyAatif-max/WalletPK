namespace WalletPK.Common.Results;

/// <summary>
/// The outcome of an operation that can fail for an expected business reason.
/// Expected failures (insufficient balance, invalid input) are returned as values;
/// exceptions are kept for bugs and infrastructure faults.
/// </summary>
public class Result
{
    private static readonly Result _success = new(null);
    private readonly Error? _error;

    protected Result(Error? error)
    {
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    /// <summary>The error of a failed result. Throws if the result is a success.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    public static Result Success() => _success;

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<T> Success<T>(T value) => new(value);

    public static Result<T> Failure<T>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(error);
    }

    public static implicit operator Result(Error error) => Failure(error);

    /// <summary>Runs one of two functions depending on the outcome.</summary>
    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess() : onFailure(Error);
    }

    public override string ToString() => IsSuccess ? "Success" : $"Failure ({Error})";
}

/// <summary>The outcome of an operation that returns a value on success.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T value)
        : base(null)
    {
        _value = value;
    }

    internal Result(Error error)
        : base(error)
    {
    }

    /// <summary>The value of a successful result. Throws if the result is a failure.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error.Code}).");

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);

    /// <summary>Runs one of two functions depending on the outcome.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(Value) : onFailure(Error);
    }

    /// <summary>Transforms the value of a success; a failure passes through unchanged.</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? new Result<TOut>(map(Value)) : new Result<TOut>(Error);
    }

    /// <summary>Chains another operation that can fail; a failure short-circuits.</summary>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(Value) : new Result<TOut>(Error);
    }

    /// <summary>Turns a success into a failure when the value does not satisfy a rule.</summary>
    public Result<T> Ensure(Func<T, bool> predicate, Error error)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(error);
        if (IsFailure)
        {
            return this;
        }

        return predicate(Value) ? this : new Result<T>(error);
    }
}
