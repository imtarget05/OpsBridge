# Functional Requirements — Commerce + OpsBridge (OpsBridge bounded context)

> Mã định dạng `FR-Cxx`. Mỗi requirement được trace tới component, test case và evidence trong `traceability-matrix.md`. "Could" = chưa implement trong OpsBridge, không claim đã làm.

## Yêu cầu chức năng

| ID | Yêu cầu | Ưu tiên | Component thực hiện | Test case mapping | Trạng thái chứng minh |
|---|---|---|---|---|---|
| FR-C01 | Đặt hàng và reserve tồn kho nhất quán (no-oversell tại thời điểm đặt hàng) | Must | FlashSale Order/Inventory (ngoài OpsBridge) | concurrency-harness `oversell_demo.py` | PASS local (F2: stock 10→7, 0 oversell) |
| FR-C02 | Ghi outbox event cùng transaction nghiệp vụ (webhook accepted dù broker down) | Must | `OutboxPublisherHostedService`, `EfOutboxRepository` | `FailureDrill1_RabbitMqDown_WebhookStillAcceptedAndStoredInOutbox` | PASS (F2) |
| FR-C03 | Consumer xử lý event theo idempotency key (dedupe cùng eventId) | Must | `GoodsReceiptConsumerHost`, `IInboxRepository` | `FailureDrill4` / `OutboxPoisonTests` | PASS (F2) |
| FR-C04 | Phát hiện đơn đã nhận nhưng kẹt trước khi worker tạo Order | Must | `StuckOrderScanUseCase`, `StuckOrderRepository`, `StuckOrderScanHostedService` | `StuckOrderScanTests`, `StuckOrderIntegrationTests` | PARTIAL: đã có mã và test thời gian giả lập; chưa chạy được test runtime trong sandbox và chưa có fault drill/UAT cloud trên SHA phát hành |
| FR-C05 | AI tra cứu trạng thái đơn và đề xuất hướng xử lý | Should | AI Exception Assistant (ngoài OpsBridge core) | — | NOT_IMPLEMENTED (Could) |
| FR-C06 | Nhân viên phê duyệt thao tác replay / compensation (discrepancy review) | Must | `ReviewDiscrepancyUseCase`, `ThreeWayMatchingEngine` | `E2E_DiscrepancyReview_Approve...`, `E2E_DiscrepancyReview_Reject...` | PASS (F2) |
| FR-C07 | Dự báo nhu cầu theo SKU | Could | Forecast Service (ngoài OpsBridge) | — | NOT_IMPLEMENTED (Could) |

## Yêu cầu kỹ thuật ràng buộc (đã implement trong OpsBridge)

| ID | Yêu cầu | Component | Test | Trạng thái |
|---|---|---|---|---|
| FR-C08 | Replay DLQ có điều kiện (conditional-update, idempotent): chỉ 1 concurrent request thành công | `EfDeadLetterEventRepository`, `ReplayDeadLetterEventUseCase` | `FailureDrill5`, `FailureDrill6_ConcurrentManualReplay_QueuesExactlyOnce`, `ReplayDeadLetterEventUseCaseTests` | PASS (F2) |
| FR-C09 | Tất cả route OpsBridge require authorization, trừ 3 public allowlist (login, healthz, factoryflow webhook) | `OpsBridgeAuthPolicies`, endpoint `RequireAuthorization` | `OpsBridgeAuthorizationGuardTests.EveryMappedRoute_IsAuthorized_Or_InPublicAllowlist` | PASS (F2) |
| FR-C10 | Production không expose demo persona login; bắt buộc Jwt:SigningKey ≥32 bytes và từ chối built-in dev key | `Program.cs`, `OpsBridgeJwtTokenIssuer` | `OpsBridgeAuthBoundaryTests.Production_DoesNotExpose_DemoRoleLogin`, `Production_RequiresDedicatedJwtSigningKey`, `Production_RejectsBuiltInJwtSigningKey` | PASS (F2) |
| FR-C11 | Clean Architecture: Domain không tham chiếu infra pkg; Application→Domain; Infrastructure không tham chiếu FlashSale; endpoint không trực tiếp DbContext | `OpsBridgeArchitectureTests` | 5 architecture facts | PASS (F2) |

## Quy tắc vận hành (invariants)

- **INV-01 (Không double effect replay):** Cùng dead-letter eventId chỉ được enqueue replay 1 lần; các yêu cầu sau trả `AlreadyReplayed` và chỉ tạo 1 `OutboxMessage`.
- **INV-02 (Không overselling):** Số lượng bán không vượt quá tồn kho trong concurrency test đã định nghĩa.
- **INV-03 (Authorization deny-by-default):** Mọi route staff cần `RequireAuthorization`; 3 route công khai được liệt kê tường minh và kiểm tra không bị stale.
- **INV-04 (Transaction boundary):** Outbox + nghiệp vụ commit trong 1 transaction PostgreSQL qua `IUnitOfWork`/`EfUnitOfWork`.
