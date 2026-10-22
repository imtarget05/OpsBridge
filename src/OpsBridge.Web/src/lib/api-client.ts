import type {
  AuditEvent,
  AuthResponse,
  DashboardMetrics,
  DeadLetterEvent,
  Discrepancy,
  DiscrepancyStatus,
  GoodsReceipt,
  PurchaseOrder,
  ReconciliationBatch,
  ReconciliationResult,
  ReviewAction,
  ReviewDecision,
  SupplierInvoice,
  User,
  UserRole,
} from '../types';

const API_BASE = '/api/v1';

class ApiClient {
  private token: string | null = null;

  constructor() {
    this.token = localStorage.getItem('opsbridge_token');
  }

  setToken(token: string | null) {
    this.token = token;
    if (token) {
      localStorage.setItem('opsbridge_token', token);
    } else {
      localStorage.removeItem('opsbridge_token');
    }
  }

  getToken(): string | null {
    return this.token;
  }

  private async request<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
    const headers: Record<string, string> = {
      ...(options.headers as Record<string, string>),
    };

    if (this.token) {
      headers['Authorization'] = `Bearer ${this.token}`;
    }

    if (!(options.body instanceof FormData) && !headers['Content-Type']) {
      headers['Content-Type'] = 'application/json';
    }

    const res = await fetch(`${API_BASE}${endpoint}`, {
      ...options,
      headers,
    });

    if (res.status === 401) {
      // Auto logout on 401
      this.setToken(null);
      if (window.location.pathname !== '/login') {
        window.location.href = '/login';
      }
      throw new Error('Unauthorized');
    }

    if (!res.ok) {
      let errorMessage = `HTTP ${res.status}: ${res.statusText}`;
      try {
        const errJson = await res.json();
        errorMessage = errJson.message || errJson.error || errorMessage;
      } catch {
        // Fallback to status text
      }
      throw new Error(errorMessage);
    }

    // Handle 204 No Content
    if (res.status === 204) {
      return {} as T;
    }

    return res.json() as Promise<T>;
  }

  // --- Auth ---
  async login(username: string, role?: UserRole): Promise<AuthResponse> {
    const res = await this.request<AuthResponse>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username, role }),
    });
    this.setToken(res.accessToken);
    return res;
  }

  async loginWithGoogle(credential: string): Promise<AuthResponse> {
    const res = await this.request<AuthResponse>('/auth/google', {
      method: 'POST',
      body: JSON.stringify({ credential }),
    });
    this.setToken(res.accessToken);
    return res;
  }

  async getMe(): Promise<User> {
    return this.request<User>('/auth/me');
  }

  logout() {
    this.setToken(null);
  }

  // --- Dashboard Metrics ---
  async getDashboardMetrics(): Promise<DashboardMetrics> {
    return this.request<DashboardMetrics>('/dashboard/metrics');
  }

  // --- Purchase Orders ---
  async getPurchaseOrders(): Promise<PurchaseOrder[]> {
    return this.request<PurchaseOrder[]>('/purchase-orders');
  }

  async getPurchaseOrder(orderNumber: string): Promise<PurchaseOrder> {
    return this.request<PurchaseOrder>(`/purchase-orders/${orderNumber}`);
  }

  async createPurchaseOrder(data: {
    orderNumber: string;
    supplierCode: string;
    supplierName: string;
    currency?: string;
    lines: Array<{
      lineNumber: number;
      itemSku: string;
      description?: string;
      orderedQuantity: number;
      unitOfMeasure?: string;
      unitPrice: number;
    }>;
  }): Promise<PurchaseOrder> {
    return this.request<PurchaseOrder>('/purchase-orders', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  async importPurchaseOrdersCsv(csvContent: string): Promise<{ count: number; message: string }> {
    return this.request<{ count: number; message: string }>('/purchase-orders', {
      method: 'POST',
      headers: { 'Content-Type': 'text/csv' },
      body: csvContent,
    });
  }

  // --- Goods Receipts ---
  async getGoodsReceipts(): Promise<GoodsReceipt[]> {
    return this.request<GoodsReceipt[]>('/goods-receipts');
  }

  async getGoodsReceipt(receiptNumber: string): Promise<GoodsReceipt> {
    return this.request<GoodsReceipt>(`/goods-receipts/${receiptNumber}`);
  }

  async createGoodsReceipt(data: {
    receiptNumber: string;
    purchaseOrderNumber: string;
    supplierCode: string;
    receivedAt?: string;
    lines: Array<{
      lineNumber: number;
      itemSku: string;
      receivedQuantity: number;
      unitOfMeasure?: string;
    }>;
  }): Promise<GoodsReceipt> {
    return this.request<GoodsReceipt>('/goods-receipts', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  // --- Invoices ---
  async getInvoices(): Promise<SupplierInvoice[]> {
    return this.request<SupplierInvoice[]>('/invoices');
  }

  async getInvoice(id: string): Promise<SupplierInvoice> {
    return this.request<SupplierInvoice>(`/invoices/${id}`);
  }

  async createInvoice(data: {
    invoiceNumber: string;
    purchaseOrderNumber: string;
    supplierCode: string;
    supplierName: string;
    currency?: string;
    lines: Array<{
      lineNumber: number;
      itemSku: string;
      description?: string;
      invoicedQuantity: number;
      unitOfMeasure?: string;
      unitPrice: number;
      taxRate: number;
    }>;
  }): Promise<SupplierInvoice> {
    return this.request<SupplierInvoice>('/invoices', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  async importInvoicesCsv(csvContent: string): Promise<{ count: number; message: string }> {
    return this.request<{ count: number; message: string }>('/invoices', {
      method: 'POST',
      headers: { 'Content-Type': 'text/csv' },
      body: csvContent,
    });
  }

  // --- Reconciliation ---
  async runReconciliation(data: {
    invoiceId: string;
    priceTolerancePercentage?: number;
    quantityTolerancePercentage?: number;
    quantityToleranceAbsolute?: number;
  }): Promise<ReconciliationResult> {
    return this.request<ReconciliationResult>('/reconciliation/run', {
      method: 'POST',
      body: JSON.stringify(data),
    });
  }

  async getReconciliationResult(id: string): Promise<ReconciliationResult> {
    return this.request<ReconciliationResult>(`/reconciliation/results/${id}`);
  }

  async getDiscrepancies(status?: DiscrepancyStatus): Promise<Discrepancy[]> {
    const query = status ? `?status=${status}` : '';
    return this.request<Discrepancy[]>(`/reconciliation/discrepancies${query}`);
  }

  async reviewDiscrepancy(
    discrepancyId: string,
    action: ReviewAction,
    notes?: string
  ): Promise<ReviewDecision> {
    return this.request<ReviewDecision>(`/reconciliation/discrepancies/${discrepancyId}/review`, {
      method: 'POST',
      body: JSON.stringify({ action, notes }),
    });
  }

  async getAuditTrail(): Promise<AuditEvent[]> {
    return this.request<AuditEvent[]>('/reconciliation/audit-trail');
  }

  async getBatches(): Promise<ReconciliationBatch[]> {
    return this.request<ReconciliationBatch[]>('/reconciliation/batches');
  }

  // --- Integration & DLQ ---
  async getConnectors(): Promise<any[]> {
    return this.request<any[]>('/connectors');
  }

  async getIngestedEvents(): Promise<any[]> {
    return this.request<any[]>('/ingestion/events');
  }

  async getDeadLetterEvents(): Promise<DeadLetterEvent[]> {
    return this.request<DeadLetterEvent[]>('/events/dead-letter');
  }

  async replayEvent(id: string): Promise<{ message: string; replayedBy: string }> {
    return this.request<{ message: string; replayedBy: string }>(`/events/replay/${id}`, {
      method: 'POST',
    });
  }
}

export const api = new ApiClient();
