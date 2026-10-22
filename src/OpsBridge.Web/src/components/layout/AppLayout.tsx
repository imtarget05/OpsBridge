import { Outlet } from 'react-router-dom';
import { Navbar } from './Navbar';
import { Sidebar } from './Sidebar';
import { OpsCopilotWidget } from '../ai/OpsCopilotWidget';
import { ShieldAlert } from 'lucide-react';

export function AppLayout() {
  return (
    <div className="min-h-screen bg-slate-50 flex flex-col">
      {/* Sandbox Notice Banner */}
      <div className="bg-amber-500/10 border-b border-amber-500/20 px-4 py-1.5 text-xs text-amber-800 flex items-center justify-between">
        <div className="flex items-center gap-2 max-w-7xl mx-auto w-full">
          <ShieldAlert className="w-4 h-4 text-amber-600 shrink-0" />
          <span>
            <strong>Môi trường Sandbox / Dữ liệu mẫu:</strong> Hệ thống đang chạy mô phỏng quy trình Đối soát 3 Bên &amp; xử lý sai lệch. Mọi thao tác không ghi dữ liệu tài chính thực tế.
          </span>
        </div>
      </div>
      <Navbar />
      <div className="flex flex-1">
        <Sidebar />
        <main className="flex-1 p-8 overflow-y-auto max-w-7xl mx-auto w-full">
          <Outlet />
        </main>
      </div>
      <OpsCopilotWidget />
    </div>
  );
}
