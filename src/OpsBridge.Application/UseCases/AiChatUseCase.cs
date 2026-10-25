using Microsoft.Extensions.Logging;
using OpsBridge.Application.AI;
using OpsBridge.Domain;

namespace OpsBridge.Application.UseCases;

public sealed record AiChatRequest(
    string Message,
    string? ConversationId = null,
    Dictionary<string, object>? Context = null);

public sealed record AiChatResponse(
    string Response,
    string ConversationId,
    string Provider,
    int TokensUsed,
    bool IsFallback);

public sealed class AiChatUseCase(
    IEnumerable<IAiProvider> providers,
    ILogger<AiChatUseCase> logger)
{
    private readonly IAiProvider[] _providers = providers.ToArray();

    public async Task<AiChatResponse> ExecuteAsync(
        AiChatRequest request,
        string userId,
        string userRole,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedRole(userRole))
        {
            throw new UnauthorizedAccessException("AI assistant access requires staff role (Operator, FinanceReviewer, or Admin)");
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("Message cannot be empty", nameof(request.Message));
        }

        if (request.Message.Length > 4000)
        {
            throw new ArgumentException("Message exceeds maximum length of 4000 characters", nameof(request.Message));
        }

        var conversationId = request.ConversationId ?? Guid.NewGuid().ToString();
        var serverContext = BuildServerContext(userId, userRole, request.Context);
        var systemPrompt = BuildSystemPrompt(userRole);
        var fullPrompt = $"{systemPrompt}\n\nUser: {request.Message}";

        var aiRequest = new AiRequest(fullPrompt, systemPrompt, Metadata: serverContext);

        foreach (var provider in _providers)
        {
            try
            {
                logger.LogInformation("Attempting AI request with provider {Provider}", provider.ProviderName);

                var response = await provider.GenerateResponseAsync(aiRequest, ct);

                logger.LogInformation("AI response received from {Provider}: {Tokens} tokens",
                    provider.ProviderName, response.TotalTokens);

                return new AiChatResponse(
                    Response: response.Content,
                    ConversationId: conversationId,
                    Provider: provider.ProviderName,
                    TokensUsed: response.TotalTokens,
                    IsFallback: false);
            }
            catch (AiProviderException ex) when (IsRetryableError(ex.ErrorCode))
            {
                logger.LogWarning(ex, "Provider {Provider} returned retryable error: {ErrorCode}",
                    provider.ProviderName, ex.ErrorCode);
                continue;
            }
            catch (AiProviderException ex)
            {
                logger.LogError(ex, "Provider {Provider} returned non-retryable error: {ErrorCode}",
                    provider.ProviderName, ex.ErrorCode);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error from provider {Provider}", provider.ProviderName);
                continue;
            }
        }

        logger.LogWarning("All AI providers failed, returning fallback response");
        var fallbackResponse = GetFallbackResponse(request.Message);

        return new AiChatResponse(
            Response: fallbackResponse,
            ConversationId: conversationId,
            Provider: "fallback",
            TokensUsed: 0,
            IsFallback: true);
    }

    private static bool IsAuthorizedRole(string role)
    {
        return role == OpsBridgeRoles.Admin
            || role == OpsBridgeRoles.FinanceReviewer
            || role == OpsBridgeRoles.Operator;
    }

    private static Dictionary<string, object> BuildServerContext(
        string userId,
        string userRole,
        Dictionary<string, object>? clientContext)
    {
        var context = new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["userRole"] = userRole,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["environment"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown"
        };

        if (clientContext != null)
        {
            var allowedKeys = new[] { "locale", "timezone" };
            foreach (var key in allowedKeys)
            {
                if (clientContext.TryGetValue(key, out var value))
                {
                    context[key] = value;
                }
            }
        }

        return context;
    }

    private static string BuildSystemPrompt(string userRole)
    {
        var basePrompt = "You are OpsBridge AI Assistant, an expert in procurement reconciliation and warehouse operations.\n" +
            "You help users with:\n" +
            "- Three-way matching (PO, Goods Receipt, Invoice)\n" +
            "- Discrepancy analysis and resolution\n" +
            "- Tolerance policy configuration\n" +
            "- System navigation and best practices\n\n" +
            "Always respond in Vietnamese. Be concise and actionable.";

        if (userRole == OpsBridgeRoles.Admin)
        {
            basePrompt += "\n\nUser has Admin role - you can discuss system configuration and advanced settings.";
        }
        else if (userRole == OpsBridgeRoles.FinanceReviewer)
        {
            basePrompt += "\n\nUser has FinanceReviewer role - focus on discrepancy review, approval workflows, and financial impact.";
        }
        else if (userRole == OpsBridgeRoles.Operator)
        {
            basePrompt += "\n\nUser has Operator role - focus on daily operations, goods receipt, and basic reconciliation.";
        }

        return basePrompt;
    }

    private static bool IsRetryableError(AiErrorCode errorCode)
    {
        return errorCode is AiErrorCode.RateLimited
            or AiErrorCode.ProviderUnavailable
            or AiErrorCode.Timeout
            or AiErrorCode.NetworkError;
    }

    private static string GetFallbackResponse(string userMessage)
    {
        var message = userMessage.ToLowerInvariant();

        if (message.Contains("quy trinh") || message.Contains("3 ben") || message.Contains("3-way") || message.Contains("la gi"))
        {
            return "Quy trinh Doi soat 3 Ben (Three-Way Matching) gom 4 buoc:\n" +
                "1. Don mua hang (PO): Bo phan mua hang lap cam ket mua hang voi NCC (quy dinh ro So luong dat & Don gia thoa thuan).\n" +
                "2. Phieu nhap kho (GR): Thu kho kiem dem thuc te khi xe giao hang toi kho, ghi nhan so luong thuc te nhan duoc.\n" +
                "3. Hoa don NCC (INV): NCC gui hoa don tai chinh yeu cau thanh toan.\n" +
                "4. Doi soat (3-Way Matching): He thong tu dong so khop cheo:\n" +
                "   - So luong tren Hoa don phai bang So luong thuc nhan tai Kho (GR).\n" +
                "   - Don gia tren Hoa don phai bang Don gia cam ket trong Don mua (PO).\n" +
                "Neu khop 100% hoac trong bien do dung sai (Tolerance), hoa don se chuyen sang trang thai Matched de phong Tai chinh xuat tien!";
        }

        if (message.Contains("sai lech") || message.Contains("chenh lech") || message.Contains("lech") || message.Contains("discrepancy"))
        {
            return "Phan tich sai lech (Discrepancy):\n" +
                "He thong phat hien chenh lech khi:\n" +
                "- So luong: Hoa don > Thuc nhan (GR) hoac > Don dat (PO)\n" +
                "- Don gia: Hoa don > PO vuot qua Tolerance (mac dinh 1%)\n" +
                "- Thieu PO/GR: Hoa don tham chieu PO khong ton tai hoac chua co GR\n\n" +
                "Khuyen nghi:\n" +
                "1. Kiem tra lai GR thuc te tai kho\n" +
                "2. Yeu cau NCC xuat hoa don dieu chinh\n" +
                "3. Duyet voi bien dong (ApprovedWithVariance) neu chap nhan chenh lech\n" +
                "4. Tu choi (Rejected) neu NCC can xuat lai hoa don chinh xac";
        }

        if (message.Contains("dung sai") || message.Contains("tolerance"))
        {
            return "Chinh sach Bien do Dung sai (Tolerance Policy):\n" +
                "He thong cho phep cau hinh 3 chi so:\n" +
                "1. Price Tolerance (%): Bien do lech don gia (mac dinh 1%). Phu hop thue phi lam tron.\n" +
                "2. Qty Tolerance (%): Bien do lech so luong theo %.\n" +
                "3. Qty Absolute Tolerance: So luong lech tuyet doi (vi du hao hut 1-2 don vi).\n\n" +
                "Neu trong bien do -> Tu dong ApprovedWithTolerance.";
        }

        if (message.Contains("buoc") || message.Contains("nguoi moi") || message.Contains("huong dan") || message.Contains("dung"))
        {
            return "Huong dan nhanh cho nguoi moi:\n" +
                "- Buoc 1: Menu Don mua hang (PO) - kiem tra don dat hang\n" +
                "- Buoc 2: Menu Phieu nhap kho (GR) - xem hang da ve kho\n" +
                "- Buoc 3: Menu Hoa don NCC (INV) - kiem tra/nhap hoa don\n" +
                "- Buoc 4: Menu Doi soat 3 ben - chon hoa don, nhan Thuc Thi Doi Soat";
        }

        return "Toi la Tro ly AI OpsBridge (che do du phong).\n" +
            "Co the giup ban ve:\n" +
            "- Quy trinh doi soat 3 ben (PO-GR-INV)\n" +
            "- Phan tich sai lech so luong/don gia\n" +
            "- Cau hinh Tolerance Policy\n" +
            "- Huong dan su dung he thong\n\n" +
            "Vui long thu lai sau khi dich vu AI chinh khoi phuc.";
    }
}