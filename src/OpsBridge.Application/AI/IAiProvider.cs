namespace OpsBridge.Application.AI;

public interface IAiProvider
{
    Task<AiResponse> GenerateResponseAsync(AiRequest request, CancellationToken ct = default);
    string ProviderName { get; }
}

public sealed record AiRequest(
    string Prompt,
    string? SystemPrompt = null,
    float Temperature = 0.7f,
    int MaxTokens = 2048,
    Dictionary<string, object>? Metadata = null);

public sealed record AiResponse(
    string Content,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string FinishReason,
    Dictionary<string, object>? Metadata = null);

public sealed class AiProviderException : Exception
{
    public string ProviderName { get; }
    public AiErrorCode ErrorCode { get; }
    public int? HttpStatusCode { get; }

    public AiProviderException(string providerName, AiErrorCode errorCode, string message, int? httpStatusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderName = providerName;
        ErrorCode = errorCode;
        HttpStatusCode = httpStatusCode;
    }
}

public enum AiErrorCode
{
    Unknown,
    AuthenticationFailed,
    RateLimited,
    QuotaExceeded,
    ModelNotFound,
    InvalidRequest,
    ContentFiltered,
    ProviderUnavailable,
    Timeout,
    NetworkError
}