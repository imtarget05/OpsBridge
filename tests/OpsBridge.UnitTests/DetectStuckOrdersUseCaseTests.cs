using Microsoft.Extensions.Logging.Abstractions;
using OpsBridge.Application.Common;
using OpsBridge.Application.Reconciliation;
using OpsBridge.Application.StuckOrder;
using OpsBridge.Application.UseCases;
using OpsBridge.Domain.Reconciliation;
using OpsBridge.Domain.StuckOrder;

namespace OpsBridge.UnitTests;

public class DetectStuckOrdersUseCaseTests
{
    private readonly FakePurchaseOrderRepository _poRepo = new();
    private readonly FakeGoodsReceiptRepository _grRepo = new();
    private readonly FakeSupplierInvoiceRepository _invoiceRepo = new();
    private readonly FakeStuckOrderAlertRepository _alertRepo = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly DetectStuckOrdersUseCase _useCase;

    public DetectStuckOrdersUseCaseTests()
    {
        _useCase = new DetectStuckOrdersUseCase(
            _poRepo,
            _grRepo,
            _invoiceRepo,
            _alertRepo,
            _unitOfWork,
            _timeProvider,
            NullLogger<DetectStuckOrdersUseCase>.Instance);
    }

    [Fact]
    public async Task Execute_StuckOrderDetected_CreatesAlert()
    {
        // Arrange: PO created 48 hours ago, no GR or Invoice
        var po = CreatePo("PO-STUCK-01", "SUPP-01");
        po.OrderedAt = _timeProvider.UtcNow.AddHours(-48);
        await _poRepo.AddAsync(po);

        var options = new StuckOrderDetectionOptions(SlaThreshold: TimeSpan.FromHours(24));

        // Act
        var results = await _useCase.ExecuteAsync(options);

        // Assert
        var result = Assert.Single(results);
        Assert.True(result.IsStuck);
        Assert.Equal("PO-STUCK-01", result.PurchaseOrderNumber);
        Assert.Equal(StuckOrderSeverity.Critical, result.Severity);
        Assert.Contains("48", result.Reason); // 48 hours since last progress

        // Verify alert was created
        var alert = await _alertRepo.GetByPurchaseOrderIdAsync(po.Id);
        Assert.NotNull(alert);
        Assert.Equal(StuckOrderStatus.Open, alert.Status);
        Assert.Equal(StuckOrderSeverity.Critical, alert.Severity);
    }

    [Fact]
    public async Task Execute_NormalOrderNotStuck_NoAlertCreated()
    {
        // Arrange: PO created 2 hours ago, GR received 1 hour ago
        var po = CreatePo("PO-NORMAL-01", "SUPP-01");
        po.OrderedAt = _timeProvider.UtcNow.AddHours(-2);
        await _poRepo.AddAsync(po);

        var gr = CreateGr("GR-01", "PO-NORMAL-01", "SUPP-01");
        gr.ReceivedAt = _timeProvider.UtcNow.AddHours(-1);
        await _grRepo.AddAsync(gr);

        var options = new StuckOrderDetectionOptions(SlaThreshold: TimeSpan.FromHours(24));

        // Act
        var results = await _useCase.ExecuteAsync(options);

        // Assert
        var result = Assert.Single(results);
        Assert.False(result.IsStuck);
        Assert.Equal("PO-NORMAL-01", result.PurchaseOrderNumber);
        Assert.Equal(StuckOrderSeverity.Warning, result.Severity);

        // Verify no alert was created
        var alert = await _alertRepo.GetByPurchaseOrderIdAsync(po.Id);
        Assert.Null(alert);
    }

    [Fact]
    public async Task Execute_OrderProgressedAfterStuck_ResolvesAlert()
    {
        // Arrange: PO was stuck, alert created
        var po = CreatePo("PO-RESOLVED-01", "SUPP-01");
        po.OrderedAt = _timeProvider.UtcNow.AddHours(-48);
        await _poRepo.AddAsync(po);

        var options = new StuckOrderDetectionOptions(SlaThreshold: TimeSpan.FromHours(24));

        // First run - detect stuck
        await _useCase.ExecuteAsync(options);
        var alert = await _alertRepo.GetByPurchaseOrderIdAsync(po.Id);
        Assert.NotNull(alert);
        Assert.Equal(StuckOrderStatus.Open, alert.Status);

        // Act: Add GR (progress) and run detection again
        var gr = CreateGr("GR-01", "PO-RESOLVED-01", "SUPP-01");
        gr.ReceivedAt = _timeProvider.UtcNow;
        await _grRepo.AddAsync(gr);

        await _useCase.ExecuteAsync(options);

        // Assert: Alert should be resolved
        alert = await _alertRepo.GetByPurchaseOrderIdAsync(po.Id);
        Assert.NotNull(alert);
        Assert.Equal(StuckOrderStatus.Resolved, alert.Status);
        Assert.NotNull(alert.ResolvedAt);
        Assert.Equal("Order progressed - no longer stuck", alert.ResolutionNotes);
    }

    [Fact]
    public async Task Execute_StuckOrderRetried_MaxRetriesExceeded_DeadLettered()
    {
        // Arrange: PO stuck for multiple detection runs
        var po = CreatePo("PO-DLQ-01", "SUPP-01");
        po.OrderedAt = _timeProvider.UtcNow.AddHours(-48);
        await _poRepo.AddAsync(po);

        var options = new StuckOrderDetectionOptions(
            SlaThreshold: TimeSpan.FromHours(24),
            MaxRetries: 2,
            RetryInterval: TimeSpan.FromMinutes(30));

        // First detection - create alert (RetryCount = 0)
        await _useCase.ExecuteAsync(options);
        var alert = await _alertRepo.GetByPurchaseOrderIdAsync(po.Id);
        Assert.NotNull(alert);
        Assert.Equal(0, alert.RetryCount);

        // Second detection - retry (RetryCount = 1)
        _timeProvider.Advance(TimeSpan.FromHours(1));
        await _useCase.ExecuteAsync(options);
        alert = await _alertRepo.GetByPurchaseOrderIdAsync(po.Id);
        Assert.NotNull(alert);
        Assert.Equal(1, alert.RetryCount);

        // Third detection - retry (RetryCount = 2, max retries reached, dead lettered)
        _timeProvider.Advance(TimeSpan.FromHours(1));
        await _useCase.ExecuteAsync(options);
        alert = await _alertRepo.GetByPurchaseOrderIdAsync(po.Id);
        Assert.NotNull(alert);
        Assert.Equal(2, alert.RetryCount);
        Assert.True(alert.IsDeadLettered);
        Assert.Equal($"Max retries ({options.MaxRetries}) exceeded", alert.DeadLetterReason);
    }

    [Fact]
    public async Task Execute_CompletedOrder_NotChecked()
    {
        // Arrange: PO fully received and closed
        var po = CreatePo("PO-COMPLETED-01", "SUPP-01");
        po.OrderedAt = _timeProvider.UtcNow.AddDays(-10);
        po.Status = PurchaseOrderStatus.FullyReceived;
        await _poRepo.AddAsync(po);

        var options = new StuckOrderDetectionOptions(SlaThreshold: TimeSpan.FromHours(24));

        // Act
        var results = await _useCase.ExecuteAsync(options);

        // Assert: Should not be in results (skipped)
        Assert.Empty(results);
    }

    [Fact]
    public async Task Execute_ClosedOrder_NotChecked()
    {
        // Arrange: PO closed
        var po = CreatePo("PO-CLOSED-01", "SUPP-01");
        po.OrderedAt = _timeProvider.UtcNow.AddDays(-10);
        po.Status = PurchaseOrderStatus.Closed;
        await _poRepo.AddAsync(po);

        var options = new StuckOrderDetectionOptions(SlaThreshold: TimeSpan.FromHours(24));

        // Act
        var results = await _useCase.ExecuteAsync(options);

        // Assert: Should not be in results (skipped)
        Assert.Empty(results);
    }

    [Fact]
    public async Task Execute_AlreadyAlertedStuckOrder_IncrementsRetryCount()
    {
        // Arrange: PO with existing alert
        var po = CreatePo("PO-RETRY-01", "SUPP-01");
        po.OrderedAt = _timeProvider.UtcNow.AddHours(-48);
        await _poRepo.AddAsync(po);

        var existingAlert = new StuckOrderAlert
        {
            PurchaseOrderId = po.Id,
            PurchaseOrderNumber = po.OrderNumber,
            SupplierCode = po.SupplierCode,
            Reason = "Initial detection",
            Severity = StuckOrderSeverity.Warning,
            Status = StuckOrderStatus.Open,
            DetectedAt = _timeProvider.UtcNow.AddHours(-24),
            RetryCount = 1
        };
        await _alertRepo.AddAsync(existingAlert);

        var options = new StuckOrderDetectionOptions(
            SlaThreshold: TimeSpan.FromHours(24),
            MaxRetries: 3,
            RetryInterval: TimeSpan.FromHours(1));

        // Act
        _timeProvider.Advance(TimeSpan.FromHours(2));
        await _useCase.ExecuteAsync(options);

        // Assert
        var alert = await _alertRepo.GetByPurchaseOrderIdAsync(po.Id);
        Assert.NotNull(alert);
        Assert.Equal(2, alert.RetryCount);
        Assert.NotNull(alert.LastRetryAt);
    }

    // Helpers
    private static PurchaseOrder CreatePo(string poNum, string suppCode)
    {
        return new PurchaseOrder
        {
            Id = Guid.NewGuid(),
            OrderNumber = poNum,
            SupplierCode = suppCode,
            SupplierName = "Test Supplier",
            Currency = "VND",
            Status = PurchaseOrderStatus.Open,
            OrderedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new PurchaseOrderLine
                {
                    Id = Guid.NewGuid(),
                    LineNumber = 1,
                    ItemSku = "SKU-001",
                    Description = "Test Item",
                    OrderedQuantity = 100m,
                    UnitOfMeasure = "PCS",
                    UnitPrice = 20000m,
                    TotalAmount = 2000000m
                }
            ]
        };
    }

    private static GoodsReceipt CreateGr(string grNum, string poNum, string suppCode)
    {
        return new GoodsReceipt
        {
            Id = Guid.NewGuid(),
            ReceiptNumber = grNum,
            PurchaseOrderNumber = poNum,
            SupplierCode = suppCode,
            ReceivedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new GoodsReceiptLine
                {
                    Id = Guid.NewGuid(),
                    LineNumber = 1,
                    ItemSku = "SKU-001",
                    ReceivedQuantity = 100m,
                    UnitOfMeasure = "PCS",
                    AllocatedQuantity = 0m
                }
            ]
        };
    }

    // Fakes
    private sealed class FakePurchaseOrderRepository : IPurchaseOrderRepository
    {
        private readonly List<PurchaseOrder> _pos = [];
        public Task<PurchaseOrder?> GetByOrderNumberAsync(string orderNumber, CancellationToken ct = default) =>
            Task.FromResult(_pos.FirstOrDefault(p => p.OrderNumber == orderNumber));
        public Task AddAsync(PurchaseOrder po, CancellationToken ct = default)
        {
            _pos.Add(po);
            return Task.CompletedTask;
        }
        public Task UpdateAsync(PurchaseOrder po, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PurchaseOrder>> ListAsync(int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrder>>(_pos.Take(limit).ToList());
    }

    private sealed class FakeGoodsReceiptRepository : IGoodsReceiptRepository
    {
        private readonly List<GoodsReceipt> _grs = [];
        public Task<GoodsReceipt?> GetByReceiptNumberAsync(string receiptNumber, CancellationToken ct = default) =>
            Task.FromResult(_grs.FirstOrDefault(g => g.ReceiptNumber == receiptNumber));
        public Task<IReadOnlyList<GoodsReceipt>> GetByPurchaseOrderNumberAsync(string orderNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GoodsReceipt>>(_grs.Where(g => g.PurchaseOrderNumber == orderNumber).ToList());
        public Task AddAsync(GoodsReceipt gr, CancellationToken ct = default)
        {
            _grs.Add(gr);
            return Task.CompletedTask;
        }
        public Task UpdateLineAsync(GoodsReceiptLine line, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAllocateQuantityAsync(Guid lineId, decimal quantityToAllocate, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ReleaseAllocationQuantityAsync(Guid lineId, decimal quantityToRelease, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<GoodsReceipt>> ListAsync(int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GoodsReceipt>>(_grs.Take(limit).ToList());
    }

    private sealed class FakeSupplierInvoiceRepository : ISupplierInvoiceRepository
    {
        private readonly List<SupplierInvoice> _invoices = [];
        public Task<SupplierInvoice?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_invoices.FirstOrDefault(i => i.Id == id));
        public Task<SupplierInvoice?> GetByNumberAndSupplierAsync(string invoiceNumber, string supplierCode, CancellationToken ct = default) =>
            Task.FromResult(_invoices.FirstOrDefault(i => i.InvoiceNumber == invoiceNumber && i.SupplierCode == supplierCode));
        public Task<IReadOnlyList<SupplierInvoice>> GetByPurchaseOrderNumberAsync(string poNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SupplierInvoice>>(_invoices.Where(i => i.PurchaseOrderNumber == poNumber).ToList());
        public Task AddAsync(SupplierInvoice invoice, CancellationToken ct = default)
        {
            _invoices.Add(invoice);
            return Task.CompletedTask;
        }
        public Task UpdateAsync(SupplierInvoice invoice, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SupplierInvoice>> ListAsync(int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SupplierInvoice>>(_invoices.Take(limit).ToList());
    }

    private sealed class FakeStuckOrderAlertRepository : IStuckOrderAlertRepository
    {
        private readonly Dictionary<Guid, StuckOrderAlert> _alerts = [];
        public Task AddAsync(StuckOrderAlert alert, CancellationToken ct = default)
        {
            _alerts[alert.Id] = alert;
            return Task.CompletedTask;
        }
        public Task<StuckOrderAlert?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_alerts.GetValueOrDefault(id));
        public Task<StuckOrderAlert?> GetByPurchaseOrderIdAsync(Guid purchaseOrderId, CancellationToken ct = default) =>
            Task.FromResult(_alerts.Values.FirstOrDefault(a => a.PurchaseOrderId == purchaseOrderId));
        public Task<IReadOnlyList<StuckOrderAlert>> GetOpenAlertsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StuckOrderAlert>>(_alerts.Values.Where(a => a.Status == StuckOrderStatus.Open || a.Status == StuckOrderStatus.UnderInvestigation).ToList());
        public Task<IReadOnlyList<StuckOrderAlert>> GetAlertsByStatusAsync(StuckOrderStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StuckOrderAlert>>(_alerts.Values.Where(a => a.Status == status).ToList());
        public Task<IReadOnlyList<StuckOrderAlert>> GetDeadLetteredAlertsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StuckOrderAlert>>(_alerts.Values.Where(a => a.IsDeadLettered).ToList());
        public Task<IReadOnlyList<StuckOrderAlert>> GetAlertsForRetryAsync(TimeSpan retryInterval, int maxRetries, CancellationToken ct = default)
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(retryInterval);
            return Task.FromResult<IReadOnlyList<StuckOrderAlert>>(_alerts.Values
                .Where(a => a.Status == StuckOrderStatus.Open && a.RetryCount < maxRetries && (a.LastRetryAt == null || a.LastRetryAt <= cutoff))
                .ToList());
        }
        public Task UpdateAsync(StuckOrderAlert alert, CancellationToken ct = default)
        {
            _alerts[alert.Id] = alert;
            return Task.CompletedTask;
        }
        public Task<bool> ExistsForPurchaseOrderAsync(Guid purchaseOrderId, CancellationToken ct = default) =>
            Task.FromResult(_alerts.Values.Any(a => a.PurchaseOrderId == purchaseOrderId));
    }
}