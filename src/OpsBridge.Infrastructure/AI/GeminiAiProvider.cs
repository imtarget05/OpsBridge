using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpsBridge.Application.AI;

namespace OpsBridge.Infrastructure.AI;

public sealed class GeminiAiProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<GeminiAiProvider> _logger;

    public string ProviderName => "gemini";

    public GeminiAiProvider(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<GeminiAiProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _apiKey = configuration["Ai:Gemini:ApiKey"] ?? string.Empty;
        _model = configuration["Ai:Gemini:Model"] ?? "gemini-2.5-flash";

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("Gemini API key not configured, provider will not be available");
        }
    }

    public async Task<AiResponse> GenerateResponseAsync(AiRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new AiProviderException(
                ProviderName,
                AiErrorCode.AuthenticationFailed,
                "Gemini API key not configured",
                httpStatusCode: 503);
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = request.SystemPrompt != null
                            ? $"{request.SystemPrompt}\n\n{request.Prompt}"
                            : request.Prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = request.Temperature,
                maxOutputTokens = request.MaxTokens
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.PostAsync(url, content, ct);

            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorCode = MapHttpStatusToErrorCode((int)response.StatusCode);
                _logger.LogError("Gemini API error {StatusCode}: {Body}", response.StatusCode, responseBody);

                throw new AiProviderException(
                    ProviderName,
                    errorCode,
                    $"Gemini API returned {(int)response.StatusCode}: {responseBody}",
                    httpStatusCode: (int)response.StatusCode);
            }

            var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseBody, JsonSerializerOptions.Web);

            if (geminiResponse?.Candidates == null || geminiResponse.Candidates.Length == 0)
            {
                throw new AiProviderException(
                    ProviderName,
                    AiErrorCode.InvalidRequest,
                    "Gemini API returned empty response",
                    httpStatusCode: 500);
            }

            var candidate = geminiResponse.Candidates[0];
            var text = candidate.Content?.Parts?[0]?.Text ?? string.Empty;

            return new AiResponse(
                Content: text,
                Model: _model,
                PromptTokens: geminiResponse.UsageMetadata?.PromptTokenCount ?? 0,
                CompletionTokens: geminiResponse.UsageMetadata?.CandidatesTokenCount ?? 0,
                TotalTokens: geminiResponse.UsageMetadata?.TotalTokenCount ?? 0,
                FinishReason: candidate.FinishReason ?? "UNKNOWN",
                Metadata: request.Metadata);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error calling Gemini API");
            throw new AiProviderException(
                ProviderName,
                AiErrorCode.NetworkError,
                "Network error calling Gemini API",
                innerException: ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Timeout calling Gemini API");
            throw new AiProviderException(
                ProviderName,
                AiErrorCode.Timeout,
                "Timeout calling Gemini API",
                innerException: ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Gemini API response");
            throw new AiProviderException(
                ProviderName,
                AiErrorCode.InvalidRequest,
                "Failed to parse Gemini API response",
                innerException: ex);
        }
    }

    private static AiErrorCode MapHttpStatusToErrorCode(int statusCode)
    {
        return statusCode switch
        {
            400 => AiErrorCode.InvalidRequest,
            401 => AiErrorCode.AuthenticationFailed,
            403 => AiErrorCode.AuthenticationFailed,
            404 => AiErrorCode.ModelNotFound,
            429 => AiErrorCode.RateLimited,
            500 => AiErrorCode.ProviderUnavailable,
            503 => AiErrorCode.ProviderUnavailable,
            504 => AiErrorCode.Timeout,
            _ => AiErrorCode.Unknown
        };
    }

    // Response DTOs
    private sealed class GeminiResponse
    {
        public GeminiCandidate[]? Candidates { get; set; }
        public GeminiUsageMetadata? UsageMetadata { get; set; }
    }

    private sealed class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class GeminiContent
    {
        public GeminiPart[]? Parts { get; set; }
    }

    private sealed class GeminiPart
    {
        public string? Text { get; set; }
    }

    private sealed class GeminiUsageMetadata
    {
        public int PromptTokenCount { get; set; }
        public int CandidatesTokenCount { get; set; }
        public int TotalTokenCount { get; set; }
    }
}