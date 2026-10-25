import { useAuth } from '../../features/auth/AuthContext';
import { Button } from '../ui/Button';
import { Layers, LogOut, User as UserIcon, RefreshCw } from 'lucide-react';
import type { UserRole } from '../../types';

export function Navbar() {
  const { user, role, logout, switchPersona } = useAuth();

  const personas: Array<{ role: UserRole; label: string }> = [
    { role: 'FinanceReviewer', label: 'Finance Reviewer' },
    { role: 'Operator', label: 'Operator' },
    { role: 'Admin', label: 'Administrator' },
    { role: 'Auditor', label: 'Auditor' },
  ];

  return (
    <header className="h-16 bg-white border-b border-slate-200 px-6 flex items-center justify-between sticky top-0 z-30">
      <div className="flex items-center gap-3">
        <div className="flex p-2 rounded-xl bg-indigo-600 text-white shadow-xs">
          <Layers className="w-5 h-5" />
        </div>
        <div>
          <span className="font-bold text-slate-900 tracking-tight text-base">OpsBridge</span>
          <span className="ml-2 text-xs font-medium text-slate-500 hidden sm:inline">
            Reconciliation & Operations
          </span>
        </div>
      </div>

      <div className="flex items-center gap-4">
        {import.meta.env.DEV && <div className="flex items-center gap-2 bg-slate-100 p-1 rounded-lg border border-slate-200">
          <span className="text-xs font-semibold text-slate-500 pl-2 hidden md:inline flex items-center gap-1">
            <RefreshCw className="w-3 h-3" /> Role:
          </span>
          <select
            value={role || 'Operator'}
            onChange={(e) => switchPersona(e.target.value as UserRole)}
            className="text-xs font-medium bg-white border border-slate-300 rounded-md px-2 py-1 text-slate-800 focus:outline-none focus:ring-1 focus:ring-indigo-500 cursor-pointer"
          >
            {personas.map((p) => (
              <option key={p.role} value={p.role}>
                {p.label}
              </option>
            ))}
          </select>
        </div>}

        {/* User Badge */}
        <div className="flex items-center gap-3 pl-2 border-l border-slate-200">
          <div className="flex items-center gap-2">
            <div className="w-8 h-8 rounded-full bg-indigo-100 text-indigo-700 font-bold flex items-center justify-center text-xs">
              <UserIcon className="w-4 h-4" />
            </div>
            <div className="hidden lg:block text-left">
              <div className="text-xs font-semibold text-slate-900">{user?.username}</div>
              <div className="text-[10px] text-slate-500 capitalize">{role}</div>
            </div>
          </div>

          <Button
            variant="ghost"
            size="sm"
            onClick={logout}
            className="text-slate-500 hover:text-rose-600 hover:bg-rose-50"
            title="Log Out"
          >
            <LogOut className="w-4 h-4" />
          </Button>
        </div>
      </div>
    </header>
  );
}
