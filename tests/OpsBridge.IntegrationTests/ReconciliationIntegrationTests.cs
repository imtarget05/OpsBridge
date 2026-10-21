using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsBridge.Api.Endpoints;
using OpsBridge.Domain;
using OpsBridge.Domain.Reconciliation;
using OpsBridge.Infrastructure.Persistence;

namespace OpsBridge.IntegrationTests;

[Collection("OpsBridgeIntegration")]
public class ReconciliationIntegrationTests(OpsBridgeFixture fixture)
{
    private HttpClient CreateClient() => fixture.AuthenticatedClient;

    [Fact]
    public async Task E2E_PurchaseOrder_Ingestion_PersistsInPostgres()
    {
        await fixture.ResetAsync();
        var client = CreateClient();

        var poRequest = new CreatePurchaseOrderRequest(
            OrderNumber: "PO-INT-100",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme Supplies",
            Currency: "VND",
            SourceSystem: "IntegrationTest",
            Lines:
            [
                new CreatePurchaseOrderLineRequest(1, "RM-008", "Raw Material 8", 100m, "PCS", 20000m)
            ]
        );

        var response = await client.PostAsJsonAsync("/api/v1/purchase-orders", poRequest);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var getResponse = await client.GetAsync("/api/v1/purchase-orders/PO-INT-100");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var po = await getResponse.Content.ReadOpsBridgeJsonAsync<PurchaseOrder>();
        Assert.NotNull(po);
        Assert.Equal("PO-INT-100", po.OrderNumber);
        Assert.Equal(2000000m, po.TotalAmount); // 100 * 20,000
    }

    [Fact]
    public async Task E2E_SupplierInvoice_CsvIngestion_PersistsInPostgres()
    {
        await fixture.ResetAsync();
        var client = CreateClient();

        var csv = """
            InvoiceNumber,PurchaseOrderNumber,SupplierCode,SupplierName,Currency,LineNumber,ItemSku,Description,InvoicedQuantity,UnitOfMeasure,UnitPrice,TaxRate
            INV-CSV-01,PO-INT-100,SUPP-01,Acme Supplies,VND,1,RM-008,Raw Material 8,100,PCS,20000,0.1
            """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoices")
        {
            Content = new StringContent(csv, System.Text.Encoding.UTF8, "text/csv")
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        var invoice = await db.SupplierInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.InvoiceNumber == "INV-CSV-01");

        Assert.NotNull(invoice);
        Assert.Equal("PO-INT-100", invoice.PurchaseOrderNumber);
        Assert.Equal(2000000m, invoice.SubTotal);
        Assert.Equal(200000m, invoice.TaxAmount); // 10% VAT
        Assert.Equal(2200000m, invoice.TotalAmount);
    }

    [Fact]
    public async Task E2E_ThreeWayMatching_ReferenceScenario_Po1001_MatchesOnRerun()
    {
        await fixture.ResetAsync();
        var client = CreateClient();

        // Step 1: Create PO-1001 (100 PCS @ 20,000 VND)
        var poRequest = new CreatePurchaseOrderRequest(
            OrderNumber: "PO-1001",
            SupplierCode: "SUPP-01",
            SupplierName: "Global Raw Goods",
            Currency: "VND",
            SourceSystem: "ERP",
            Lines:
            [
                new CreatePurchaseOrderLineRequest(1, "RM-008", "Titanium Alloy Rod", 100m, "PCS", 20000m)
            ]
        );
        var poResp = await client.PostAsJsonAsync("/api/v1/purchase-orders", poRequest);
        Assert.Equal(HttpStatusCode.Created, poResp.StatusCode);

        // Step 2: Ingest GR-1 (60 PCS) & GR-2 (30 PCS) -> Cumulative 90 PCS
        var gr1 = new CreateGoodsReceiptRequest(
            ReceiptNumber: "GR-1",
            PurchaseOrderNumber: "PO-1001",
            SupplierCode: "SUPP-01",
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceSystem: "Warehouse",
            Lines: [new CreateGoodsReceiptLineRequest(1, "RM-008", 60m, "PCS")]
        );
        await client.PostAsJsonAsync("/api/v1/goods-receipts", gr1);

        var gr2 = new CreateGoodsReceiptRequest(
            ReceiptNumber: "GR-2",
            PurchaseOrderNumber: "PO-1001",
            SupplierCode: "SUPP-01",
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceSystem: "Warehouse",
            Lines: [new CreateGoodsReceiptLineRequest(1, "RM-008", 30m, "PCS")]
        );
        await client.PostAsJsonAsync("/api/v1/goods-receipts", gr2);

        // Step 3: Ingest Supplier Invoice (100 PCS @ 20,000 VND)
        var invRequest = new CreateSupplierInvoiceRequest(
            InvoiceNumber: "INV-9001",
            PurchaseOrderNumber: "PO-1001",
            SupplierCode: "SUPP-01",
            SupplierName: "Global Raw Goods",
            Currency: "VND",
            InvoiceDate: DateTimeOffset.UtcNow,
            SourceSystem: "VendorPortal",
            Lines: [new CreateSupplierInvoiceLineRequest(1, "RM-008", "Titanium Alloy Rod", 100m, "PCS", 20000m, 0m)]
        );
        var invResp = await client.PostAsJsonAsync("/api/v1/invoices", invRequest);
        Assert.Equal(HttpStatusCode.Created, invResp.StatusCode);
        var createdInvoice = await invResp.Content.ReadOpsBridgeJsonAsync<SupplierInvoice>();
        Assert.NotNull(createdInvoice);

        // Step 4: Run initial 3-way match
        var matchReq1 = new RunReconciliationRequest(InvoiceId: createdInvoice.Id);
        var matchResp1 = await client.PostAsJsonAsync("/api/v1/reconciliation/run", matchReq1);
        Assert.Equal(HttpStatusCode.OK, matchResp1.StatusCode);
        var result1 = await matchResp1.Content.ReadOpsBridgeJsonAsync<ReconciliationResult>();
        Assert.NotNull(result1);
        Assert.Equal(MatchStatus.Discrepancy, result1.Status);
        Assert.Contains(result1.Discrepancies, d => d.Type == DiscrepancyType.QuantityMismatch && d.VarianceAmount == 10m);

        // Step 5: Deliver final 10 PCS in GR-3
        var gr3 = new CreateGoodsReceiptRequest(
            ReceiptNumber: "GR-3",
            PurchaseOrderNumber: "PO-1001",
            SupplierCode: "SUPP-01",
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceSystem: "Warehouse",
            Lines: [new CreateGoodsReceiptLineRequest(1, "RM-008", 10m, "PCS")]
        );
        await client.PostAsJsonAsync("/api/v1/goods-receipts", gr3);

        // Step 6: Rerun 3-way match
        var matchReq2 = new RunReconciliationRequest(InvoiceId: createdInvoice.Id);
        var matchResp2 = await client.PostAsJsonAsync("/api/v1/reconciliation/run", matchReq2);
        Assert.Equal(HttpStatusCode.OK, matchResp2.StatusCode);
        var result2 = await matchResp2.Content.ReadOpsBridgeJsonAsync<ReconciliationResult>();
        Assert.NotNull(result2);
        Assert.Equal(MatchStatus.Matched, result2.Status);
        Assert.Empty(result2.Discrepancies);
        Assert.Equal(3, result2.Allocations.Count);

        // Verify PostgreSQL database state: receipts allocated and invoice status Matched
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        var updatedInvoice = await db.SupplierInvoices.FindAsync(createdInvoice.Id);
        Assert.Equal(InvoiceStatus.Matched, updatedInvoice?.Status);

        var grs = await db.GoodsReceipts
            .Include(g => g.Lines)
            .Where(g => g.PurchaseOrderNumber == "PO-1001")
            .OrderBy(g => g.ReceiptNumber)
            .ToListAsync();

        Assert.Equal(60m, grs.First(g => g.ReceiptNumber == "GR-1").Lines[0].AllocatedQuantity);
        Assert.Equal(30m, grs.First(g => g.ReceiptNumber == "GR-2").Lines[0].AllocatedQuantity);
        Assert.Equal(10m, grs.First(g => g.ReceiptNumber == "GR-3").Lines[0].AllocatedQuantity);
    }

    [Fact]
    public async Task E2E_ConcurrentReconciliations_CompeteForReceiptStock_PreventsOverAllocation()
    {
        await fixture.ResetAsync();
        var client = CreateClient();

        // PO with 100 PCS
        await client.PostAsJsonAsync("/api/v1/purchase-orders", new CreatePurchaseOrderRequest(
            OrderNumber: "PO-CONCUR",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme",
            Currency: "VND",
            SourceSystem: "ERP",
            Lines: [new CreatePurchaseOrderLineRequest(1, "RM-CONCUR", "Material", 100m, "PCS", 20000m)]
        ));

        // GR with exactly 100 PCS
        await client.PostAsJsonAsync("/api/v1/goods-receipts", new CreateGoodsReceiptRequest(
            ReceiptNumber: "GR-CONCUR-01",
            PurchaseOrderNumber: "PO-CONCUR",
            SupplierCode: "SUPP-01",
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceSystem: "Warehouse",
            Lines: [new CreateGoodsReceiptLineRequest(1, "RM-CONCUR", 100m, "PCS")]
        ));

        // Two competing invoices for 70 PCS each (total 140 PCS > 100 PCS available)
        var inv1Resp = await client.PostAsJsonAsync("/api/v1/invoices", new CreateSupplierInvoiceRequest(
            InvoiceNumber: "INV-C1",
            PurchaseOrderNumber: "PO-CONCUR",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme",
            Currency: "VND",
            InvoiceDate: DateTimeOffset.UtcNow,
            SourceSystem: "Test",
            Lines: [new CreateSupplierInvoiceLineRequest(1, "RM-CONCUR", "Material", 70m, "PCS", 20000m, 0m)]
        ));
        var inv1 = await inv1Resp.Content.ReadOpsBridgeJsonAsync<SupplierInvoice>();

        var inv2Resp = await client.PostAsJsonAsync("/api/v1/invoices", new CreateSupplierInvoiceRequest(
            InvoiceNumber: "INV-C2",
            PurchaseOrderNumber: "PO-CONCUR",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme",
            Currency: "VND",
            InvoiceDate: DateTimeOffset.UtcNow,
            SourceSystem: "Test",
            Lines: [new CreateSupplierInvoiceLineRequest(1, "RM-CONCUR", "Material", 70m, "PCS", 20000m, 0m)]
        ));
        var inv2 = await inv2Resp.Content.ReadOpsBridgeJsonAsync<SupplierInvoice>();

        // Fire both reconciliation runs
        var task1 = client.PostAsJsonAsync("/api/v1/reconciliation/run", new RunReconciliationRequest(InvoiceId: inv1!.Id));
        var task2 = client.PostAsJsonAsync("/api/v1/reconciliation/run", new RunReconciliationRequest(InvoiceId: inv2!.Id));

        var responses = await Task.WhenAll(task1, task2);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var res1 = await responses[0].Content.ReadOpsBridgeJsonAsync<ReconciliationResult>();
        var res2 = await responses[1].Content.ReadOpsBridgeJsonAsync<ReconciliationResult>();

        // Exactly one must be Matched, and the other must have a QuantityMismatch discrepancy
        var matchedCount = (res1!.Status == MatchStatus.Matched ? 1 : 0) + (res2!.Status == MatchStatus.Matched ? 1 : 0);
        var discrepancyCount = (res1.Status == MatchStatus.Discrepancy ? 1 : 0) + (res2.Status == MatchStatus.Discrepancy ? 1 : 0);

        Assert.Equal(1, matchedCount);
        Assert.Equal(1, discrepancyCount);

        // Verify in DB that total allocated quantity is exactly 70m, never 140m
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        var grLine = await db.GoodsReceiptLines.FirstAsync(l => l.ItemSku == "RM-CONCUR");
        Assert.Equal(70m, grLine.AllocatedQuantity);
        Assert.Equal(30m, grLine.AvailableQuantity);
    }

    [Fact]
    public async Task E2E_DiscrepancyReview_WithRbac_AuditsDecision()
    {
        await fixture.ResetAsync();
        var client = CreateClient();

        // PO @ 20,000 VND
        await client.PostAsJsonAsync("/api/v1/purchase-orders", new CreatePurchaseOrderRequest(
            OrderNumber: "PO-REVIEW",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme",
            Currency: "VND",
            SourceSystem: "ERP",
            Lines: [new CreatePurchaseOrderLineRequest(1, "RM-008", "Material", 100m, "PCS", 20000m)]
        ));

        // GR 100 PCS
        await client.PostAsJsonAsync("/api/v1/goods-receipts", new CreateGoodsReceiptRequest(
            ReceiptNumber: "GR-REVIEW-01",
            PurchaseOrderNumber: "PO-REVIEW",
            SupplierCode: "SUPP-01",
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceSystem: "Warehouse",
            Lines: [new CreateGoodsReceiptLineRequest(1, "RM-008", 100m, "PCS")]
        ));

        // Invoice @ 25,000 VND (Price Mismatch)
        var invResp = await client.PostAsJsonAsync("/api/v1/invoices", new CreateSupplierInvoiceRequest(
            InvoiceNumber: "INV-PRICE-ERR",
            PurchaseOrderNumber: "PO-REVIEW",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme",
            Currency: "VND",
            InvoiceDate: DateTimeOffset.UtcNow,
            SourceSystem: "Portal",
            Lines: [new CreateSupplierInvoiceLineRequest(1, "RM-008", "Material", 100m, "PCS", 25000m, 0m)]
        ));
        var invoice = await invResp.Content.ReadOpsBridgeJsonAsync<SupplierInvoice>();

        // Reconcile -> creates PriceMismatch discrepancy
        var runResp = await client.PostAsJsonAsync("/api/v1/reconciliation/run", new RunReconciliationRequest(InvoiceId: invoice!.Id));
        var runResult = await runResp.Content.ReadOpsBridgeJsonAsync<ReconciliationResult>();
        Assert.Equal(MatchStatus.Discrepancy, runResult!.Status);
        var disc = Assert.Single(runResult.Discrepancies);

        // Attempt review with insufficient role (Operator lacks FinanceReviewer) -> 403 Forbidden
        var forbiddenClient = fixture.ClientForRole(OpsBridgeRoles.Auditor);
        var unauthorizedReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/reconciliation/discrepancies/{disc.Id}/review")
        {
            Content = JsonContent.Create(new ReviewDiscrepancyRequest(ReviewAction.ApprovedWithTolerance, "Attempted bypass"))
        };

        var forbiddenResp = await forbiddenClient.SendAsync(unauthorizedReq);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);

        // Review with authorized FinanceReviewer JWT -> 200 OK
        var financeClient = fixture.ClientForRole(OpsBridgeRoles.FinanceReviewer);
        var authorizedReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/reconciliation/discrepancies/{disc.Id}/review")
        {
            Content = JsonContent.Create(new ReviewDiscrepancyRequest(ReviewAction.ApprovedWithTolerance, "Executive price variance approved for batch"))
        };

        var successResp = await financeClient.SendAsync(authorizedReq);
        Assert.Equal(HttpStatusCode.OK, successResp.StatusCode);

        // Verify audit trail in PostgreSQL
        var auditResp = await financeClient.GetAsync("/api/v1/reconciliation/audit-trail");
        Assert.Equal(HttpStatusCode.OK, auditResp.StatusCode);
        var auditEvents = await auditResp.Content.ReadOpsBridgeJsonAsync<List<AuditEvent>>();
        Assert.NotNull(auditEvents);
        Assert.Contains(auditEvents, a => a.EntityType == "Discrepancy" && a.UserId.StartsWith("int-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task E2E_DiscrepancyReview_ApproveWithVariance_CommitsAllocations_AndTransitionsStatus()
    {
        await fixture.ResetAsync();
        var client = CreateClient();

        // 1. PO: 100 PCS @ 20,000 VND
        await client.PostAsJsonAsync("/api/v1/purchase-orders", new CreatePurchaseOrderRequest(
            OrderNumber: "PO-VAR-APPROVE",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme",
            Currency: "VND",
            SourceSystem: "ERP",
            Lines: [new CreatePurchaseOrderLineRequest(1, "RM-008", "Material", 100m, "PCS", 20000m)]
        ));

        // 2. GR: 100 PCS received
        await client.PostAsJsonAsync("/api/v1/goods-receipts", new CreateGoodsReceiptRequest(
            ReceiptNumber: "GR-VAR-APPROVE",
            PurchaseOrderNumber: "PO-VAR-APPROVE",
            SupplierCode: "SUPP-01",
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceSystem: "Warehouse",
            Lines: [new CreateGoodsReceiptLineRequest(1, "RM-008", 100m, "PCS")]
        ));

        // 3. Invoice: 100 PCS @ 21,000 VND (5% variance, above default 1% tolerance)
        var invResp = await client.PostAsJsonAsync("/api/v1/invoices", new CreateSupplierInvoiceRequest(
            InvoiceNumber: "INV-VAR-APPROVE",
            PurchaseOrderNumber: "PO-VAR-APPROVE",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme",
            Currency: "VND",
            InvoiceDate: DateTimeOffset.UtcNow,
            SourceSystem: "Portal",
            Lines: [new CreateSupplierInvoiceLineRequest(1, "RM-008", "Material", 100m, "PCS", 21000m, 0m)]
        ));
        var invoice = await invResp.Content.ReadOpsBridgeJsonAsync<SupplierInvoice>();

        // 4. Reconcile -> PriceMismatch Discrepancy
        var runResp = await client.PostAsJsonAsync("/api/v1/reconciliation/run", new RunReconciliationRequest(InvoiceId: invoice!.Id));
        var runResult = await runResp.Content.ReadOpsBridgeJsonAsync<ReconciliationResult>();
        Assert.Equal(MatchStatus.Discrepancy, runResult!.Status);
        var disc = Assert.Single(runResult.Discrepancies);

        // 5. Review decision: ApprovedWithTolerance by FinanceReviewer JWT
        var reviewReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/reconciliation/discrepancies/{disc.Id}/review")
        {
            Content = JsonContent.Create(new ReviewDiscrepancyRequest(ReviewAction.ApprovedWithTolerance, "Approved acceptable price surge"))
        };

        var reviewResp = await fixture.ClientForRole(OpsBridgeRoles.FinanceReviewer).SendAsync(reviewReq);
        Assert.Equal(HttpStatusCode.OK, reviewResp.StatusCode);

        // 6. Verify in DB: Invoice status updated to ApprovedWithVariance and GR allocations committed
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        var dbInvoice = await db.SupplierInvoices.FindAsync(invoice.Id);
        Assert.NotNull(dbInvoice);
        Assert.Equal(InvoiceStatus.ApprovedWithVariance, dbInvoice.Status);

        var gr = await db.GoodsReceipts.Include(g => g.Lines).FirstAsync(g => g.ReceiptNumber == "GR-VAR-APPROVE");
        var grLine = gr.Lines[0];
        Assert.Equal(100m, grLine.AllocatedQuantity);
        Assert.Equal(0m, grLine.AvailableQuantity);
    }

    [Fact]
    public async Task E2E_DiscrepancyReview_Reject_TransitionsStatus_AndKeepsReceiptsAvailable()
    {
        await fixture.ResetAsync();
        var client = CreateClient();

        // 1. PO: 100 PCS
        await client.PostAsJsonAsync("/api/v1/purchase-orders", new CreatePurchaseOrderRequest(
            OrderNumber: "PO-VAR-REJECT",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme",
            Currency: "VND",
            SourceSystem: "ERP",
            Lines: [new CreatePurchaseOrderLineRequest(1, "RM-008", "Material", 100m, "PCS", 20000m)]
        ));

        // 2. GR: 100 PCS
        await client.PostAsJsonAsync("/api/v1/goods-receipts", new CreateGoodsReceiptRequest(
            ReceiptNumber: "GR-VAR-REJECT",
            PurchaseOrderNumber: "PO-VAR-REJECT",
            SupplierCode: "SUPP-01",
            ReceivedAt: DateTimeOffset.UtcNow,
            SourceSystem: "Warehouse",
            Lines: [new CreateGoodsReceiptLineRequest(1, "RM-008", 100m, "PCS")]
        ));

        // 3. Invoice: 100 PCS @ 30,000 VND (excessive variance)
        var invResp = await client.PostAsJsonAsync("/api/v1/invoices", new CreateSupplierInvoiceRequest(
            InvoiceNumber: "INV-VAR-REJECT",
            PurchaseOrderNumber: "PO-VAR-REJECT",
            SupplierCode: "SUPP-01",
            SupplierName: "Acme",
            Currency: "VND",
            InvoiceDate: DateTimeOffset.UtcNow,
            SourceSystem: "Portal",
            Lines: [new CreateSupplierInvoiceLineRequest(1, "RM-008", "Material", 100m, "PCS", 30000m, 0m)]
        ));
        var invoice = await invResp.Content.ReadOpsBridgeJsonAsync<SupplierInvoice>();

        // 4. Reconcile
        var runResp = await client.PostAsJsonAsync("/api/v1/reconciliation/run", new RunReconciliationRequest(InvoiceId: invoice!.Id));
        var runResult = await runResp.Content.ReadOpsBridgeJsonAsync<ReconciliationResult>();
        var disc = Assert.Single(runResult!.Discrepancies);

        // 5. Review decision: Rejected by FinanceReviewer JWT
        var reviewReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/reconciliation/discrepancies/{disc.Id}/review")
        {
            Content = JsonContent.Create(new ReviewDiscrepancyRequest(ReviewAction.Rejected, "Unacceptable price jump, rejected"))
        };

        var reviewResp = await fixture.ClientForRole(OpsBridgeRoles.FinanceReviewer).SendAsync(reviewReq);
        Assert.Equal(HttpStatusCode.OK, reviewResp.StatusCode);

        // 6. Verify in DB: Invoice status is Rejected, GR inventory remains 100% available (0 allocated)
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        var dbInvoice = await db.SupplierInvoices.FindAsync(invoice.Id);
        Assert.NotNull(dbInvoice);
        Assert.Equal(InvoiceStatus.Rejected, dbInvoice.Status);

        var gr = await db.GoodsReceipts.Include(g => g.Lines).FirstAsync(g => g.ReceiptNumber == "GR-VAR-REJECT");
        var grLine = gr.Lines[0];
        Assert.Equal(0m, grLine.AllocatedQuantity);
        Assert.Equal(100m, grLine.AvailableQuantity);
    }
}
