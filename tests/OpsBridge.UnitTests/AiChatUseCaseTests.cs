using Microsoft.Extensions.Logging.Abstractions;
using OpsBridge.Application.AI;
using OpsBridge.Application.UseCases;
using OpsBridge.Domain;

namespace OpsBridge.UnitTests;

public class AiChatUseCaseTests
{
    private readonly AiChatUseCase _useCase;
    private readonly FakeAiProvider _failingProvider = new("failing", shouldFail: true);
    private readonly FakeAiProvider _successProvider = new("success", shouldFail: false);
    private readonly FakeAiProvider _rateLimitedProvider = new("rate-limited", shouldFail: true, AiErrorCode.RateLimited);

    public AiChatUseCaseTests()
    {
        _useCase = new AiChatUseCase(
            [
                new FakeAiProvider("failing", shouldFail: true, AiErrorCode.ProviderUnavailable),
                new FakeAiProvider("success", shouldFail: false),
                new FakeAiProvider("rate-limited", shouldFail: true, AiErrorCode.RateLimited)
            ],
            NullLogger<AiChatUseCase>.Instance);
    }

    [Fact]
    public async Task Execute_UnauthorizedRole_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var request = new AiChatRequest("Test message");

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _useCase.ExecuteAsync(request, "user1", "Customer"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _useCase.ExecuteAsync(request, "user1", "Auditor"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _useCase.ExecuteAsync(request, "user1", "UnknownRole"));
    }

    [Fact]
    public async Task Execute_AuthorizedRoles_Succeeds()
    {
        // Arrange
        var request = new AiChatRequest("Test message");

        // Act & Assert - should not throw for staff roles
        var response1 = await _useCase.ExecuteAsync(request, "user1", OpsBridgeRoles.Admin);
        var response2 = await _useCase.ExecuteAsync(request, "user1", OpsBridgeRoles.FinanceReviewer);
        var response3 = await _useCase.ExecuteAsync(request, "user1", OpsBridgeRoles.Operator);

        Assert.NotNull(response1);
        Assert.NotNull(response2);
        Assert.NotNull(response3);
    }

    [Fact]
    public async Task Execute_EmptyMessage_ThrowsArgumentException()
    {
        // Arrange
        var request = new AiChatRequest("");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _useCase.ExecuteAsync(request, "user1", OpsBridgeRoles.Operator));
    }

    [Fact]
    public async Task Execute_MessageTooLong_ThrowsArgumentException()
    {
        // Arrange
        var request = new AiChatRequest(new string('a', 4001));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _useCase.ExecuteAsync(request, "user1", OpsBridgeRoles.Operator));
    }

    [Fact]
    public async Task Execute_FirstProviderFails_SecondSucceeds_ReturnsSuccessResponse()
    {
        // Arrange
        var request = new AiChatRequest("Test message");

        // Act
        var response = await _useCase.ExecuteAsync(request, "user1", OpsBridgeRoles.Operator);

        // Assert
        Assert.Equal("success", response.Provider);
        Assert.False(response.IsFallback);
        Assert.Contains("Success response", response.Response);
    }

    [Fact]
    public async Task Execute_AllProvidersFail_ReturnsFallbackResponse()
    {
        // Arrange - use only failing providers with retryable errors
        var useCase = new AiChatUseCase(
            [
                new FakeAiProvider("fail1", shouldFail: true, AiErrorCode.ProviderUnavailable),
                new FakeAiProvider("fail2", shouldFail: true, AiErrorCode.NetworkError)
            ],
            NullLogger<AiChatUseCase>.Instance);

        // Use non-accented keywords that match the fallback logic
        var request = new AiChatRequest("quy trinh doi soat 3 ben");

        // Act
        var response = await useCase.ExecuteAsync(request, "user1", OpsBridgeRoles.Operator);

        // Assert
        Assert.Equal("fallback", response.Provider);
        Assert.True(response.IsFallback);
        Assert.Contains("Doi soat 3 Ben", response.Response); // Non-accented fallback
    }

    [Fact]
    public async Task Execute_RateLimitedError_RetriesNextProvider()
    {
        // Arrange - rate limited provider should be retried
        var useCase = new AiChatUseCase(
            [_rateLimitedProvider, _successProvider],
            NullLogger<AiChatUseCase>.Instance);

        var request = new AiChatRequest("Test message");

        // Act
        var response = await useCase.ExecuteAsync(request, "user1", OpsBridgeRoles.Operator);

        // Assert - should skip rate limited and use success provider
        Assert.Equal("success", response.Provider);
        Assert.False(response.IsFallback);
    }

    [Fact]
    public async Task Execute_NonRetryableError_ThrowsImmediately()
    {
        // Arrange - authentication failed is non-retryable
        var authFailProvider = new FakeAiProvider("auth-fail", shouldFail: true, AiErrorCode.AuthenticationFailed);
        var useCase = new AiChatUseCase(
            [authFailProvider, _successProvider],
            NullLogger<AiChatUseCase>.Instance);

        var request = new AiChatRequest("Test message");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<AiProviderException>(() =>
            useCase.ExecuteAsync(request, "user1", OpsBridgeRoles.Operator));

        Assert.Equal(AiErrorCode.AuthenticationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task Execute_FallbackResponse_ContainsVietnameseContent()
    {
        // Arrange - use retryable error so fallback is triggered
        var useCase = new AiChatUseCase(
            [new FakeAiProvider("fail", shouldFail: true, AiErrorCode.ProviderUnavailable)],
            NullLogger<AiChatUseCase>.Instance);

        // Act
        var response = await useCase.ExecuteAsync(
            new AiChatRequest("sai lệch số lượng"), "user1", OpsBridgeRoles.Operator);

        // Assert
        Assert.True(response.IsFallback);
        Assert.Equal("fallback", response.Provider);
        Assert.Contains("sai lech", response.Response); // Non-accented fallback
    }

    [Fact]
    public async Task Execute_ServerContext_IncludesUserIdAndRole()
    {
        // Arrange
        var capturingProvider = new CapturingAiProvider("capture", shouldFail: false);
        var useCase = new AiChatUseCase(
            [capturingProvider],
            NullLogger<AiChatUseCase>.Instance);

        var request = new AiChatRequest("Test message");

        // Act
        await useCase.ExecuteAsync(request, "test-user-123", OpsBridgeRoles.FinanceReviewer);

        // Assert
        Assert.NotNull(capturingProvider.LastRequest);
        Assert.Equal("test-user-123", capturingProvider.LastRequest.Metadata?["userId"]);
        Assert.Equal(OpsBridgeRoles.FinanceReviewer, capturingProvider.LastRequest.Metadata?["userRole"]);
        Assert.NotNull(capturingProvider.LastRequest.Metadata?["timestamp"]);
    }

    [Fact]
    public async Task Execute_ClientContext_OnlyAllowedKeysIncluded()
    {
        // Arrange
        var capturingProvider = new CapturingAiProvider("capture", shouldFail: false);
        var useCase = new AiChatUseCase(
            [capturingProvider],
            NullLogger<AiChatUseCase>.Instance);

        var request = new AiChatRequest("Test message", Context: new Dictionary<string, object>
        {
            ["locale"] = "vi-VN",
            ["timezone"] = "Asia/Ho_Chi_Minh",
            ["maliciousKey"] = "should-be-ignored",
            ["userId"] = "attacker" // should not override server-side userId
        });

        // Act
        await useCase.ExecuteAsync(request, "real-user-456", OpsBridgeRoles.Operator);

        // Assert
        Assert.Equal("vi-VN", capturingProvider.LastRequest.Metadata?["locale"]);
        Assert.Equal("Asia/Ho_Chi_Minh", capturingProvider.LastRequest.Metadata?["timezone"]);
        Assert.Equal("real-user-456", capturingProvider.LastRequest.Metadata?["userId"]); // Server-side wins
        Assert.False(capturingProvider.LastRequest.Metadata?.ContainsKey("maliciousKey") ?? true);
    }

    // Fake providers
    private sealed class FakeAiProvider(string name, bool shouldFail, AiErrorCode errorCode = AiErrorCode.Unknown) : IAiProvider
    {
        public string ProviderName => name;

        public Task<AiResponse> GenerateResponseAsync(AiRequest request, CancellationToken ct = default)
        {
            if (shouldFail)
            {
                throw new AiProviderException(name, errorCode, $"{name} failed", httpStatusCode: 500);
            }

            return Task.FromResult(new AiResponse(
                Content: "Success response from " + name,
                Model: "test-model",
                PromptTokens: 10,
                CompletionTokens: 20,
                TotalTokens: 30,
                FinishReason: "STOP"));
        }
    }

    private sealed class CapturingAiProvider(string name, bool shouldFail) : IAiProvider
    {
        public string ProviderName => name;
        public AiRequest? LastRequest { get; private set; }

        public Task<AiResponse> GenerateResponseAsync(AiRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (shouldFail)
            {
                throw new AiProviderException(name, AiErrorCode.Unknown, $"{name} failed", httpStatusCode: 500);
            }

            return Task.FromResult(new AiResponse(
                Content: "Success response from " + name,
                Model: "test-model",
                PromptTokens: 10,
                CompletionTokens: 20,
                TotalTokens: 30,
                FinishReason: "STOP"));
        }
    }
}