using System.Collections.ObjectModel;

namespace OokiGrader.Domain.Common;

public sealed record DomainError(string Code, string Message, string? Path = null);

public sealed class DomainValidationResult
{
    private static readonly DomainValidationResult ValidResult = new([]);
    private readonly ReadOnlyCollection<DomainError> _errors;

    private DomainValidationResult(IEnumerable<DomainError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        _errors = Array.AsReadOnly(errors.ToArray());
    }

    public bool IsValid => _errors.Count == 0;

    public IReadOnlyList<DomainError> Errors => _errors;

    public static DomainValidationResult Valid() => ValidResult;

    public static DomainValidationResult Invalid(params DomainError[] errors) =>
        Invalid((IEnumerable<DomainError>)errors);

    public static DomainValidationResult Invalid(IEnumerable<DomainError> errors)
    {
        var materialized = errors?.ToArray() ?? throw new ArgumentNullException(nameof(errors));
        if (materialized.Length == 0)
        {
            throw new ArgumentException("An invalid result must contain at least one error.", nameof(errors));
        }

        return new DomainValidationResult(materialized);
    }

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new DomainValidationException(_errors);
        }
    }
}

public sealed class DomainResult<T>
{
    private readonly T? _value;
    private readonly ReadOnlyCollection<DomainError> _errors;

    internal DomainResult(T value)
    {
        _value = value;
        _errors = Array.AsReadOnly(Array.Empty<DomainError>());
        IsSuccess = true;
    }

    internal DomainResult(IEnumerable<DomainError> errors)
    {
        var materialized = errors.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("A failed result must contain at least one error.", nameof(errors));
        }

        _errors = Array.AsReadOnly(materialized);
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public IReadOnlyList<DomainError> Errors => _errors;

    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidDomainStateException("A failed domain result has no value.");

}

public static class DomainResult
{
    public static DomainResult<T> Success<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DomainResult<T>(value);
    }

    public static DomainResult<T> Failure<T>(IEnumerable<DomainError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new DomainResult<T>(errors);
    }
}
