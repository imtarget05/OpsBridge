export type UserRole = 'Admin' | 'Operator' | 'FinanceReviewer' | 'Auditor';

export interface User {
  id: string;
  username: string;
  role: UserRole;
  email: string;
}

export interface AuthResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
  user: User;
}

export type InvoiceStatus =
  | 'Draft'
  | 'PendingReconciliation'
  | 'PartiallyMatched'
  | 'Matched'
  | 'Discrepancy'
  | 'Approved'
  | 'ApprovedWithVariance'
  | 'Rejected'
  | 'Cancelled';

export interface PurchaseOrderLine {
  id: string;
  lineNumber: number;
  itemSku: string;
  description: string;
  orderedQuantity: number;
  unitOfMeasure: string;
  unitPrice: number;
  totalAmount: number;
}

export interface PurchaseOrder {
  id: string;
  orderNumber: string;
  supplierCode: string;
  supplierName: string;
  currency: string;
  orderedAt: string;
  status: string;
  sourceSystem: string;
  totalAmount: number;
  lines: PurchaseOrderLine[];
}

export interface GoodsReceiptLine {
  id: string;
  lineNumber: number;
  itemSku: string;
  receivedQuantity: number;
  unitOfMeasure: string;
  allocatedQuantity: number;
  availableQuantity: number;
}

export interface GoodsReceipt {
  id: string;
  receiptNumber: string;
  purchaseOrderNumber: string;
  supplierCode: string;
  receivedAt: string;
  sourceEventId?: string;
  sourceSystem: string;
  lines: GoodsReceiptLine[];
}

export interface SupplierInvoiceLine {
  id: string;
  lineNumber: number;
  itemSku: string;
  description: string;
  invoicedQuantity: number;
  unitOfMeasure: string;
  unitPrice: number;
  taxRate: number;
  lineTotal: number;
  matchedQuantity: number;
}

export interface SupplierInvoice {
  id: string;
  invoiceNumber: string;
  purchaseOrderNumber: string;
  supplierCode: string;
  supplierName: string;
  currency: string;
  invoiceDate: string;
  status: InvoiceStatus;
  subTotal: number;
  taxAmount: number;
  discountAmount: number;
  additionalCharges: number;
  totalAmount: number;
  sourceSystem: string;
  lines: SupplierInvoiceLine[];
}

export type DiscrepancyType =
  | 'QuantityMismatch'
  | 'PriceMismatch'
  | 'MissingPurchaseOrder'
  | 'MissingGoodsReceipt'
  | 'UnmatchedInvoiceLine'
  | 'DuplicateInvoice'
  | 'CurrencyMismatch'
  | 'SupplierMismatch'
  | 'UomConversionError';

export type DiscrepancySeverity = 'Warning' | 'Error';
export type DiscrepancyStatus = 'Open' | 'UnderReview' | 'Resolved' | 'Rejected' | 'ApprovedWithVariance';
export type ReviewAction = 'ApprovedWithTolerance' | 'Adjusted' | 'Rejected' | 'HoldForInvestigation';

export interface ReviewDecision {
  id: string;
  discrepancyId: string;
  action: ReviewAction;
  reviewerUserId: string;
  reviewerRole: string;
  notes: string;
  decidedAt: string;
}

export interface Discrepancy {
  id: string;
  reconciliationResultId: string;
  type: DiscrepancyType;
  severity: DiscrepancySeverity;
  itemSku: string;
  expectedValue: string;
  actualValue: string;
  varianceAmount: number;
  status: DiscrepancyStatus;
  details: string;
  reviewDecision?: ReviewDecision;
}

export interface ReconciliationAllocation {
  id: string;
  reconciliationResultId: string;
  goodsReceiptLineId: string;
  supplierInvoiceLineId: string;
  allocatedQuantity: number;
  unitPrice: number;
  allocatedAt: string;
}

export type MatchStatus = 'Matched' | 'Discrepancy';

export interface ReconciliationResult {
  id: string;
  batchId?: string;
  purchaseOrderNumber: string;
  invoiceNumber: string;
  status: MatchStatus;
  totalQuantityOrdered: number;
  totalQuantityReceived: number;
  totalQuantityInvoiced: number;
  totalAmountVariance: number;
  createdAt: string;
  allocations: ReconciliationAllocation[];
  discrepancies: Discrepancy[];
}

export interface ReconciliationBatch {
  id: string;
  batchNumber: string;
  createdAt: string;
  executedAt?: string;
  status: string;
  initiatedBy: string;
  results: ReconciliationResult[];
}

export interface DashboardMetrics {
  totalBatches: number;
  totalInvoices: number;
  matchedInvoices: number;
  discrepantInvoices: number;
  openDiscrepancies: number;
  activeConnectors: number;
  deadLetterCount: number;
}

export interface AuditEvent {
  id: string;
  entityType: string;
  entityId: string;
  action: string;
  userId: string;
  timestamp: string;
  details: string;
}

export interface DeadLetterEvent {
  id: string;
  eventId: string;
  eventType: string;
  topic: string;
  payload: string;
  reason: string;
  retryCount: number;
  failedAt: string;
  isReplayed: boolean;
  replayedAt?: string;
  replayedBy?: string;
}
