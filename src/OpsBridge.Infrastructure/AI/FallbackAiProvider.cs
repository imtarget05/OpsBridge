using System.Text.Json;
using OpsBridge.Application.AI;

namespace OpsBridge.Infrastructure.AI;

public sealed class FallbackAiProvider : IAiProvider
{
    public string ProviderName => "fallback";

    public Task<AiResponse> GenerateResponseAsync(AiRequest request, CancellationToken ct = default)
    {
        // This provider always throws to trigger fallback logic in the use case
        throw new AiProviderException(
            ProviderName,
            AiErrorCode.ProviderUnavailable,
            "Fallback provider intentionally fails to trigger heuristic response",
            httpStatusCode: 503);
    }
}