namespace OokiGrader.Ai.Abstractions;

public enum AiFailureKind
{
    InvalidConfiguration,
    Authentication,
    BudgetBlocked,
    RateLimited,
    TransientProvider,
    Timeout,
    SafetyBlocked,
    InvalidResponse,
    RequestRejected,
}

public sealed class AiProviderException : Exception
{
    public AiProviderException(
        AiFailureKind kind,
        string safeErrorCode,
        bool isTransient,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(safeErrorCode, innerException)
    {
        Kind = kind;
        SafeErrorCode = safeErrorCode;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    public AiFailureKind Kind { get; }

    public string SafeErrorCode { get; }

    public bool IsTransient { get; }

    public TimeSpan? RetryAfter { get; }
}
