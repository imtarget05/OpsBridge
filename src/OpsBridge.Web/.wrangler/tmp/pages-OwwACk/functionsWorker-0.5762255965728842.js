var __defProp = Object.defineProperty;
var __name = (target, value) => __defProp(target, "name", { value, configurable: true });

// api/v1/[[path]].ts
var onRequest = /* @__PURE__ */ __name(async (context) => {
  const url = new URL(context.request.url);
  if (context.request.method === "OPTIONS") {
    return new Response(null, {
      status: 204,
      headers: {
        "Access-Control-Allow-Origin": url.origin,
        "Access-Control-Allow-Methods": "GET, POST, PUT, DELETE, PATCH, OPTIONS",
        "Access-Control-Allow-Headers": "Content-Type, Authorization",
        "Access-Control-Allow-Credentials": "true"
      }
    });
  }
  if (context.env.API_BACKEND_URL && !context.env.API_BACKEND_URL.includes("127.0.0.1")) {
    const subPath2 = url.pathname.replace(/^\/api\/v1/, "");
    const target = `${context.env.API_BACKEND_URL.replace(/\/$/, "")}/api/v1${subPath2}${url.search}`;
    try {
      const resp = await fetch(target, {
        method: context.request.method,
        headers: context.request.headers,
        body: ["GET", "HEAD"].includes(context.request.method) ? void 0 : context.request.body
      });
      if (resp.ok || resp.status < 500 && resp.status !== 404) {
        return resp;
      }
    } catch {
    }
  }
  const subPath = url.pathname.replace(/^\/api\/v1/, "");
  const jsonHeaders = {
    "Content-Type": "application/json",
    "Access-Control-Allow-Origin": url.origin,
    "Access-Control-Allow-Credentials": "true"
  };
  const purchaseOrders = [
    {
      id: "po-1001",
      orderNumber: "PO-2026-1001",
      supplierCode: "SUP-BAO-LOC-TEA",
      supplierName: "HTX Tr\xE0 Cao Nguy\xEAn B\u1EA3o L\u1ED9c",
      currency: "VND",
      orderedAt: new Date(Date.now() - 6048e5).toISOString(),
      status: "Fulfilled",
      sourceSystem: "SAP_S4HANA",
      totalAmount: 15e6,
      lines: [
        {
          id: "pol-01",
          lineNumber: 1,
          itemSku: "SKU-TEA-OOLONG-01",
          description: "Tr\xE0 Oolong Th\u01B0\u1EE3ng H\u1EA1ng (Bao 50kg)",
          orderedQuantity: 100,
          unitOfMeasure: "BAO",
          unitPrice: 15e4,
          totalAmount: 15e6
        }
      ]
    },
    {
      id: "po-1002",
      orderNumber: "PO-2026-1002",
      supplierCode: "SUP-VINAMILK-ENT",
      supplierName: "C\xF4ng ty C\u1ED5 ph\u1EA7n S\u1EEFa Vi\u1EC7t Nam (Vinamilk B2B)",
      currency: "VND",
      orderedAt: new Date(Date.now() - 5184e5).toISOString(),
      status: "Fulfilled",
      sourceSystem: "ORACLE_NETSUITE",
      totalAmount: 14e6,
      lines: [
        {
          id: "pol-02",
          lineNumber: 1,
          itemSku: "SKU-MILK-FRESH-1L",
          description: "S\u1EEFa T\u01B0\u01A1i Thanh Tr\xF9ng 100% (Th\xF9ng 12 H\u1ED9p 1L)",
          orderedQuantity: 500,
          unitOfMeasure: "THUNG",
          unitPrice: 28e3,
          totalAmount: 14e6
        }
      ]
    },
    {
      id: "po-1003",
      orderNumber: "PO-2026-1003",
      supplierCode: "SUP-TAIWAN-BOBA",
      supplierName: "C\xF4ng ty TNHH Nguy\xEAn Li\u1EC7u Tr\xE2n Ch\xE2u \u0110\xE0i Loan",
      currency: "VND",
      orderedAt: new Date(Date.now() - 432e6).toISOString(),
      status: "PartiallyFulfilled",
      sourceSystem: "SAP_S4HANA",
      totalAmount: 6e7,
      lines: [
        {
          id: "pol-03",
          lineNumber: 1,
          itemSku: "SKU-BOBA-BLACK-20K",
          description: "Tr\xE2n Ch\xE2u \u0110en D\u1EBBo Cao C\u1EA5p (Bao 20kg)",
          orderedQuantity: 200,
          unitOfMeasure: "BAO",
          unitPrice: 3e5,
          totalAmount: 6e7
        }
      ]
    },
    {
      id: "po-1004",
      orderNumber: "PO-2026-1004",
      supplierCode: "SUP-SAIGON-LOGISTICS",
      supplierName: "Cty TNHH Ti\u1EBFp V\u1EADn & Bao B\xEC S\xE0i G\xF2n",
      currency: "VND",
      orderedAt: new Date(Date.now() - 3456e5).toISOString(),
      status: "Fulfilled",
      sourceSystem: "MICROSOFT_DYNAMICS",
      totalAmount: 45e6,
      lines: [
        {
          id: "pol-04",
          lineNumber: 1,
          itemSku: "SKU-FILM-SEAL-700",
          description: "M\xE0ng \xC9p Mi\u1EC7ng Ly Nh\u1EF1a In Logo (Cu\u1ED9n 2,500 n\u1EAFp)",
          orderedQuantity: 1e3,
          unitOfMeasure: "CUON",
          unitPrice: 45e3,
          totalAmount: 45e6
        }
      ]
    },
    {
      id: "po-1005",
      orderNumber: "PO-2026-1005",
      supplierCode: "SUP-DAIKIN-COLD",
      supplierName: "Daikin Cold Chain Equipment Vietnam",
      currency: "VND",
      orderedAt: new Date(Date.now() - 2592e5).toISOString(),
      status: "Issued",
      sourceSystem: "ORACLE_NETSUITE",
      totalAmount: 185e5,
      lines: [
        {
          id: "pol-05",
          lineNumber: 1,
          itemSku: "SKU-FILTER-COLD-01",
          description: "B\u1ED9 L\u1ECDc Kh\xF4ng Kh\xED & Kh\u1EED Khu\u1EA9n Kho L\u1EA1nh",
          orderedQuantity: 10,
          unitOfMeasure: "SET",
          unitPrice: 185e4,
          totalAmount: 185e5
        }
      ]
    }
  ];
  const goodsReceipts = [
    {
      id: "gr-5001",
      receiptNumber: "GR-2026-5001",
      purchaseOrderNumber: "PO-2026-1001",
      supplierCode: "SUP-BAO-LOC-TEA",
      receivedAt: new Date(Date.now() - 3456e5).toISOString(),
      status: "Verified",
      warehouseCode: "WH-HCM-CENTRAL",
      sourceSystem: "WMS_MANHATTAN",
      lines: [
        {
          id: "grl-01",
          lineNumber: 1,
          itemSku: "SKU-TEA-OOLONG-01",
          receivedQuantity: 100,
          unitOfMeasure: "BAO",
          allocatedQuantity: 100,
          availableQuantity: 0
        }
      ]
    },
    {
      id: "gr-5002",
      receiptNumber: "GR-2026-5002",
      purchaseOrderNumber: "PO-2026-1002",
      supplierCode: "SUP-VINAMILK-ENT",
      receivedAt: new Date(Date.now() - 2592e5).toISOString(),
      status: "Verified",
      warehouseCode: "WH-HCM-COLD-02",
      sourceSystem: "WMS_MANHATTAN",
      lines: [
        {
          id: "grl-02",
          lineNumber: 1,
          itemSku: "SKU-MILK-FRESH-1L",
          receivedQuantity: 500,
          unitOfMeasure: "THUNG",
          allocatedQuantity: 500,
          availableQuantity: 0
        }
      ]
    },
    {
      id: "gr-5003",
      receiptNumber: "GR-2026-5003",
      purchaseOrderNumber: "PO-2026-1003",
      supplierCode: "SUP-TAIWAN-BOBA",
      receivedAt: new Date(Date.now() - 1728e5).toISOString(),
      status: "DamagedReturnRecorded",
      warehouseCode: "WH-HCM-CENTRAL",
      sourceSystem: "WMS_MANHATTAN",
      lines: [
        {
          id: "grl-03",
          lineNumber: 1,
          itemSku: "SKU-BOBA-BLACK-20K",
          receivedQuantity: 160,
          // Shortage: 40 bags damaged during container transport
          unitOfMeasure: "BAO",
          allocatedQuantity: 160,
          availableQuantity: 0
        }
      ]
    },
    {
      id: "gr-5004",
      receiptNumber: "GR-2026-5004",
      purchaseOrderNumber: "PO-2026-1004",
      supplierCode: "SUP-SAIGON-LOGISTICS",
      receivedAt: new Date(Date.now() - 864e5).toISOString(),
      status: "Verified",
      warehouseCode: "WH-BINH-DUONG-HUB",
      sourceSystem: "WMS_MANHATTAN",
      lines: [
        {
          id: "grl-04",
          lineNumber: 1,
          itemSku: "SKU-FILM-SEAL-700",
          receivedQuantity: 1e3,
          unitOfMeasure: "CUON",
          allocatedQuantity: 1e3,
          availableQuantity: 0
        }
      ]
    }
  ];
  const invoices = [
    {
      id: "inv-8801",
      invoiceNumber: "INV-2026-8801",
      purchaseOrderNumber: "PO-2026-1001",
      supplierCode: "SUP-BAO-LOC-TEA",
      supplierName: "HTX Tr\xE0 Cao Nguy\xEAn B\u1EA3o L\u1ED9c",
      currency: "VND",
      invoiceDate: new Date(Date.now() - 2592e5).toISOString(),
      status: "Matched",
      subTotal: 15e6,
      taxAmount: 15e5,
      discountAmount: 0,
      additionalCharges: 0,
      totalAmount: 165e5,
      sourceSystem: "EINVOICE_VNPT",
      lines: [
        {
          id: "invl-01",
          lineNumber: 1,
          itemSku: "SKU-TEA-OOLONG-01",
          description: "Tr\xE0 Oolong Th\u01B0\u1EE3ng H\u1EA1ng (Bao 50kg)",
          invoicedQuantity: 100,
          unitOfMeasure: "BAO",
          unitPrice: 15e4,
          taxRate: 0.1,
          lineTotal: 15e6,
          matchedQuantity: 100
        }
      ]
    },
    {
      id: "inv-8802",
      invoiceNumber: "INV-2026-8802",
      purchaseOrderNumber: "PO-2026-1002",
      supplierCode: "SUP-VINAMILK-ENT",
      supplierName: "C\xF4ng ty C\u1ED5 ph\u1EA7n S\u1EEFa Vi\u1EC7t Nam (Vinamilk B2B)",
      currency: "VND",
      invoiceDate: new Date(Date.now() - 1728e5).toISOString(),
      status: "ApprovedWithVariance",
      subTotal: 141e5,
      // +100,000 VND fuel surcharge on cold chain
      taxAmount: 141e4,
      discountAmount: 0,
      additionalCharges: 1e5,
      totalAmount: 1551e4,
      sourceSystem: "EINVOICE_VIETTEL",
      lines: [
        {
          id: "invl-02",
          lineNumber: 1,
          itemSku: "SKU-MILK-FRESH-1L",
          description: "S\u1EEFa T\u01B0\u01A1i Thanh Tr\xF9ng 100% (Th\xF9ng 12 H\u1ED9p 1L)",
          invoicedQuantity: 500,
          unitOfMeasure: "THUNG",
          unitPrice: 28200,
          taxRate: 0.1,
          lineTotal: 141e5,
          matchedQuantity: 500
        }
      ]
    },
    {
      id: "inv-8803",
      invoiceNumber: "INV-2026-8803",
      purchaseOrderNumber: "PO-2026-1003",
      supplierCode: "SUP-TAIWAN-BOBA",
      supplierName: "C\xF4ng ty TNHH Nguy\xEAn Li\u1EC7u Tr\xE2n Ch\xE2u \u0110\xE0i Loan",
      currency: "VND",
      invoiceDate: new Date(Date.now() - 864e5).toISOString(),
      status: "Discrepancy",
      subTotal: 6e7,
      // Billed for 200 bags, but GR only accepted 160 bags
      taxAmount: 6e6,
      discountAmount: 0,
      additionalCharges: 0,
      totalAmount: 66e6,
      sourceSystem: "EINVOICE_MISA",
      lines: [
        {
          id: "invl-03",
          lineNumber: 1,
          itemSku: "SKU-BOBA-BLACK-20K",
          description: "Tr\xE2n Ch\xE2u \u0110en D\u1EBBo Cao C\u1EA5p (Bao 20kg)",
          invoicedQuantity: 200,
          unitOfMeasure: "BAO",
          unitPrice: 3e5,
          taxRate: 0.1,
          lineTotal: 6e7,
          matchedQuantity: 160
        }
      ]
    },
    {
      id: "inv-8804",
      invoiceNumber: "INV-2026-8804",
      purchaseOrderNumber: "PO-2026-1004",
      supplierCode: "SUP-SAIGON-LOGISTICS",
      supplierName: "Cty TNHH Ti\u1EBFp V\u1EADn & Bao B\xEC S\xE0i G\xF2n",
      currency: "VND",
      invoiceDate: new Date(Date.now() - 432e5).toISOString(),
      status: "Discrepancy",
      subTotal: 52e6,
      // Supplier arbitrarily raised unit price from 45,000 to 52,000 (+15.5%)
      taxAmount: 52e5,
      discountAmount: 0,
      additionalCharges: 0,
      totalAmount: 572e5,
      sourceSystem: "EINVOICE_VNPT",
      lines: [
        {
          id: "invl-04",
          lineNumber: 1,
          itemSku: "SKU-FILM-SEAL-700",
          description: "M\xE0ng \xC9p Mi\u1EC7ng Ly Nh\u1EF1a In Logo (Cu\u1ED9n 2,500 n\u1EAFp)",
          invoicedQuantity: 1e3,
          unitOfMeasure: "CUON",
          unitPrice: 52e3,
          taxRate: 0.1,
          lineTotal: 52e6,
          matchedQuantity: 1e3
        }
      ]
    },
    {
      id: "inv-8805",
      invoiceNumber: "INV-2026-8805",
      purchaseOrderNumber: "PO-2026-1005",
      supplierCode: "SUP-DAIKIN-COLD",
      supplierName: "Daikin Cold Chain Equipment Vietnam",
      currency: "VND",
      invoiceDate: new Date(Date.now() - 144e5).toISOString(),
      status: "PendingReconciliation",
      subTotal: 185e5,
      taxAmount: 185e4,
      discountAmount: 0,
      additionalCharges: 0,
      totalAmount: 2035e4,
      sourceSystem: "EINVOICE_VIETTEL",
      lines: [
        {
          id: "invl-05",
          lineNumber: 1,
          itemSku: "SKU-FILTER-COLD-01",
          description: "B\u1ED9 L\u1ECDc Kh\xF4ng Kh\xED & Kh\u1EED Khu\u1EA9n Kho L\u1EA1nh",
          invoicedQuantity: 10,
          unitOfMeasure: "SET",
          unitPrice: 185e4,
          taxRate: 0.1,
          lineTotal: 185e5,
          matchedQuantity: 0
        }
      ]
    }
  ];
  const discrepancies = [
    {
      id: "disc-01",
      reconciliationResultId: "recon-8803",
      type: "QuantityMismatch",
      severity: "High",
      itemSku: "SKU-BOBA-BLACK-20K",
      expectedValue: "160 BAO (Kho th\u1EF1c nh\u1EADn t\u1EA1i GR-2026-5003)",
      actualValue: "200 BAO (H\xF3a \u0111\u01A1n INV-2026-8803)",
      varianceAmount: 12e6,
      status: "Open",
      details: "Thi\u1EBFu 40 bao tr\xE2n ch\xE2u do r\xE1ch r\u01B0\u1EDBi bao b\xEC khi b\u1ED1c d\u1EE1 t\u1EA1i c\u1ED5ng kho. Nh\xE0 cung c\u1EA5p c\u1EA7n xu\u1EA5t h\xF3a \u0111\u01A1n \u0111i\u1EC1u ch\u1EC9nh (Credit Note) gi\u1EA3m 12,000,000 VND."
    },
    {
      id: "disc-02",
      reconciliationResultId: "recon-8804",
      type: "PriceMismatch",
      severity: "Critical",
      itemSku: "SKU-FILM-SEAL-700",
      expectedValue: "45,000 VND / Cu\u1ED9n (PO-2026-1004)",
      actualValue: "52,000 VND / Cu\u1ED9n (INV-2026-8804)",
      varianceAmount: 7e6,
      status: "UnderReview",
      details: "Nh\xE0 cung c\u1EA5p t\u1EF1 \xFD t\u0103ng \u0111\u01A1n gi\xE1 15.5% m\xE0 kh\xF4ng c\xF3 v\u0103n b\u1EA3n ph\xEA duy\u1EC7t procurement. \u0110\xE3 t\u1EA1m kh\xF3a thanh to\xE1n \u0111\u1EC3 K\u1EBF to\xE1n tr\u01B0\u1EDFng \u0111\u1ED1i so\xE1t."
    },
    {
      id: "disc-03",
      reconciliationResultId: "recon-8802",
      type: "PriceMismatch",
      severity: "Low",
      itemSku: "SKU-MILK-FRESH-1L",
      expectedValue: "28,000 VND / Th\xF9ng",
      actualValue: "28,200 VND / Th\xF9ng",
      varianceAmount: 1e5,
      status: "ApprovedWithVariance",
      details: "Ph\u1EE5 ph\xED container l\u1EA1nh \u0111\u1EB7c th\xF9 trong \u0111\u1EE3t n\u1EAFng n\xF3ng. Ch\xEAnh l\u1EC7ch 0.71% n\u1EB1m trong h\u1EA1n m\u1EE9c dung sai cho ph\xE9p (<1.0%). \u0110\xE3 duy\u1EC7t thanh to\xE1n."
    }
  ];
  const ingestedEvents = [
    {
      id: "evt-9011",
      sourceSystem: "SAP_S4HANA",
      eventType: "PURCHASE_ORDER_ISSUED",
      entityId: "PO-2026-1005",
      payload: '{"orderNumber":"PO-2026-1005","totalAmount":18500000,"lines":1}',
      status: "Processed",
      receivedAt: new Date(Date.now() - 36e4).toISOString(),
      durationMs: 42
    },
    {
      id: "evt-9012",
      sourceSystem: "WMS_MANHATTAN",
      eventType: "GOODS_RECEIPT_POSTED",
      entityId: "GR-2026-5004",
      payload: '{"receiptNumber":"GR-2026-5004","receivedQuantity":1000}',
      status: "Processed",
      receivedAt: new Date(Date.now() - 72e4).toISOString(),
      durationMs: 38
    },
    {
      id: "evt-9013",
      sourceSystem: "EINVOICE_VIETTEL",
      eventType: "INVOICE_XML_INGESTED",
      entityId: "INV-2026-8805",
      payload: '{"invoiceNumber":"INV-2026-8805","vatRate":0.10,"totalAmount":20350000}',
      status: "Processed",
      receivedAt: new Date(Date.now() - 12e5).toISOString(),
      durationMs: 51
    },
    {
      id: "evt-9014",
      sourceSystem: "EINVOICE_VNPT",
      eventType: "INVOICE_XML_INGESTED",
      entityId: "INV-2026-8804",
      payload: '{"invoiceNumber":"INV-2026-8804","unitPrice":52000}',
      status: "FlaggedDiscrepancy",
      receivedAt: new Date(Date.now() - 18e5).toISOString(),
      durationMs: 64
    }
  ];
  const deadLetterEvents = [
    {
      id: "dlq-001",
      eventId: "evt-dlq-corrupted-tax",
      eventType: "INVOICE_HMAC_VALIDATION_FAILED",
      topic: "ap.invoices.raw",
      payload: '{"invoiceNumber":"INV-ERR-9901","supplierCode":"SUP-UNKNOWN","checksum":"invalid_hmac_sha256"}',
      reason: "SecurityException: Invalid HMAC Signature or corrupt webhook header",
      retryCount: 3,
      failedAt: new Date(Date.now() - 144e5).toISOString(),
      isReplayed: false
    },
    {
      id: "dlq-002",
      eventId: "evt-dlq-missing-po-ref",
      eventType: "RECONCILIATION_DEPENDENCY_TIMEOUT",
      topic: "reconciliation.jobs",
      payload: '{"invoiceId":"inv-orphan-01","retryTarget":"SAP_ODATA_GATEWAY"}',
      reason: "TimeoutException: SAP OData Gateway latency > 15,000ms during peak batch",
      retryCount: 4,
      failedAt: new Date(Date.now() - 288e5).toISOString(),
      isReplayed: true,
      replayedAt: new Date(Date.now() - 36e5).toISOString(),
      replayedBy: "admin_system"
    }
  ];
  if (subPath === "/auth/login") {
    let reqBody = {};
    if (context.request.method === "POST") {
      try {
        reqBody = await context.request.json();
      } catch {
      }
    }
    const username = reqBody.username || "alice_finance";
    let role = reqBody.role || "FinanceReviewer";
    if (role.toUpperCase() === "ADMIN") role = "Admin";
    else if (role.toUpperCase() === "OPERATOR") role = "Operator";
    else if (role.toUpperCase().includes("FINANCE")) role = "FinanceReviewer";
    else if (role.toUpperCase().includes("AUDIT")) role = "Auditor";
    const user = {
      id: "usr-" + Math.abs(username.split("").reduce((acc, c) => acc + c.charCodeAt(0), 100)),
      username,
      role,
      email: `${username.toLowerCase()}@opsbridge.internal`
    };
    return new Response(
      JSON.stringify({
        accessToken: "ey-opsbridge-demo-jwt-" + btoa(JSON.stringify({ sub: username, role })),
        tokenType: "Bearer",
        expiresIn: 86400,
        user
      }),
      { headers: jsonHeaders }
    );
  }
  if (subPath === "/auth/google" && context.request.method === "POST") {
    let reqBody = {};
    try {
      reqBody = await context.request.json();
    } catch {
    }
    const credential = reqBody.credential;
    let payload = {};
    if (credential) {
      try {
        const base64Url = credential.split(".")[1];
        const base64 = base64Url.replace(/-/g, "+").replace(/_/g, "/");
        const jsonPayload = decodeURIComponent(
          atob(base64).split("").map((c) => "%" + ("00" + c.charCodeAt(0).toString(16)).slice(-2)).join("")
        );
        payload = JSON.parse(jsonPayload);
      } catch {
        payload = { email: "operator@gmail.com", name: "Google Operator", sub: "google-user-01" };
      }
    }
    const username = payload.name || (payload.email ? payload.email.split("@")[0] : "google_user");
    const user = {
      id: "usr-google-" + (payload.sub || Date.now()),
      username,
      role: "FinanceReviewer",
      email: payload.email || `${username}@gmail.com`,
      picture: payload.picture
    };
    return new Response(
      JSON.stringify({
        accessToken: "ey-opsbridge-google-jwt-" + btoa(JSON.stringify({ sub: username, email: user.email, role: user.role })),
        tokenType: "Bearer",
        expiresIn: 86400,
        user
      }),
      { headers: jsonHeaders }
    );
  }
  if (subPath === "/auth/me") {
    return new Response(
      JSON.stringify({
        id: "usr-01",
        username: "alice_finance",
        role: "FinanceReviewer",
        email: "alice_finance@opsbridge.internal"
      }),
      { headers: jsonHeaders }
    );
  }
  if (subPath === "/dashboard/metrics") {
    return new Response(
      JSON.stringify({
        totalBatches: 52,
        totalInvoices: invoices.length,
        matchedInvoices: invoices.filter((i) => i.status === "Matched" || i.status === "ApprovedWithVariance").length,
        discrepantInvoices: invoices.filter((i) => i.status === "Discrepancy").length,
        openDiscrepancies: discrepancies.filter((d) => d.status === "Open" || d.status === "UnderReview").length,
        activeConnectors: 5,
        deadLetterCount: deadLetterEvents.filter((d) => !d.isReplayed).length
      }),
      { headers: jsonHeaders }
    );
  }
  if (subPath.startsWith("/purchase-orders")) {
    const parts = subPath.split("/").filter(Boolean);
    if (parts.length > 1) {
      const orderParam = parts[1];
      const found = purchaseOrders.find((p) => p.orderNumber === orderParam || p.id === orderParam) || purchaseOrders[0];
      return new Response(JSON.stringify(found), { headers: jsonHeaders });
    }
    if (context.request.method === "POST") {
      let body = {};
      try {
        body = await context.request.json();
      } catch {
      }
      const newPo = {
        id: "po-" + Date.now(),
        orderNumber: body.orderNumber || `PO-2026-${Math.floor(Math.random() * 9e3 + 1e3)}`,
        supplierCode: body.supplierCode || "SUP-GENERAL-01",
        supplierName: body.supplierName || "Nh\xE0 Cung C\u1EA5p T\u1ED5ng H\u1EE3p",
        currency: body.currency || "VND",
        orderedAt: (/* @__PURE__ */ new Date()).toISOString(),
        status: "Issued",
        sourceSystem: "WEB_PORTAL",
        totalAmount: 2e7,
        lines: body.lines || [
          {
            id: "pol-new-01",
            lineNumber: 1,
            itemSku: "SKU-SAMPLE-01",
            description: "Nguy\xEAn v\u1EADt li\u1EC7u m\u1EABu",
            orderedQuantity: 100,
            unitOfMeasure: "PCS",
            unitPrice: 2e5,
            totalAmount: 2e7
          }
        ]
      };
      return new Response(JSON.stringify(newPo), { status: 201, headers: jsonHeaders });
    }
    return new Response(JSON.stringify(purchaseOrders), { headers: jsonHeaders });
  }
  if (subPath.startsWith("/goods-receipts")) {
    const parts = subPath.split("/").filter(Boolean);
    if (parts.length > 1) {
      const grParam = parts[1];
      const found = goodsReceipts.find((g) => g.receiptNumber === grParam || g.id === grParam) || goodsReceipts[0];
      return new Response(JSON.stringify(found), { headers: jsonHeaders });
    }
    if (context.request.method === "POST") {
      let body = {};
      try {
        body = await context.request.json();
      } catch {
      }
      const newGr = {
        id: "gr-" + Date.now(),
        receiptNumber: body.receiptNumber || `GR-2026-${Math.floor(Math.random() * 9e3 + 1e3)}`,
        purchaseOrderNumber: body.purchaseOrderNumber || "PO-2026-1001",
        supplierCode: body.supplierCode || "SUP-BAO-LOC-TEA",
        receivedAt: (/* @__PURE__ */ new Date()).toISOString(),
        status: "Verified",
        warehouseCode: "WH-HCM-CENTRAL",
        sourceSystem: "WEB_PORTAL",
        lines: body.lines || [
          {
            id: "grl-new-01",
            lineNumber: 1,
            itemSku: "SKU-TEA-OOLONG-01",
            receivedQuantity: 100,
            unitOfMeasure: "BAO",
            allocatedQuantity: 100,
            availableQuantity: 0
          }
        ]
      };
      return new Response(JSON.stringify(newGr), { status: 201, headers: jsonHeaders });
    }
    return new Response(JSON.stringify(goodsReceipts), { headers: jsonHeaders });
  }
  if (subPath.startsWith("/invoices")) {
    const parts = subPath.split("/").filter(Boolean);
    if (parts.length > 1) {
      const invParam = parts[1];
      const found = invoices.find((i) => i.id === invParam || i.invoiceNumber === invParam) || invoices[0];
      return new Response(JSON.stringify(found), { headers: jsonHeaders });
    }
    if (context.request.method === "POST") {
      const contentType = context.request.headers.get("content-type") || "";
      if (contentType.includes("text/csv")) {
        return new Response(
          JSON.stringify({ count: 5, message: "Nh\u1EADp th\xE0nh c\xF4ng 5 h\xF3a \u0111\u01A1n t\u1EEB file CSV \u0111\u1ED1i so\xE1t." }),
          { status: 200, headers: jsonHeaders }
        );
      }
      let body = {};
      try {
        body = await context.request.json();
      } catch {
      }
      const newInv = {
        id: "inv-" + Date.now(),
        invoiceNumber: body.invoiceNumber || `INV-2026-${Math.floor(Math.random() * 9e3 + 1e3)}`,
        purchaseOrderNumber: body.purchaseOrderNumber || "PO-2026-1001",
        supplierCode: body.supplierCode || "SUP-BAO-LOC-TEA",
        supplierName: body.supplierName || "HTX Tr\xE0 Cao Nguy\xEAn B\u1EA3o L\u1ED9c",
        currency: body.currency || "VND",
        invoiceDate: (/* @__PURE__ */ new Date()).toISOString(),
        status: "PendingReconciliation",
        subTotal: 15e6,
        taxAmount: 15e5,
        discountAmount: 0,
        additionalCharges: 0,
        totalAmount: 165e5,
        sourceSystem: "WEB_PORTAL",
        lines: body.lines || [
          {
            id: "invl-new-01",
            lineNumber: 1,
            itemSku: "SKU-TEA-OOLONG-01",
            description: "Tr\xE0 Oolong Th\u01B0\u1EE3ng H\u1EA1ng (Bao 50kg)",
            invoicedQuantity: 100,
            unitOfMeasure: "BAO",
            unitPrice: 15e4,
            taxRate: 0.1,
            lineTotal: 15e6,
            matchedQuantity: 0
          }
        ]
      };
      return new Response(JSON.stringify(newInv), { status: 201, headers: jsonHeaders });
    }
    return new Response(JSON.stringify(invoices), { headers: jsonHeaders });
  }
  if (subPath === "/reconciliation/run" && context.request.method === "POST") {
    let body = {};
    try {
      body = await context.request.json();
    } catch {
    }
    const invoiceId = body.invoiceId || "inv-8805";
    const targetInv = invoices.find((i) => i.id === invoiceId) || invoices[0];
    const isMatched = targetInv.id === "inv-8801" || targetInv.id === "inv-8802" || targetInv.id === "inv-8805";
    const reconId = "recon-" + Date.now();
    const orderedQty = 100;
    const receivedQty = isMatched ? 100 : 80;
    const invoicedQty = 100;
    const unitPrice = targetInv.lines[0]?.unitPrice || 15e4;
    const totalVariance = isMatched ? 0 : targetInv.totalAmount * 0.2;
    const reconResult = {
      id: reconId,
      batchId: "batch-live-01",
      purchaseOrderNumber: targetInv.purchaseOrderNumber,
      invoiceNumber: targetInv.invoiceNumber,
      status: isMatched ? "Matched" : "Discrepancy",
      totalQuantityOrdered: orderedQty,
      totalQuantityReceived: receivedQty,
      totalQuantityInvoiced: invoicedQty,
      totalAmountVariance: totalVariance,
      createdAt: (/* @__PURE__ */ new Date()).toISOString(),
      reconciledAt: (/* @__PURE__ */ new Date()).toISOString(),
      reconciledBy: "alice_finance",
      totalInvoicedAmount: targetInv.totalAmount,
      totalMatchedAmount: isMatched ? targetInv.totalAmount : targetInv.totalAmount * 0.8,
      totalVarianceAmount: totalVariance,
      variancePercentage: isMatched ? 0 : 20,
      lines: [
        {
          id: "recon-line-01",
          lineNumber: 1,
          itemSku: targetInv.lines[0]?.itemSku || "SKU-DEFAULT",
          orderedQuantity: orderedQty,
          receivedQuantity: receivedQty,
          invoicedQuantity: invoicedQty,
          matchedQuantity: receivedQty,
          orderedUnitPrice: unitPrice,
          invoicedUnitPrice: unitPrice,
          status: isMatched ? "Matched" : "Discrepancy",
          discrepancyType: isMatched ? void 0 : "QuantityMismatch"
        }
      ],
      allocations: [
        {
          id: "alloc-" + Date.now() + "-1",
          reconciliationResultId: reconId,
          goodsReceiptLineId: "grl-" + (isMatched ? "1001" : "1003"),
          supplierInvoiceLineId: targetInv.lines[0]?.id || "invl-01",
          allocatedQuantity: receivedQty,
          unitPrice,
          allocatedAt: (/* @__PURE__ */ new Date()).toISOString()
        }
      ],
      discrepancies: isMatched ? [] : [
        {
          id: "disc-live-" + Date.now(),
          reconciliationResultId: reconId,
          type: "QuantityMismatch",
          severity: "High",
          itemSku: targetInv.lines[0]?.itemSku || "SKU-DEFAULT",
          expectedValue: "80 BAO (Th\u1EF1c nh\u1EADn t\u1EA1i kho)",
          actualValue: "100 BAO (H\xF3a \u0111\u01A1n NCC y\xEAu c\u1EA7u)",
          varianceAmount: totalVariance,
          status: "Open",
          details: "Ph\xE1t hi\u1EC7n ch\xEAnh l\u1EC7ch 20 \u0111\u01A1n v\u1ECB h\xE0ng h\xF3a gi\u1EEFa th\u1EF1c t\u1EBF ki\u1EC3m \u0111\u1EBFm nh\u1EADp kho (80 bao) v\xE0 h\xF3a \u0111\u01A1n NCC g\u1EEDi sang (100 bao)."
        }
      ]
    };
    return new Response(JSON.stringify(reconResult), { headers: jsonHeaders });
  }
  if (subPath.startsWith("/reconciliation/results/")) {
    const parts = subPath.split("/").filter(Boolean);
    const resultId = parts[2] || "recon-" + Date.now();
    const mockResult = {
      id: resultId,
      batchId: "batch-20261021-01",
      purchaseOrderNumber: "PO-2026-1001",
      invoiceNumber: "INV-2026-8801",
      status: "Matched",
      totalQuantityOrdered: 100,
      totalQuantityReceived: 100,
      totalQuantityInvoiced: 100,
      totalAmountVariance: 0,
      createdAt: (/* @__PURE__ */ new Date()).toISOString(),
      reconciledAt: new Date(Date.now() - 36e5).toISOString(),
      reconciledBy: "system.reconciliation.worker",
      totalInvoicedAmount: 165e5,
      totalMatchedAmount: 165e5,
      totalVarianceAmount: 0,
      variancePercentage: 0,
      lines: [],
      allocations: [
        {
          id: "alloc-prev-01",
          reconciliationResultId: resultId,
          goodsReceiptLineId: "grl-1001",
          supplierInvoiceLineId: "invl-1001",
          allocatedQuantity: 100,
          unitPrice: 15e4,
          allocatedAt: (/* @__PURE__ */ new Date()).toISOString()
        }
      ],
      discrepancies: []
    };
    return new Response(JSON.stringify(mockResult), { headers: jsonHeaders });
  }
  if (subPath.startsWith("/reconciliation/discrepancies")) {
    if (subPath.includes("/review") && context.request.method === "POST") {
      let body = {};
      try {
        body = await context.request.json();
      } catch {
      }
      const reviewDecision = {
        id: "rev-" + Date.now(),
        discrepancyId: subPath.split("/")[2],
        action: body.action || "ApprovedWithTolerance",
        decidedBy: "alice_finance",
        decidedAt: (/* @__PURE__ */ new Date()).toISOString(),
        notes: body.notes || "Ph\xEA duy\u1EC7t dung sai theo \u1EE7y quy\u1EC1n t\xE0i ch\xEDnh."
      };
      return new Response(JSON.stringify(reviewDecision), { headers: jsonHeaders });
    }
    const urlObj = new URL(context.request.url);
    const statusQuery = urlObj.searchParams.get("status");
    let result = discrepancies;
    if (statusQuery && statusQuery !== "ALL") {
      result = discrepancies.filter((d) => d.status === statusQuery);
    }
    return new Response(JSON.stringify(result), { headers: jsonHeaders });
  }
  if (subPath.startsWith("/reconciliation/batches")) {
    return new Response(
      JSON.stringify([
        {
          id: "batch-01",
          batchNumber: "BATCH-20261021-01",
          executedAt: new Date(Date.now() - 72e5).toISOString(),
          status: "Completed",
          totalInvoices: 124,
          matchedCount: 119,
          discrepancyCount: 5,
          totalValue: 245e6,
          initiatedBy: "system.cron"
        },
        {
          id: "batch-02",
          batchNumber: "BATCH-20261020-02",
          executedAt: new Date(Date.now() - 936e5).toISOString(),
          status: "Completed",
          totalInvoices: 98,
          matchedCount: 96,
          discrepancyCount: 2,
          totalValue: 182e6,
          initiatedBy: "alice_finance"
        }
      ]),
      { headers: jsonHeaders }
    );
  }
  if (subPath === "/reconciliation/audit-trail") {
    return new Response(
      JSON.stringify([
        {
          id: "aud-001",
          entityType: "SupplierInvoice",
          entityId: "INV-2026-8801",
          action: "AUTO_MATCHED_3WAY",
          userId: "reconciliation.worker",
          timestamp: new Date(Date.now() - 36e4).toISOString(),
          details: "\u0110\u1ED1i so\xE1t t\u1EF1 \u0111\u1ED9ng 3 b\xEAn ho\xE0n h\u1EA3o gi\u1EEFa PO-2026-1001, GR-2026-5001 v\xE0 h\xF3a \u0111\u01A1n VAT. Ch\xEAnh l\u1EC7ch 0 VND."
        },
        {
          id: "aud-002",
          entityType: "Discrepancy",
          entityId: "DISC-03",
          action: "VARIANCE_APPROVED",
          userId: "alice_finance",
          timestamp: new Date(Date.now() - 12e5).toISOString(),
          details: "Duy\u1EC7t dung sai gi\xE1 0.71% (100,000 VND) cho Vinamilk B2B do ph\xE1t sinh b\u1EA3o qu\u1EA3n l\u1EA1nh theo h\u1EE3p \u0111\u1ED3ng khung."
        },
        {
          id: "aud-003",
          entityType: "DeadLetterEvent",
          entityId: "evt-dlq-missing-po-ref",
          action: "EVENT_REPLAYED",
          userId: "admin_system",
          timestamp: new Date(Date.now() - 36e5).toISOString(),
          details: "Th\u1EF1c hi\u1EC7n replay webhook OData sau khi c\u1EA5u h\xECnh l\u1EA1i timeout m\u1EA1ng 30s."
        },
        {
          id: "aud-004",
          entityType: "SupplierInvoice",
          entityId: "INV-2026-8804",
          action: "PAYMENT_LOCKED",
          userId: "system.invariant.guard",
          timestamp: new Date(Date.now() - 72e5).toISOString(),
          details: "T\u1EF1 \u0111\u1ED9ng kh\xF3a l\u1EC7nh chi tr\u1EA3: \u0110\u01A1n gi\xE1 h\xF3a \u0111\u01A1n (52,000 VND) v\u01B0\u1EE3t gi\xE1 PO k\xFD k\u1EBFt (45,000 VND) t\u1EDBi 15.5%."
        },
        {
          id: "aud-005",
          entityType: "PurchaseOrder",
          entityId: "PO-2026-1005",
          action: "PO_APPROVED",
          userId: "procurement.manager",
          timestamp: new Date(Date.now() - 144e5).toISOString(),
          details: "Ph\xEA duy\u1EC7t ng\xE2n s\xE1ch mua s\u1EAFm thi\u1EBFt b\u1ECB kho Daikin 18,500,000 VND."
        }
      ]),
      { headers: jsonHeaders }
    );
  }
  if (subPath.startsWith("/connectors")) {
    return new Response(
      JSON.stringify([
        { id: "conn-01", name: "SAP S/4HANA OData v4", type: "ERP", status: "Healthy", lastSyncAt: new Date(Date.now() - 12e4).toISOString() },
        { id: "conn-02", name: "Oracle NetSuite REST API", type: "ERP", status: "Healthy", lastSyncAt: new Date(Date.now() - 18e4).toISOString() },
        { id: "conn-03", name: "VNPT / Viettel e-Invoice Connector", type: "EInvoice", status: "Healthy", lastSyncAt: new Date(Date.now() - 6e4).toISOString() },
        { id: "conn-04", name: "Manhattan WMS Warehouse Stream", type: "WMS", status: "Healthy", lastSyncAt: new Date(Date.now() - 9e4).toISOString() },
        { id: "conn-05", name: "RabbitMQ Event Mesh (ap.invoices.raw)", type: "MessageBroker", status: "Active", lastSyncAt: (/* @__PURE__ */ new Date()).toISOString() }
      ]),
      { headers: jsonHeaders }
    );
  }
  if (subPath.startsWith("/ingestion/events")) {
    return new Response(JSON.stringify(ingestedEvents), { headers: jsonHeaders });
  }
  if (subPath.startsWith("/events/dead-letter")) {
    return new Response(JSON.stringify(deadLetterEvents), { headers: jsonHeaders });
  }
  if (subPath.startsWith("/events/replay/")) {
    const parts = subPath.split("/").filter(Boolean);
    const eventId = parts[2] || "evt-unknown";
    return new Response(
      JSON.stringify({
        message: `S\u1EF1 ki\u1EC7n ${eventId} \u0111\xE3 \u0111\u01B0\u1EE3c Replay th\xE0nh c\xF4ng v\xE0o h\xE0ng \u0111\u1EE3i t\xE1i x\u1EED l\xFD.`,
        replayedBy: "alice_finance",
        replayedAt: (/* @__PURE__ */ new Date()).toISOString()
      }),
      { status: 200, headers: jsonHeaders }
    );
  }
  return new Response(
    JSON.stringify({
      status: "healthy",
      service: "OpsBridge Enterprise Telemetry",
      route: url.pathname,
      timestamp: (/* @__PURE__ */ new Date()).toISOString()
    }),
    { headers: jsonHeaders }
  );
}, "onRequest");

// ../.wrangler/tmp/pages-OwwACk/functionsRoutes-0.653327050133954.mjs
var routes = [
  {
    routePath: "/api/v1/:path*",
    mountPath: "/api/v1",
    method: "",
    middlewares: [],
    modules: [onRequest]
  }
];

// ../../../../../../../opt/homebrew/lib/node_modules/wrangler/node_modules/path-to-regexp/dist.es2015/index.js
function lexer(str) {
  var tokens = [];
  var i = 0;
  while (i < str.length) {
    var char = str[i];
    if (char === "*" || char === "+" || char === "?") {
      tokens.push({ type: "MODIFIER", index: i, value: str[i++] });
      continue;
    }
    if (char === "\\") {
      tokens.push({ type: "ESCAPED_CHAR", index: i++, value: str[i++] });
      continue;
    }
    if (char === "{") {
      tokens.push({ type: "OPEN", index: i, value: str[i++] });
      continue;
    }
    if (char === "}") {
      tokens.push({ type: "CLOSE", index: i, value: str[i++] });
      continue;
    }
    if (char === ":") {
      var name = "";
      var j = i + 1;
      while (j < str.length) {
        var code = str.charCodeAt(j);
        if (
          // `0-9`
          code >= 48 && code <= 57 || // `A-Z`
          code >= 65 && code <= 90 || // `a-z`
          code >= 97 && code <= 122 || // `_`
          code === 95
        ) {
          name += str[j++];
          continue;
        }
        break;
      }
      if (!name)
        throw new TypeError("Missing parameter name at ".concat(i));
      tokens.push({ type: "NAME", index: i, value: name });
      i = j;
      continue;
    }
    if (char === "(") {
      var count = 1;
      var pattern = "";
      var j = i + 1;
      if (str[j] === "?") {
        throw new TypeError('Pattern cannot start with "?" at '.concat(j));
      }
      while (j < str.length) {
        if (str[j] === "\\") {
          pattern += str[j++] + str[j++];
          continue;
        }
        if (str[j] === ")") {
          count--;
          if (count === 0) {
            j++;
            break;
          }
        } else if (str[j] === "(") {
          count++;
          if (str[j + 1] !== "?") {
            throw new TypeError("Capturing groups are not allowed at ".concat(j));
          }
        }
        pattern += str[j++];
      }
      if (count)
        throw new TypeError("Unbalanced pattern at ".concat(i));
      if (!pattern)
        throw new TypeError("Missing pattern at ".concat(i));
      tokens.push({ type: "PATTERN", index: i, value: pattern });
      i = j;
      continue;
    }
    tokens.push({ type: "CHAR", index: i, value: str[i++] });
  }
  tokens.push({ type: "END", index: i, value: "" });
  return tokens;
}
__name(lexer, "lexer");
function parse(str, options) {
  if (options === void 0) {
    options = {};
  }
  var tokens = lexer(str);
  var _a = options.prefixes, prefixes = _a === void 0 ? "./" : _a, _b = options.delimiter, delimiter = _b === void 0 ? "/#?" : _b;
  var result = [];
  var key = 0;
  var i = 0;
  var path = "";
  var tryConsume = /* @__PURE__ */ __name(function(type) {
    if (i < tokens.length && tokens[i].type === type)
      return tokens[i++].value;
  }, "tryConsume");
  var mustConsume = /* @__PURE__ */ __name(function(type) {
    var value2 = tryConsume(type);
    if (value2 !== void 0)
      return value2;
    var _a2 = tokens[i], nextType = _a2.type, index = _a2.index;
    throw new TypeError("Unexpected ".concat(nextType, " at ").concat(index, ", expected ").concat(type));
  }, "mustConsume");
  var consumeText = /* @__PURE__ */ __name(function() {
    var result2 = "";
    var value2;
    while (value2 = tryConsume("CHAR") || tryConsume("ESCAPED_CHAR")) {
      result2 += value2;
    }
    return result2;
  }, "consumeText");
  var isSafe = /* @__PURE__ */ __name(function(value2) {
    for (var _i = 0, delimiter_1 = delimiter; _i < delimiter_1.length; _i++) {
      var char2 = delimiter_1[_i];
      if (value2.indexOf(char2) > -1)
        return true;
    }
    return false;
  }, "isSafe");
  var safePattern = /* @__PURE__ */ __name(function(prefix2) {
    var prev = result[result.length - 1];
    var prevText = prefix2 || (prev && typeof prev === "string" ? prev : "");
    if (prev && !prevText) {
      throw new TypeError('Must have text between two parameters, missing text after "'.concat(prev.name, '"'));
    }
    if (!prevText || isSafe(prevText))
      return "[^".concat(escapeString(delimiter), "]+?");
    return "(?:(?!".concat(escapeString(prevText), ")[^").concat(escapeString(delimiter), "])+?");
  }, "safePattern");
  while (i < tokens.length) {
    var char = tryConsume("CHAR");
    var name = tryConsume("NAME");
    var pattern = tryConsume("PATTERN");
    if (name || pattern) {
      var prefix = char || "";
      if (prefixes.indexOf(prefix) === -1) {
        path += prefix;
        prefix = "";
      }
      if (path) {
        result.push(path);
        path = "";
      }
      result.push({
        name: name || key++,
        prefix,
        suffix: "",
        pattern: pattern || safePattern(prefix),
        modifier: tryConsume("MODIFIER") || ""
      });
      continue;
    }
    var value = char || tryConsume("ESCAPED_CHAR");
    if (value) {
      path += value;
      continue;
    }
    if (path) {
      result.push(path);
      path = "";
    }
    var open = tryConsume("OPEN");
    if (open) {
      var prefix = consumeText();
      var name_1 = tryConsume("NAME") || "";
      var pattern_1 = tryConsume("PATTERN") || "";
      var suffix = consumeText();
      mustConsume("CLOSE");
      result.push({
        name: name_1 || (pattern_1 ? key++ : ""),
        pattern: name_1 && !pattern_1 ? safePattern(prefix) : pattern_1,
        prefix,
        suffix,
        modifier: tryConsume("MODIFIER") || ""
      });
      continue;
    }
    mustConsume("END");
  }
  return result;
}
__name(parse, "parse");
function match(str, options) {
  var keys = [];
  var re = pathToRegexp(str, keys, options);
  return regexpToFunction(re, keys, options);
}
__name(match, "match");
function regexpToFunction(re, keys, options) {
  if (options === void 0) {
    options = {};
  }
  var _a = options.decode, decode = _a === void 0 ? function(x) {
    return x;
  } : _a;
  return function(pathname) {
    var m = re.exec(pathname);
    if (!m)
      return false;
    var path = m[0], index = m.index;
    var params = /* @__PURE__ */ Object.create(null);
    var _loop_1 = /* @__PURE__ */ __name(function(i2) {
      if (m[i2] === void 0)
        return "continue";
      var key = keys[i2 - 1];
      if (key.modifier === "*" || key.modifier === "+") {
        params[key.name] = m[i2].split(key.prefix + key.suffix).map(function(value) {
          return decode(value, key);
        });
      } else {
        params[key.name] = decode(m[i2], key);
      }
    }, "_loop_1");
    for (var i = 1; i < m.length; i++) {
      _loop_1(i);
    }
    return { path, index, params };
  };
}
__name(regexpToFunction, "regexpToFunction");
function escapeString(str) {
  return str.replace(/([.+*?=^!:${}()[\]|/\\])/g, "\\$1");
}
__name(escapeString, "escapeString");
function flags(options) {
  return options && options.sensitive ? "" : "i";
}
__name(flags, "flags");
function regexpToRegexp(path, keys) {
  if (!keys)
    return path;
  var groupsRegex = /\((?:\?<(.*?)>)?(?!\?)/g;
  var index = 0;
  var execResult = groupsRegex.exec(path.source);
  while (execResult) {
    keys.push({
      // Use parenthesized substring match if available, index otherwise
      name: execResult[1] || index++,
      prefix: "",
      suffix: "",
      modifier: "",
      pattern: ""
    });
    execResult = groupsRegex.exec(path.source);
  }
  return path;
}
__name(regexpToRegexp, "regexpToRegexp");
function arrayToRegexp(paths, keys, options) {
  var parts = paths.map(function(path) {
    return pathToRegexp(path, keys, options).source;
  });
  return new RegExp("(?:".concat(parts.join("|"), ")"), flags(options));
}
__name(arrayToRegexp, "arrayToRegexp");
function stringToRegexp(path, keys, options) {
  return tokensToRegexp(parse(path, options), keys, options);
}
__name(stringToRegexp, "stringToRegexp");
function tokensToRegexp(tokens, keys, options) {
  if (options === void 0) {
    options = {};
  }
  var _a = options.strict, strict = _a === void 0 ? false : _a, _b = options.start, start = _b === void 0 ? true : _b, _c = options.end, end = _c === void 0 ? true : _c, _d = options.encode, encode = _d === void 0 ? function(x) {
    return x;
  } : _d, _e = options.delimiter, delimiter = _e === void 0 ? "/#?" : _e, _f = options.endsWith, endsWith = _f === void 0 ? "" : _f;
  var endsWithRe = "[".concat(escapeString(endsWith), "]|$");
  var delimiterRe = "[".concat(escapeString(delimiter), "]");
  var route = start ? "^" : "";
  for (var _i = 0, tokens_1 = tokens; _i < tokens_1.length; _i++) {
    var token = tokens_1[_i];
    if (typeof token === "string") {
      route += escapeString(encode(token));
    } else {
      var prefix = escapeString(encode(token.prefix));
      var suffix = escapeString(encode(token.suffix));
      if (token.pattern) {
        if (keys)
          keys.push(token);
        if (prefix || suffix) {
          if (token.modifier === "+" || token.modifier === "*") {
            var mod = token.modifier === "*" ? "?" : "";
            route += "(?:".concat(prefix, "((?:").concat(token.pattern, ")(?:").concat(suffix).concat(prefix, "(?:").concat(token.pattern, "))*)").concat(suffix, ")").concat(mod);
          } else {
            route += "(?:".concat(prefix, "(").concat(token.pattern, ")").concat(suffix, ")").concat(token.modifier);
          }
        } else {
          if (token.modifier === "+" || token.modifier === "*") {
            throw new TypeError('Can not repeat "'.concat(token.name, '" without a prefix and suffix'));
          }
          route += "(".concat(token.pattern, ")").concat(token.modifier);
        }
      } else {
        route += "(?:".concat(prefix).concat(suffix, ")").concat(token.modifier);
      }
    }
  }
  if (end) {
    if (!strict)
      route += "".concat(delimiterRe, "?");
    route += !options.endsWith ? "$" : "(?=".concat(endsWithRe, ")");
  } else {
    var endToken = tokens[tokens.length - 1];
    var isEndDelimited = typeof endToken === "string" ? delimiterRe.indexOf(endToken[endToken.length - 1]) > -1 : endToken === void 0;
    if (!strict) {
      route += "(?:".concat(delimiterRe, "(?=").concat(endsWithRe, "))?");
    }
    if (!isEndDelimited) {
      route += "(?=".concat(delimiterRe, "|").concat(endsWithRe, ")");
    }
  }
  return new RegExp(route, flags(options));
}
__name(tokensToRegexp, "tokensToRegexp");
function pathToRegexp(path, keys, options) {
  if (path instanceof RegExp)
    return regexpToRegexp(path, keys);
  if (Array.isArray(path))
    return arrayToRegexp(path, keys, options);
  return stringToRegexp(path, keys, options);
}
__name(pathToRegexp, "pathToRegexp");

// ../../../../../../../opt/homebrew/lib/node_modules/wrangler/templates/pages-template-worker.ts
var escapeRegex = /[.+?^${}()|[\]\\]/g;
function* executeRequest(request) {
  const requestPath = new URL(request.url).pathname;
  for (const route of [...routes].reverse()) {
    if (route.method && route.method !== request.method) {
      continue;
    }
    const routeMatcher = match(route.routePath.replace(escapeRegex, "\\$&"), {
      end: false
    });
    const mountMatcher = match(route.mountPath.replace(escapeRegex, "\\$&"), {
      end: false
    });
    const matchResult = routeMatcher(requestPath);
    const mountMatchResult = mountMatcher(requestPath);
    if (matchResult && mountMatchResult) {
      for (const handler of route.middlewares.flat()) {
        yield {
          handler,
          params: matchResult.params,
          path: mountMatchResult.path
        };
      }
    }
  }
  for (const route of routes) {
    if (route.method && route.method !== request.method) {
      continue;
    }
    const routeMatcher = match(route.routePath.replace(escapeRegex, "\\$&"), {
      end: true
    });
    const mountMatcher = match(route.mountPath.replace(escapeRegex, "\\$&"), {
      end: false
    });
    const matchResult = routeMatcher(requestPath);
    const mountMatchResult = mountMatcher(requestPath);
    if (matchResult && mountMatchResult && route.modules.length) {
      for (const handler of route.modules.flat()) {
        yield {
          handler,
          params: matchResult.params,
          path: matchResult.path
        };
      }
      break;
    }
  }
}
__name(executeRequest, "executeRequest");
var pages_template_worker_default = {
  async fetch(originalRequest, env, workerContext) {
    let request = originalRequest;
    const handlerIterator = executeRequest(request);
    let data = {};
    let isFailOpen = false;
    const next = /* @__PURE__ */ __name(async (input, init) => {
      if (input !== void 0) {
        let url = input;
        if (typeof input === "string") {
          url = new URL(input, request.url).toString();
        }
        request = new Request(url, init);
      }
      const result = handlerIterator.next();
      if (result.done === false) {
        const { handler, params, path } = result.value;
        const context = {
          request: new Request(request.clone()),
          functionPath: path,
          next,
          params,
          get data() {
            return data;
          },
          set data(value) {
            if (typeof value !== "object" || value === null) {
              throw new Error("context.data must be an object");
            }
            data = value;
          },
          env,
          waitUntil: workerContext.waitUntil.bind(workerContext),
          passThroughOnException: /* @__PURE__ */ __name(() => {
            isFailOpen = true;
          }, "passThroughOnException")
        };
        const response = await handler(context);
        if (!(response instanceof Response)) {
          throw new Error("Your Pages function should return a Response");
        }
        return cloneResponse(response);
      } else if ("ASSETS") {
        const response = await env["ASSETS"].fetch(request);
        return cloneResponse(response);
      } else {
        const response = await fetch(request);
        return cloneResponse(response);
      }
    }, "next");
    try {
      return await next();
    } catch (error) {
      if (isFailOpen) {
        const response = await env["ASSETS"].fetch(request);
        return cloneResponse(response);
      }
      throw error;
    }
  }
};
var cloneResponse = /* @__PURE__ */ __name((response) => (
  // https://fetch.spec.whatwg.org/#null-body-status
  new Response(
    [101, 204, 205, 304].includes(response.status) ? null : response.body,
    response
  )
), "cloneResponse");
export {
  pages_template_worker_default as default
};
