using System.Collections.ObjectModel;

namespace OokiGrader.Domain.Common;

public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }

    protected DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DomainValidationException : DomainException
{
    private readonly ReadOnlyCollection<DomainError> _errors;

    public DomainValidationException(IEnumerable<DomainError> errors)
        : base(CreateMessage(errors, out var materialized))
    {
        _errors = Array.AsReadOnly(materialized);
    }

    public IReadOnlyList<DomainError> Errors => _errors;

    private static string CreateMessage(
        IEnumerable<DomainError> errors,
        out DomainError[] materialized)
    {
        ArgumentNullException.ThrowIfNull(errors);
        materialized = errors.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException(
                "A validation exception must contain at least one error.",
                nameof(errors));
        }

        return string.Join(
            Environment.NewLine,
            materialized.Select(error =>
                error.Path is null
                    ? $"{error.Code}: {error.Message}"
                    : $"{error.Code} ({error.Path}): {error.Message}"));
    }
}

public sealed class InvalidDomainStateException : DomainException
{
    public InvalidDomainStateException(string message)
        : base(message)
    {
    }
}
