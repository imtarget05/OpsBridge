import { useState, useEffect } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api-client';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/Card';
import { Button } from '../../components/ui/Button';
import { Input } from '../../components/ui/Input';
import { Select } from '../../components/ui/Select';
import { StatusBadge, DiscrepancySeverityBadge } from '../../components/ui/Badge';
import { formatCurrency, formatDate } from '../../lib/formatters';
import type { ReconciliationResult } from '../../types';
import {
  GitCompare,
  Play,
  CheckCircle2,
  AlertTriangle,
  ArrowRight,
  Sliders,
  FileText,
  Truck,
  Receipt,
  ShieldCheck,
} from 'lucide-react';

export function MatchingWorkspacePage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const invoiceIdParam = searchParams.get('invoiceId') || '';
  const [selectedInvoiceId, setSelectedInvoiceId] = useState<string>(invoiceIdParam);

  // Tolerance Policy State
  const [priceTolerancePct, setPriceTolerancePct] = useState('1.0');
  const [qtyTolerancePct, setQtyTolerancePct] = useState('0.0');
  const [qtyToleranceAbs, setQtyToleranceAbs] = useState('0.0');

  // Load All Invoices for selection
  const { data: invoices, isLoading: loadingInvoices } = useQuery({
    queryKey: ['invoices'],
    queryFn: () => api.getInvoices(),
  });

  // Keep selectedInvoiceId in sync with query parameter
  useEffect(() => {
    if (invoiceIdParam) {
      setSelectedInvoiceId(invoiceIdParam);
    } else if (invoices && invoices.length > 0 && !selectedInvoiceId) {
      // Pick first pending or first invoice
      const pending = invoices.find((i) => i.status === 'PendingReconciliation');
      const targetId = (pending || invoices[0]).id;
      setSelectedInvoiceId(targetId);
      setSearchParams({ invoiceId: targetId });
    }
  }, [invoiceIdParam, invoices]);

  // Selected Invoice
  const selectedInvoice = invoices?.find((i) => i.id === selectedInvoiceId);

  // Associated PO
  const { data: po, isLoading: loadingPo } = useQuery({
    queryKey: ['purchase-order', selectedInvoice?.purchaseOrderNumber],
    queryFn: () =>
      selectedInvoice ? api.getPurchaseOrder(selectedInvoice.purchaseOrderNumber) : null,
    enabled: !!selectedInvoice?.purchaseOrderNumber,
  });

  // Associated Goods Receipts
  const { data: allGrs, isLoading: loadingGrs } = useQuery({
    queryKey: ['goods-receipts'],
    queryFn: () => api.getGoodsReceipts(),
  });

  const matchingGrs = (allGrs || []).filter(
    (gr) => selectedInvoice && gr.purchaseOrderNumber === selectedInvoice.purchaseOrderNumber
  );

  // Reconciliation Run Result state
  const [reconResult, setReconResult] = useState<ReconciliationResult | null>(null);

  const runMutation = useMutation({
    mutationFn: (data: {
      invoiceId: string;
      priceTolerancePercentage: number;
      quantityTolerancePercentage: number;
      quantityToleranceAbsolute: number;
    }) => api.runReconciliation(data),
    onSuccess: (result) => {
      setReconResult(result);
      queryClient.invalidateQueries({ queryKey: ['invoices'] });
      queryClient.invalidateQueries({ queryKey: ['goods-receipts'] });
      queryClient.invalidateQueries({ queryKey: ['discrepancies'] });
      queryClient.invalidateQueries({ queryKey: ['dashboard-metrics'] });
    },
  });

  const handleRunReconciliation = () => {
    if (!selectedInvoiceId) return;
    runMutation.mutate({
      invoiceId: selectedInvoiceId,
      priceTolerancePercentage: (parseFloat(priceTolerancePct) || 0) / 100,
      quantityTolerancePercentage: (parseFloat(qtyTolerancePct) || 0) / 100,
      quantityToleranceAbsolute: parseFloat(qtyToleranceAbs) || 0,
    });
  };

  const handleSelectInvoice = (id: string) => {
    setSelectedInvoiceId(id);
    setSearchParams({ invoiceId: id });
    setReconResult(null);
  };

  return (
    <div className="space-y-6">
      {/* Page Header */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-slate-900 flex items-center gap-2">
            <GitCompare className="w-6 h-6 text-blue-600" />
            <span>Không Gian Đối Soát 3 Bên (3-Way Matching Workspace)</span>
          </h1>
          <p className="text-sm text-slate-500 mt-1">
            Tự động đối chiếu chéo Đơn mua hàng (PO), Phiếu thực nhập kho (GR) và Hóa đơn nhà cung cấp (INV) theo biên độ dung sai cho phép.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="primary"
            size="sm"
            onClick={handleRunReconciliation}
            disabled={!selectedInvoice || runMutation.isPending}
            className="flex items-center gap-1.5 shadow-sm bg-blue-600 hover:bg-blue-700 text-white font-medium"
          >
            <Play className="w-4 h-4 fill-white" />
            <span>{runMutation.isPending ? 'Đang đối soát...' : 'Thực Thi Đối Soát 3 Bên (Execute 3-Way Match)'}</span>
          </Button>
        </div>
      </div>

      {/* Target Invoice & Tolerance Configuration */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
        {/* Invoice Selector */}
        <Card className="lg:col-span-2">
          <CardHeader className="pb-3 border-b border-slate-100">
            <CardTitle className="text-sm font-semibold text-slate-900 flex items-center gap-2">
              <Receipt className="w-4 h-4 text-blue-600" />
              <span>Target Invoice for Reconciliation</span>
            </CardTitle>
          </CardHeader>
          <CardContent className="p-4 space-y-4">
            <div className="flex flex-col sm:flex-row gap-3">
              <div className="flex-1">
                <label className="text-xs font-medium text-slate-600 block mb-1">
                  Select Supplier Invoice
                </label>
                <Select
                  value={selectedInvoiceId}
                  onChange={(e) => handleSelectInvoice(e.target.value)}
                  className="w-full text-sm font-mono"
                >
                  {loadingInvoices ? (
                    <option>Loading invoices...</option>
                  ) : (
                    invoices?.map((inv) => (
                      <option key={inv.id} value={inv.id}>
                        {inv.invoiceNumber} — {inv.supplierName} ({inv.purchaseOrderNumber}) [
                        {inv.status}]
                      </option>
                    ))
                  )}
                </Select>
              </div>
            </div>

            {selectedInvoice && (
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 p-3 bg-slate-50 rounded-lg text-xs">
                <div>
                  <span className="text-slate-500 block">Status:</span>
                  <StatusBadge status={selectedInvoice.status} />
                </div>
                <div>
                  <span className="text-slate-500 block">Supplier:</span>
                  <span className="font-semibold text-slate-900 truncate block">
                    {selectedInvoice.supplierName}
                  </span>
                  <span className="font-mono text-slate-400">{selectedInvoice.supplierCode}</span>
                </div>
                <div>
                  <span className="text-slate-500 block">PO Reference:</span>
                  <span className="font-mono font-semibold text-blue-600">
                    {selectedInvoice.purchaseOrderNumber}
                  </span>
                </div>
                <div>
                  <span className="text-slate-500 block">Total Amount:</span>
                  <span className="font-mono font-bold text-slate-900">
                    {formatCurrency(selectedInvoice.totalAmount, selectedInvoice.currency)}
                  </span>
                </div>
              </div>
            )}
          </CardContent>
        </Card>

        {/* Tolerance Controls */}
        <Card>
          <CardHeader className="pb-3 border-b border-slate-100">
            <CardTitle className="text-sm font-semibold text-slate-900 flex items-center gap-2">
              <Sliders className="w-4 h-4 text-purple-600" />
              <span>Tolerance Policy Engine</span>
            </CardTitle>
          </CardHeader>
          <CardContent className="p-4 space-y-3">
            <div>
              <div className="flex justify-between items-center mb-1">
                <label className="text-xs font-medium text-slate-600">Price Tolerance (%)</label>
                <span className="text-xs font-mono font-bold text-purple-700">
                  {priceTolerancePct}%
                </span>
              </div>
              <Input
                type="number"
                step="0.1"
                min="0"
                max="50"
                value={priceTolerancePct}
                onChange={(e) => setPriceTolerancePct(e.target.value)}
                className="text-xs"
              />
              <p className="text-[11px] text-slate-400 mt-1">Acceptable unit price variance</p>
            </div>

            <div>
              <div className="flex justify-between items-center mb-1">
                <label className="text-xs font-medium text-slate-600">Qty Tolerance (%)</label>
                <span className="text-xs font-mono font-bold text-purple-700">
                  {qtyTolerancePct}%
                </span>
              </div>
              <Input
                type="number"
                step="0.1"
                min="0"
                max="50"
                value={qtyTolerancePct}
                onChange={(e) => setQtyTolerancePct(e.target.value)}
                className="text-xs"
              />
            </div>

            <div>
              <div className="flex justify-between items-center mb-1">
                <label className="text-xs font-medium text-slate-600">Qty Absolute Tolerance</label>
                <span className="text-xs font-mono font-bold text-purple-700">{qtyToleranceAbs}</span>
              </div>
              <Input
                type="number"
                step="1"
                min="0"
                value={qtyToleranceAbs}
                onChange={(e) => setQtyToleranceAbs(e.target.value)}
                className="text-xs"
              />
            </div>
          </CardContent>
        </Card>
      </div>

      {/* The 3 Pillars of 3-Way Matching */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        {/* Pillar 1: Purchase Order */}
        <Card className="border-t-4 border-t-blue-500 shadow-sm">
          <CardHeader className="py-3 px-4 bg-slate-50 border-b border-slate-100">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <FileText className="w-4 h-4 text-blue-600" />
                <span className="font-semibold text-xs uppercase tracking-wider text-slate-800">
                  1. Purchase Order
                </span>
              </div>
              {po && <StatusBadge status={po.status} />}
            </div>
            <div className="text-xs font-mono text-blue-700 mt-1">
              {po ? po.orderNumber : selectedInvoice?.purchaseOrderNumber || '—'}
            </div>
          </CardHeader>
          <CardContent className="p-3 text-xs">
            {loadingPo ? (
              <div className="p-4 text-center text-slate-400">Loading PO...</div>
            ) : !po ? (
              <div className="p-6 text-center text-slate-400">
                <AlertTriangle className="w-6 h-6 mx-auto mb-1 text-amber-500" />
                <p className="font-medium text-slate-700">No Purchase Order Found</p>
                <p className="text-[11px] text-slate-400">
                  Will trigger MissingPurchaseOrder discrepancy.
                </p>
              </div>
            ) : (
              <div className="space-y-2">
                <div className="text-[11px] text-slate-500 mb-1">
                  Supplier: <span className="font-semibold text-slate-700">{po.supplierName}</span>
                </div>
                <div className="border border-slate-100 rounded overflow-hidden">
                  <table className="w-full text-left">
                    <thead className="bg-slate-100 text-[10px] text-slate-600 uppercase">
                      <tr>
                        <th className="p-1.5">SKU</th>
                        <th className="p-1.5 text-right">Ordered</th>
                        <th className="p-1.5 text-right">Price</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 font-mono">
                      {(po.lines ?? []).map((l) => (
                        <tr key={l.id}>
                          <td className="p-1.5 font-semibold text-slate-900">{l.itemSku}</td>
                          <td className="p-1.5 text-right">{l.orderedQuantity}</td>
                          <td className="p-1.5 text-right">{formatCurrency(l.unitPrice, po.currency)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                <div className="pt-2 flex justify-between font-semibold text-slate-900">
                  <span>PO Total:</span>
                  <span className="font-mono">{formatCurrency(po.totalAmount, po.currency)}</span>
                </div>
              </div>
            )}
          </CardContent>
        </Card>

        {/* Pillar 2: Goods Receipts */}
        <Card className="border-t-4 border-t-amber-500 shadow-sm">
          <CardHeader className="py-3 px-4 bg-slate-50 border-b border-slate-100">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <Truck className="w-4 h-4 text-amber-600" />
                <span className="font-semibold text-xs uppercase tracking-wider text-slate-800">
                  2. Goods Receipts
                </span>
              </div>
              <span className="text-xs font-semibold px-2 py-0.5 rounded-full bg-amber-100 text-amber-800">
                {matchingGrs.length} Receipt(s)
              </span>
            </div>
            <div className="text-xs text-slate-500 mt-1">Physical deliveries against PO</div>
          </CardHeader>
          <CardContent className="p-3 text-xs">
            {loadingGrs ? (
              <div className="p-4 text-center text-slate-400">Loading receipts...</div>
            ) : matchingGrs.length === 0 ? (
              <div className="p-6 text-center text-slate-400">
                <AlertTriangle className="w-6 h-6 mx-auto mb-1 text-amber-500" />
                <p className="font-medium text-slate-700">No Goods Receipts Yet</p>
                <p className="text-[11px] text-slate-400">
                  Physical goods not yet received in warehouse.
                </p>
              </div>
            ) : (
              <div className="space-y-3">
                {matchingGrs.map((gr) => (
                  <div key={gr.id} className="border border-slate-200 rounded p-2 bg-slate-50/50">
                    <div className="flex justify-between items-center mb-1">
                      <span className="font-mono font-semibold text-slate-900">{gr.receiptNumber}</span>
                      <span className="text-[10px] text-slate-500">{formatDate(gr.receivedAt)}</span>
                    </div>
                    <table className="w-full text-left">
                      <thead className="text-[10px] text-slate-500 uppercase border-b border-slate-200">
                        <tr>
                          <th className="py-1">SKU</th>
                          <th className="py-1 text-right">Received</th>
                          <th className="py-1 text-right">Allocated</th>
                          <th className="py-1 text-right">Avail</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100 font-mono text-[11px]">
                        {(gr.lines ?? []).map((l) => (
                          <tr key={l.id}>
                            <td className="py-1 font-semibold text-slate-800">{l.itemSku}</td>
                            <td className="py-1 text-right">{l.receivedQuantity}</td>
                            <td className="py-1 text-right text-slate-500">{l.allocatedQuantity}</td>
                            <td className="py-1 text-right font-bold text-emerald-700">
                              {l.availableQuantity ?? (l.receivedQuantity - l.allocatedQuantity)}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ))}
              </div>
            )}
          </CardContent>
        </Card>

        {/* Pillar 3: Supplier Invoice */}
        <Card className="border-t-4 border-t-emerald-500 shadow-sm">
          <CardHeader className="py-3 px-4 bg-slate-50 border-b border-slate-100">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <Receipt className="w-4 h-4 text-emerald-600" />
                <span className="font-semibold text-xs uppercase tracking-wider text-slate-800">
                  3. Supplier Invoice
                </span>
              </div>
              {selectedInvoice && <StatusBadge status={selectedInvoice.status} />}
            </div>
            <div className="text-xs font-mono text-emerald-700 mt-1">
              {selectedInvoice ? selectedInvoice.invoiceNumber : '—'}
            </div>
          </CardHeader>
          <CardContent className="p-3 text-xs">
            {!selectedInvoice ? (
              <div className="p-6 text-center text-slate-400">Select an invoice above</div>
            ) : (
              <div className="space-y-2">
                <div className="border border-slate-100 rounded overflow-hidden">
                  <table className="w-full text-left">
                    <thead className="bg-slate-100 text-[10px] text-slate-600 uppercase">
                      <tr>
                        <th className="p-1.5">SKU</th>
                        <th className="p-1.5 text-right">Invoiced</th>
                        <th className="p-1.5 text-right">Price</th>
                        <th className="p-1.5 text-right">Total</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 font-mono">
                      {(selectedInvoice.lines ?? []).map((l) => (
                        <tr key={l.id}>
                          <td className="p-1.5 font-semibold text-slate-900">{l.itemSku}</td>
                          <td className="p-1.5 text-right">{l.invoicedQuantity}</td>
                          <td className="p-1.5 text-right">
                            {formatCurrency(l.unitPrice, selectedInvoice.currency)}
                          </td>
                          <td className="p-1.5 text-right font-semibold">
                            {formatCurrency(l.lineTotal, selectedInvoice.currency)}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                <div className="pt-2 flex justify-between font-semibold text-slate-900">
                  <span>Invoice Total:</span>
                  <span className="font-mono">
                    {formatCurrency(selectedInvoice.totalAmount, selectedInvoice.currency)}
                  </span>
                </div>
              </div>
            )}
          </CardContent>
        </Card>
      </div>

      {/* Reconciliation Execution Result Banner */}
      {reconResult && (
        <Card className={`border-2 shadow-md ${reconResult.status === 'Matched' ? 'border-emerald-500 bg-emerald-50/20' : 'border-rose-400 bg-rose-50/20'}`}>
          <CardHeader className="py-3 px-4 border-b border-slate-100">
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2">
              <div className="flex items-center gap-2">
                {reconResult.status === 'Matched' ? (
                  <CheckCircle2 className="w-6 h-6 text-emerald-600 shrink-0" />
                ) : (
                  <AlertTriangle className="w-6 h-6 text-rose-600 shrink-0" />
                )}
                <div>
                  <h3 className="font-bold text-slate-900 text-base">
                    {reconResult.status === 'Matched'
                      ? 'Đối Soát Thành Công (Matched & Ready for Payment)'
                      : 'Phát Hiện Sai Lệch (Discrepancy Detected)'}
                  </h3>
                  <p className="text-xs text-slate-500">
                    Hóa đơn <span className="font-mono font-semibold text-slate-700">{reconResult.invoiceNumber}</span> đối soát với Đơn mua <span className="font-mono font-semibold text-blue-600">{reconResult.purchaseOrderNumber}</span>
                  </p>
                </div>
              </div>
              <div className="flex items-center gap-3">
                <StatusBadge status={reconResult.status} />
                {(reconResult.discrepancies ?? []).length > 0 && (
                  <Button
                    variant="primary"
                    size="sm"
                    onClick={() => navigate('/discrepancies')}
                    className="flex items-center gap-1.5 text-xs bg-rose-600 hover:bg-rose-700 shadow-sm"
                  >
                    <span>Xử lý sai lệch (Investigate)</span>
                    <ArrowRight className="w-3.5 h-3.5" />
                  </Button>
                )}
              </div>
            </div>
          </CardHeader>
          <CardContent className="p-4 space-y-4">
            {/* Quantitative Summary */}
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 p-3 bg-white border border-slate-200 rounded-lg text-xs">
              <div>
                <span className="text-slate-500 block">Số lượng đặt (Ordered):</span>
                <span className="font-mono font-bold text-slate-900">
                  {reconResult.totalQuantityOrdered ?? 0} PCS
                </span>
              </div>
              <div>
                <span className="text-slate-500 block">Thực nhận tại kho (Received):</span>
                <span className="font-mono font-bold text-slate-900">
                  {reconResult.totalQuantityReceived ?? 0} PCS
                </span>
              </div>
              <div>
                <span className="text-slate-500 block">Số lượng ghi HĐ (Invoiced):</span>
                <span className="font-mono font-bold text-slate-900">
                  {reconResult.totalQuantityInvoiced ?? 0} PCS
                </span>
              </div>
              <div>
                <span className="text-slate-500 block">Chênh lệch tiền (Variance):</span>
                <span className={`font-mono font-bold ${(reconResult.totalAmountVariance ?? 0) === 0 ? 'text-emerald-600' : 'text-rose-600'}`}>
                  {formatCurrency(reconResult.totalAmountVariance ?? 0, selectedInvoice?.currency || 'VND')}
                </span>
              </div>
            </div>

            {/* Discrepancies List */}
            {(reconResult.discrepancies ?? []).length > 0 && (
              <div>
                <h4 className="text-xs uppercase font-semibold text-rose-700 mb-2 flex items-center gap-1.5">
                  <AlertTriangle className="w-4 h-4" />
                  <span>Danh Sách Sai Lệch Phát Hiện ({(reconResult.discrepancies ?? []).length})</span>
                </h4>
                <div className="border border-rose-200 rounded-lg overflow-hidden bg-white">
                  <table className="w-full text-left text-xs text-slate-700">
                    <thead className="bg-rose-50/75 uppercase text-[10px] font-semibold text-rose-800">
                      <tr>
                        <th className="p-2.5">Phân loại</th>
                        <th className="p-2.5">Mức độ</th>
                        <th className="p-2.5">Mã SKU</th>
                        <th className="p-2.5">Giá trị Kho (Kỳ vọng)</th>
                        <th className="p-2.5">Giá trị Hóa đơn (Thực tế)</th>
                        <th className="p-2.5">Chi tiết phân tích</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-rose-100">
                      {(reconResult.discrepancies ?? []).map((d) => (
                        <tr key={d.id} className="hover:bg-rose-50/30">
                          <td className="p-2.5 font-semibold text-slate-900">{d.type}</td>
                          <td className="p-2.5">
                            <DiscrepancySeverityBadge severity={d.severity} />
                          </td>
                          <td className="p-2.5 font-mono">{d.itemSku || '—'}</td>
                          <td className="p-2.5 font-mono text-emerald-700 font-medium">{d.expectedValue}</td>
                          <td className="p-2.5 font-mono font-semibold text-rose-700">
                            {d.actualValue}
                          </td>
                          <td className="p-2.5 text-slate-600">{d.details}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            {/* Allocations Table */}
            {(reconResult.allocations ?? []).length > 0 && (
              <div>
                <h4 className="text-xs uppercase font-semibold text-emerald-800 mb-2 flex items-center gap-1.5">
                  <ShieldCheck className="w-4 h-4 text-emerald-600" />
                  <span>Phân Bổ Hàng Nhập Kho Đã Khớp ({(reconResult.allocations ?? []).length} cam kết dòng hàng)</span>
                </h4>
                <div className="border border-slate-200 rounded-lg overflow-hidden bg-white">
                  <table className="w-full text-left text-xs text-slate-700">
                    <thead className="bg-slate-50 uppercase text-[10px] font-semibold text-slate-600">
                      <tr>
                        <th className="p-2.5">Mã Dòng Nhập Kho (GR)</th>
                        <th className="p-2.5">Mã Dòng Hóa Đơn (INV)</th>
                        <th className="p-2.5 text-right">SL Phân Bổ</th>
                        <th className="p-2.5 text-right">Đơn Giá Khớp</th>
                        <th className="p-2.5 text-right">Thời Điểm Khóa</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 font-mono text-[11px]">
                      {(reconResult.allocations ?? []).map((a) => (
                        <tr key={a.id}>
                          <td className="p-2.5 text-slate-600 font-semibold truncate max-w-[150px]">
                            {a.goodsReceiptLineId}
                          </td>
                          <td className="p-2.5 text-slate-500 truncate max-w-[150px]">
                            {a.supplierInvoiceLineId}
                          </td>
                          <td className="p-2.5 text-right font-bold text-emerald-700">
                            {a.allocatedQuantity} PCS
                          </td>
                          <td className="p-2.5 text-right">
                            {formatCurrency(a.unitPrice, selectedInvoice?.currency || 'VND')}
                          </td>
                          <td className="p-2.5 text-right text-slate-400">
                            {formatDate(a.allocatedAt)}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </CardContent>
        </Card>
      )}

      {runMutation.isError && (
        <div className="p-4 bg-rose-50 border border-rose-200 rounded-lg text-sm text-rose-700 flex items-center gap-2">
          <AlertTriangle className="w-5 h-5 flex-shrink-0 text-rose-600" />
          <span>Lỗi khi chạy đối soát: {(runMutation.error as Error).message}</span>
        </div>
      )}
    </div>
  );
}
