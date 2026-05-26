namespace Cnas.Ps.Core.Common;

/// <summary>
/// Outcome of a service operation that may succeed or fail.
/// Business failures are returned as <see cref="Result{T}"/> with <see cref="ErrorCode"/> set;
/// only truly exceptional conditions (network, OOM, ...) throw.
/// See CLAUDE.md §2.1 — Result Pattern.
/// </summary>
/// <typeparam name="T">Type of the value carried on success.</typeparam>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, string? errorCode, string? errorMessage)
    {
        IsSuccess = isSuccess;
        _value = value;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>True when the operation completed without error.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when the operation failed; convenience inverse of <see cref="IsSuccess"/>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// The success value. Accessing this on a failed result is a programmer error and throws.
    /// Callers should branch on <see cref="IsSuccess"/> before reading.
    /// </summary>
    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException(
                $"Cannot read Value from a failed Result (code={ErrorCode}).");

    /// <summary>
    /// Stable, screaming-snake-case error code from <see cref="ErrorCodes"/>.
    /// Stable across versions — renaming is a breaking API change.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>Human-friendly description of the failure (logged but never shown to anonymous users).</summary>
    public string? ErrorMessage { get; }

    /// <summary>Wraps <paramref name="value"/> in a successful <see cref="Result{T}"/>.</summary>
    public static Result<T> Success(T value) => new(true, value, null, null);

    /// <summary>Builds a failed result with the given <paramref name="code"/> and human message.</summary>
    public static Result<T> Failure(string code, string message) => new(false, default, code, message);

    /// <summary>Lifts a non-generic <see cref="Result"/> failure to <see cref="Result{T}"/>.</summary>
    public static Result<T> From(Result failure) =>
        failure.IsFailure
            ? Failure(failure.ErrorCode!, failure.ErrorMessage!)
            : throw new InvalidOperationException("From(Result) requires a failed input.");
}

/// <summary>
/// Non-generic flavour of <see cref="Result{T}"/> used when an operation has no return value.
/// </summary>
public readonly struct Result
{
    private Result(bool isSuccess, string? errorCode, string? errorMessage)
    {
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>True when the operation completed without error.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Stable error code from <see cref="ErrorCodes"/>.</summary>
    public string? ErrorCode { get; }

    /// <summary>Human-friendly description of the failure.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Successful, no-value result.</summary>
    public static Result Success() => new(true, null, null);

    /// <summary>Failed result with the given <paramref name="code"/> and human message.</summary>
    public static Result Failure(string code, string message) => new(false, code, message);
}
