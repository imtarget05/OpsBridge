import { NavLink } from 'react-router-dom';
import {
  LayoutDashboard,
  FileText,
  PackageCheck,
  Receipt,
  GitCompare,
  AlertTriangle,
  Network,
  History,
} from 'lucide-react';
import { cn } from '../../lib/utils';

const navItems = [
  { to: '/dashboard', labelVi: 'Tổng quan vận hành', labelEn: 'Operations Overview', icon: LayoutDashboard },
  { to: '/purchase-orders', labelVi: 'Đơn mua hàng (PO)', labelEn: 'Purchase Orders', icon: FileText },
  { to: '/goods-receipts', labelVi: 'Phiếu nhập kho (GR)', labelEn: 'Goods Receipts', icon: PackageCheck },
  { to: '/invoices', labelVi: 'Hóa đơn NCC (INV)', labelEn: 'Supplier Invoices', icon: Receipt },
  { to: '/reconciliation', labelVi: 'Đối soát 3 bên', labelEn: '3-Way Matching Workspace', icon: GitCompare },
  { to: '/discrepancies', labelVi: 'Xử lý sai lệch', labelEn: 'Discrepancy Review', icon: AlertTriangle },
  { to: '/integrations', labelVi: 'Tích hợp hệ thống', labelEn: 'Integration Operations', icon: Network },
  { to: '/audit', labelVi: 'Nhật ký kiểm toán', labelEn: 'Audit Timeline', icon: History },
];

export function Sidebar() {
  return (
    <aside className="w-64 bg-slate-900 border-r border-slate-800 flex flex-col shrink-0 min-h-[calc(100vh-4rem)]">
      <div className="p-3 space-y-1">
        <div className="text-[11px] font-bold text-slate-500 uppercase tracking-wider px-3 mb-2">
          Nghiệp Vụ Cốt Lõi (Core Ops)
        </div>
        {navItems.map((item) => {
          const Icon = item.icon;
          return (
            <NavLink
              key={item.to}
              to={item.to}
              aria-label={item.labelEn}
              data-testid={`nav-${item.to.replace('/', '')}`}
              className={({ isActive }) =>
                cn(
                  'flex items-center gap-3 px-3 py-2.5 rounded-xl text-xs font-medium transition-colors',
                  isActive
                    ? 'bg-indigo-600 text-white shadow-md font-semibold'
                    : 'text-slate-400 hover:text-white hover:bg-slate-800/60'
                )
              }
            >
              <Icon className="w-4 h-4 shrink-0" />
              <div className="flex flex-col min-w-0">
                <span className="truncate">{item.labelVi}</span>
                <span className="text-[10px] opacity-70 truncate font-normal">{item.labelEn}</span>
              </div>
            </NavLink>
          );
        })}
      </div>

      <div className="mt-auto p-4 border-t border-slate-800">
        <div className="bg-slate-800/60 rounded-xl p-3 border border-slate-700/50">
          <div className="text-xs font-semibold text-slate-300">OpsBridge Platform v2.0</div>
          <div className="text-[11px] text-slate-400 mt-1">
            Động cơ đối soát 3 bên tự động
          </div>
          <div className="flex items-center gap-1.5 mt-2">
            <span className="w-2 h-2 rounded-full bg-emerald-400 animate-pulse"></span>
            <span className="text-[10px] text-emerald-400 font-medium">Hệ thống đang hoạt động</span>
          </div>
        </div>
      </div>
    </aside>
  );
}
