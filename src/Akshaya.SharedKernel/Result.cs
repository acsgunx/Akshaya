namespace Akshaya.SharedKernel;

/// <summary>
/// The single failure channel for the whole platform. Broker calls fail constantly and
/// predictably — expired sessions, closed markets, rejected risk — and those are outcomes,
/// not exceptions. Exceptions are reserved for programmer error.
/// </summary>
public readonly record struct Error(
    string Code,
    string Message,
    string? VendorCode = null,
    string? VendorMessage = null,
    IReadOnlyDictionary<string, string>? Context = null)
{
    public override string ToString() =>
        VendorCode is null ? $"{Code}: {Message}" : $"{Code}: {Message} (vendor {VendorCode}: {VendorMessage})";
}

public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = default;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        _value = default;
        Error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    /// <summary>Throws if the result is a failure. Call only after checking <see cref="IsSuccess"/>.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read Value of a failed Result: {Error}");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsSuccess ? Result<TOut>.Success(map(_value!)) : Result<TOut>.Failure(Error);

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind) =>
        IsSuccess ? bind(_value!) : Result<TOut>.Failure(Error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(Error);

    public T ValueOr(T fallback) => IsSuccess ? _value! : fallback;
}

/// <summary>Non-generic companion for operations that return nothing meaningful.</summary>
public readonly struct Result
{
    private Result(Error error)
    {
        Error = error;
        IsSuccess = false;
    }

    private Result(bool success)
    {
        Error = default;
        IsSuccess = success;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true);

    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => new(error);
}
