import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../lib/api-client';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/Card';
import { Button } from '../../components/ui/Button';
import { Modal } from '../../components/ui/Modal';
import { formatDate } from '../../lib/formatters';
import {
  Layers,
  CheckCircle2,
  AlertTriangle,
  Skull,
  ArrowRight,
  TrendingUp,
  History,
  RefreshCw,
  FileText,
  PackageCheck,
  Receipt,
  GitCompare,
  HelpCircle,
  Sparkles,
} from 'lucide-react';
import { Link } from 'react-router-dom';

export function DashboardPage() {
  const [showTour, setShowTour] = useState(false);

  const {
    data: metrics,
    isLoading: loadingMetrics,
    refetch: refetchMetrics,
  } = useQuery({
    queryKey: ['dashboard-metrics'],
    queryFn: () => api.getDashboardMetrics(),
    refetchInterval: 10000,
  });

  const { data: auditEvents, isLoading: loadingAudit } = useQuery({
    queryKey: ['recent-audit'],
    queryFn: () => api.getAuditTrail(),
  });

  return (
    <div className="space-y-6">
      {/* Page Header */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-slate-900 flex items-center gap-2">
            <span>Tổng Quan Vận Hành (Operations Overview)</span>
          </h1>
          <p className="text-sm text-slate-500 mt-1">
            Trung tâm đối soát 3 bên tự động giữa Đơn mua hàng (PO), Thực nhập kho (GR) và Hóa đơn tài chính (INV).
          </p>
        </div>
        <div className="flex items-center gap-2.5">
          <Button
            variant="outline"
            size="sm"
            onClick={() => setShowTour(true)}
            className="flex items-center gap-1.5 text-indigo-600 border-indigo-200 hover:bg-indigo-50"
          >
            <HelpCircle className="w-4 h-4" />
            <span>Hướng dẫn quy trình (Tour)</span>
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => refetchMetrics()}
            className="flex items-center gap-1.5"
          >
            <RefreshCw className="w-4 h-4 text-slate-500" />
            <span>Làm mới</span>
          </Button>
          <Link to="/reconciliation">
            <Button variant="primary" size="sm" className="flex items-center gap-1.5 bg-blue-600 hover:bg-blue-700">
              <GitCompare className="w-4 h-4" />
              <span>Không gian Đối soát</span>
              <ArrowRight className="w-4 h-4" />
            </Button>
          </Link>
        </div>
      </div>

      {/* Interactive 4-Step 3-Way Matching Workflow Guide */}
      <Card className="border-indigo-100 bg-gradient-to-r from-indigo-50/70 via-white to-blue-50/50 shadow-sm overflow-hidden">
        <div className="p-4 border-b border-indigo-100/60 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <span className="p-1 rounded-md bg-indigo-600 text-white">
              <Sparkles className="w-4 h-4" />
            </span>
            <span className="font-bold text-sm text-slate-900">
              Quy Trình Đối Soát 3 Bên Trực Quan (Interactive 3-Way Matching Workflow)
            </span>
          </div>
          <span className="text-xs text-indigo-700 font-medium">Bấm vào từng bước để thao tác</span>
        </div>
        <CardContent className="p-4">
          <div className="grid grid-cols-1 md:grid-cols-4 gap-3 relative">
            {/* Step 1 */}
            <Link
              to="/purchase-orders"
              className="p-3.5 rounded-xl border border-blue-200 bg-white hover:border-blue-400 hover:shadow-md transition-all group"
            >
              <div className="flex items-center justify-between mb-2">
                <span className="text-[10px] font-bold uppercase tracking-wider text-blue-600 bg-blue-50 px-2 py-0.5 rounded-full">
                  Bước 1
                </span>
                <FileText className="w-4 h-4 text-blue-600 group-hover:scale-110 transition-transform" />
              </div>
              <h4 className="font-bold text-slate-900 text-xs sm:text-sm">Đơn Mua Hàng (PO)</h4>
              <p className="text-[11px] text-slate-500 mt-1 leading-snug">
                Tạo và quản lý cam kết đặt nguyên vật liệu với Nhà cung cấp.
              </p>
              <div className="mt-3 text-[11px] text-blue-600 font-semibold flex items-center gap-1">
                <span>Xem danh sách PO</span>
                <ArrowRight className="w-3 h-3 group-hover:translate-x-1 transition-transform" />
              </div>
            </Link>

            {/* Step 2 */}
            <Link
              to="/goods-receipts"
              className="p-3.5 rounded-xl border border-amber-200 bg-white hover:border-amber-400 hover:shadow-md transition-all group"
            >
              <div className="flex items-center justify-between mb-2">
                <span className="text-[10px] font-bold uppercase tracking-wider text-amber-600 bg-amber-50 px-2 py-0.5 rounded-full">
                  Bước 2
                </span>
                <PackageCheck className="w-4 h-4 text-amber-600 group-hover:scale-110 transition-transform" />
              </div>
              <h4 className="font-bold text-slate-900 text-xs sm:text-sm">Phiếu Nhập Kho (GR)</h4>
              <p className="text-[11px] text-slate-500 mt-1 leading-snug">
                Thủ kho kiểm đếm thực nhận và ghi nhận số lượng thực tế tại kho.
              </p>
              <div className="mt-3 text-[11px] text-amber-700 font-semibold flex items-center gap-1">
                <span>Nhập kho thực tế</span>
                <ArrowRight className="w-3 h-3 group-hover:translate-x-1 transition-transform" />
              </div>
            </Link>

            {/* Step 3 */}
            <Link
              to="/invoices"
              className="p-3.5 rounded-xl border border-emerald-200 bg-white hover:border-emerald-400 hover:shadow-md transition-all group"
            >
              <div className="flex items-center justify-between mb-2">
                <span className="text-[10px] font-bold uppercase tracking-wider text-emerald-600 bg-emerald-50 px-2 py-0.5 rounded-full">
                  Bước 3
                </span>
                <Receipt className="w-4 h-4 text-emerald-600 group-hover:scale-110 transition-transform" />
              </div>
              <h4 className="font-bold text-slate-900 text-xs sm:text-sm">Hóa Đơn NCC (INV)</h4>
              <p className="text-[11px] text-slate-500 mt-1 leading-snug">
                Nhập hóa đơn tài chính hoặc upload file CSV đề nghị thanh toán.
              </p>
              <div className="mt-3 text-[11px] text-emerald-700 font-semibold flex items-center gap-1">
                <span>Kiểm tra hóa đơn</span>
                <ArrowRight className="w-3 h-3 group-hover:translate-x-1 transition-transform" />
              </div>
            </Link>

            {/* Step 4 */}
            <Link
              to="/reconciliation"
              className="p-3.5 rounded-xl border border-indigo-300 bg-indigo-600 text-white hover:bg-indigo-700 hover:shadow-md transition-all group"
            >
              <div className="flex items-center justify-between mb-2">
                <span className="text-[10px] font-bold uppercase tracking-wider text-white bg-indigo-500 px-2 py-0.5 rounded-full">
                  Bước 4
                </span>
                <GitCompare className="w-4 h-4 text-white group-hover:scale-110 transition-transform" />
              </div>
              <h4 className="font-bold text-white text-xs sm:text-sm">Chạy Đối Soát 3 Bên</h4>
              <p className="text-[11px] text-indigo-100 mt-1 leading-snug">
                Tự động kiểm tra khớp Số lượng & Đơn giá, khóa dòng thanh toán.
              </p>
              <div className="mt-3 text-[11px] text-white font-semibold flex items-center gap-1">
                <span>Thực thi ngay</span>
                <ArrowRight className="w-3 h-3 group-hover:translate-x-1 transition-transform" />
              </div>
            </Link>
          </div>
        </CardContent>
      </Card>

      {/* KPI Metric Cards */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Total Invoices */}
        <Card className="border-l-4 border-l-indigo-500 hover:shadow-sm transition-shadow">
          <CardContent className="p-5">
            <div className="flex items-center justify-between">
              <div>
                <span className="text-xs font-bold text-slate-700 uppercase tracking-wider block">
                  Tổng Hóa Đơn
                </span>
                <span className="text-[11px] text-slate-400 font-normal">Total Invoices</span>
              </div>
              <div className="p-2 rounded-lg bg-indigo-50 text-indigo-600">
                <Layers className="w-4 h-4" />
              </div>
            </div>
            <div className="mt-3">
              <div className="text-2xl font-bold text-slate-900" data-testid="metric-total-invoices">
                {loadingMetrics ? '...' : metrics?.totalInvoices ?? 0}
              </div>
              <div className="text-xs text-slate-500 mt-1 flex items-center gap-1">
                <span>{metrics?.totalBatches ?? 0} đợt đối soát đã thực hiện</span>
              </div>
            </div>
          </CardContent>
        </Card>

        {/* Matched Invoices */}
        <Card className="border-l-4 border-l-emerald-500 hover:shadow-sm transition-shadow">
          <CardContent className="p-5">
            <div className="flex items-center justify-between">
              <div>
                <span className="text-xs font-bold text-emerald-800 uppercase tracking-wider block">
                  Đã Khớp Chuẩn
                </span>
                <span className="text-[11px] text-slate-400 font-normal">Matched & Approved</span>
              </div>
              <div className="p-2 rounded-lg bg-emerald-50 text-emerald-600">
                <CheckCircle2 className="w-4 h-4" />
              </div>
            </div>
            <div className="mt-3">
              <div className="text-2xl font-bold text-emerald-700">
                {loadingMetrics ? '...' : metrics?.matchedInvoices ?? 0}
              </div>
              <div className="text-xs text-slate-500 mt-1 flex items-center gap-1">
                <TrendingUp className="w-3.5 h-3.5 text-emerald-600" />
                <span>Không lệch hoặc trong biên độ dung sai</span>
              </div>
            </div>
          </CardContent>
        </Card>

        {/* Open Discrepancies */}
        <Card className="border-l-4 border-l-amber-500 hover:shadow-sm transition-shadow">
          <CardContent className="p-5">
            <div className="flex items-center justify-between">
              <div>
                <span className="text-xs font-bold text-amber-800 uppercase tracking-wider block">
                  Sai Lệch Cần Xử Lý
                </span>
                <span className="text-[11px] text-slate-400 font-normal">Open Discrepancies</span>
              </div>
              <div className="p-2 rounded-lg bg-amber-50 text-amber-600">
                <AlertTriangle className="w-4 h-4" />
              </div>
            </div>
            <div className="mt-3">
              <div className="text-2xl font-bold text-amber-700">
                {loadingMetrics ? '...' : metrics?.openDiscrepancies ?? 0}
              </div>
              <div className="text-xs text-slate-500 mt-1 flex items-center gap-1">
                <span>{metrics?.discrepantInvoices ?? 0} hóa đơn cần kế toán duyệt/từ chối</span>
              </div>
            </div>
          </CardContent>
        </Card>

        {/* Integration DLQ */}
        <Card className="border-l-4 border-l-rose-500 hover:shadow-sm transition-shadow">
          <CardContent className="p-5">
            <div className="flex items-center justify-between">
              <div>
                <span className="text-xs font-bold text-rose-800 uppercase tracking-wider block">
                  Lỗi Hàng Đợi (DLQ)
                </span>
                <span className="text-[11px] text-slate-400 font-normal">Dead-Letter Queue</span>
              </div>
              <div className="p-2 rounded-lg bg-rose-50 text-rose-600">
                <Skull className="w-4 h-4" />
              </div>
            </div>
            <div className="mt-3">
              <div className="text-2xl font-bold text-rose-700">
                {loadingMetrics ? '...' : metrics?.deadLetterCount ?? 0}
              </div>
              <div className="text-xs text-slate-500 mt-1 flex items-center gap-1">
                <span>{metrics?.activeConnectors ?? 1} cổng tích hợp webhook trực tiếp</span>
              </div>
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Operational Highlights & Activity Feed */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left Column: Quick Action Cards */}
        <div className="space-y-4 lg:col-span-1">
          <Card className="bg-gradient-to-br from-indigo-900 to-slate-900 text-white border-0 shadow-lg">
            <CardContent className="p-6 space-y-4">
              <div className="w-10 h-10 rounded-xl bg-white/10 flex items-center justify-center text-white">
                <CheckCircle2 className="w-6 h-6 text-emerald-400" />
              </div>
              <div>
                <h3 className="text-base font-bold">Động Cơ Khớp Lệnh 3 Bên (3-Way Matching Engine)</h3>
                <p className="text-xs text-slate-300 mt-1 leading-relaxed">
                  So khớp từng dòng nguyên vật liệu giữa Đơn mua, Phiếu nhập kho và Hóa đơn; tự động khóa số lượng đã nhận để ngăn ngừa thanh toán trùng lặp.
                </p>
              </div>
              <div className="pt-2">
                <Link to="/reconciliation">
                  <Button variant="primary" size="sm" className="bg-indigo-500 hover:bg-indigo-600 text-white w-full">
                    Mở Không Gian Đối Soát
                  </Button>
                </Link>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader className="py-3 px-4">
              <CardTitle className="text-xs uppercase tracking-wider text-slate-500">
                Trạng Thái Kết Nối Hệ Thống (Topology Health)
              </CardTitle>
            </CardHeader>
            <CardContent className="p-4 space-y-3">
              <div className="flex items-center justify-between text-xs">
                <span className="font-medium text-slate-700 flex items-center gap-2">
                  <span className="w-2 h-2 rounded-full bg-emerald-500"></span>
                  FactoryFlow REST & Webhooks
                </span>
                <span className="text-emerald-700 font-semibold">Active</span>
              </div>
              <div className="flex items-center justify-between text-xs">
                <span className="font-medium text-slate-700 flex items-center gap-2">
                  <span className="w-2 h-2 rounded-full bg-emerald-500"></span>
                  PostgreSQL 15 Persistence
                </span>
                <span className="text-emerald-700 font-semibold">Healthy</span>
              </div>
              <div className="flex items-center justify-between text-xs">
                <span className="font-medium text-slate-700 flex items-center gap-2">
                  <span className="w-2 h-2 rounded-full bg-emerald-500"></span>
                  RabbitMQ 3 Queue Bus
                </span>
                <span className="text-emerald-700 font-semibold">Connected</span>
              </div>
              <div className="flex items-center justify-between text-xs">
                <span className="font-medium text-slate-700 flex items-center gap-2">
                  <span className="w-2 h-2 rounded-full bg-blue-500"></span>
                  Idempotent Consumer Host
                </span>
                <span className="text-blue-700 font-semibold">Listening</span>
              </div>
            </CardContent>
          </Card>
        </div>

        {/* Right Column: Live Audit Activity */}
        <div className="lg:col-span-2">
          <Card className="h-full">
            <CardHeader className="py-4 px-5">
              <div className="flex items-center justify-between w-full">
                <div className="flex items-center gap-2">
                  <History className="w-4 h-4 text-indigo-600" />
                  <CardTitle className="text-sm font-semibold">
                    Nhật Ký Thao Tác Trực Tiếp (Live Audit Trail)
                  </CardTitle>
                </div>
                <Link to="/audit" className="text-xs font-medium text-indigo-600 hover:text-indigo-800">
                  Xem tất cả &rarr;
                </Link>
              </div>
            </CardHeader>
            <CardContent className="p-0">
              {loadingAudit ? (
                <div className="p-8 text-center text-xs text-slate-500">Đang tải nhật ký thao tác...</div>
              ) : !auditEvents || auditEvents.length === 0 ? (
                <div className="p-8 text-center text-xs text-slate-400">
                  Chưa có sự kiện nào được ghi nhận.
                </div>
              ) : (
                <div className="divide-y divide-slate-100 max-h-[380px] overflow-y-auto">
                  {auditEvents.slice(0, 7).map((ev) => (
                    <div key={ev.id} className="p-4 hover:bg-slate-50/80 transition-colors flex items-start gap-3">
                      <div className="w-2 h-2 rounded-full bg-indigo-500 mt-2 shrink-0"></div>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between gap-2">
                          <span className="text-xs font-semibold text-slate-900 truncate">
                            {ev.action}
                          </span>
                          <span className="text-[10px] text-slate-400 shrink-0">
                            {formatDate(ev.timestamp)}
                          </span>
                        </div>
                        <p className="text-xs text-slate-600 mt-0.5 truncate">
                          {ev.details}
                        </p>
                        <div className="flex items-center gap-2 mt-1">
                          <span className="text-[10px] bg-slate-100 text-slate-600 px-1.5 py-0.5 rounded font-mono">
                            {ev.entityType}
                          </span>
                          <span className="text-[10px] text-slate-500">
                            bởi <span className="font-medium text-slate-700">{ev.userId}</span>
                          </span>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </CardContent>
          </Card>
        </div>
      </div>

      {/* Guide / Tour Modal */}
      <Modal
        isOpen={showTour}
        onClose={() => setShowTour(false)}
        title="Hướng Dẫn Nhanh: Đối Soát 3 Bên (3-Way Matching) Là Gì?"
        className="max-w-2xl"
      >
        <div className="space-y-4 text-xs text-slate-700">
          <p className="leading-relaxed">
            Trong quản lý chuỗi cung ứng và kế toán, <strong>Đối soát 3 bên (Three-Way Matching)</strong> là quy trình đối chiếu chéo 3 chứng từ trước khi xuất tiền thanh toán cho Nhà cung cấp:
          </p>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
            <div className="p-3 bg-blue-50 border border-blue-200 rounded-lg">
              <span className="font-bold text-blue-800 block text-sm mb-1">1. Đơn Mua (PO)</span>
              <span>Doanh nghiệp đặt mua bao nhiêu chiếc, với mức giá thỏa thuận là bao nhiêu?</span>
            </div>
            <div className="p-3 bg-amber-50 border border-amber-200 rounded-lg">
              <span className="font-bold text-amber-800 block text-sm mb-1">2. Thực Nhập (GR)</span>
              <span>Thủ kho thực tế nhận được bao nhiêu hàng còn nguyên vẹn tại cửa kho?</span>
            </div>
            <div className="p-3 bg-emerald-50 border border-emerald-200 rounded-lg">
              <span className="font-bold text-emerald-800 block text-sm mb-1">3. Hóa Đơn (INV)</span>
              <span>Nhà cung cấp gửi hóa đơn đòi thanh toán bao nhiêu tiền và bao nhiêu sản phẩm?</span>
            </div>
          </div>

          <div className="p-3 bg-slate-50 border border-slate-200 rounded-lg space-y-2">
            <h5 className="font-bold text-slate-900">Khi nào hệ thống báo Lệch (Discrepancy)?</h5>
            <ul className="list-disc pl-4 space-y-1 text-slate-600">
              <li><strong>Quantity Mismatch:</strong> Hóa đơn đòi 100 bao nhưng kho mới nhận 80 bao.</li>
              <li><strong>Price Mismatch:</strong> Giá trên hóa đơn cao hơn đơn giá đã cam kết trong PO.</li>
              <li><strong>Tolerance Engine:</strong> Cho phép bỏ qua độ lệch nhỏ (ví dụ lệch &lt; 1% đơn giá).</li>
            </ul>
          </div>

          <div className="pt-2 flex justify-end">
            <Button variant="primary" size="sm" onClick={() => setShowTour(false)}>
              Đã hiểu, quay lại màn hình
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
