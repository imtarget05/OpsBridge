import { useState, type FormEvent } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { AlertCircle, Layers, ShieldCheck, UserCheck } from 'lucide-react';
import { useAuth } from './AuthContext';
import type { UserRole } from '../../types';
import { Button } from '../../components/ui/Button';
import { Input } from '../../components/ui/Input';

const demoPersonas: Array<{
  name: string;
  username: string;
  role: UserRole;
  icon: typeof ShieldCheck;
}> = [
  { name: 'Finance Reviewer', username: 'alice_finance', role: 'FinanceReviewer', icon: ShieldCheck },
  { name: 'Operator', username: 'bob_operator', role: 'Operator', icon: UserCheck },
  { name: 'Administrator', username: 'admin_system', role: 'Admin', icon: UserCheck },
  { name: 'Auditor', username: 'compliance_auditor', role: 'Auditor', icon: ShieldCheck },
];

export function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = location.state?.from?.pathname || '/dashboard';
  const [username, setUsername] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const signIn = async (name: string, role?: UserRole) => {
    setError(null);
    setLoading(true);
    try {
      await login(name, role);
      navigate(from, { replace: true });
    } catch {
      setError('Đăng nhập thất bại. Vui lòng thử lại.');
    } finally {
      setLoading(false);
    }
  };

  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const name = username.trim();
    if (!name) {
      setError('Vui lòng nhập tên đăng nhập.');
      return;
    }
    void signIn(name);
  };

  return (
    <div className="min-h-screen bg-slate-50 flex items-center justify-center px-4 py-12">
      <div className="w-full max-w-md space-y-6">
        <div className="text-center space-y-3">
          <div className="inline-flex rounded-2xl bg-indigo-600 p-3 text-white shadow-sm ring-4 ring-indigo-50">
            <Layers className="h-8 w-8" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight text-slate-900">OpsBridge Platform</h1>
          <p className="text-sm text-slate-500">Đối soát và điều phối vận hành</p>
        </div>

        <div className="space-y-5 rounded-2xl border border-slate-200 bg-white p-6 shadow-sm sm:p-8">
          <h2 className="text-base font-semibold text-slate-900">Đăng nhập bàn làm việc</h2>
          {error && (
            <div role="alert" className="flex items-start gap-2 rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-700">
              <AlertCircle className="h-4 w-4 shrink-0" />
              <span>{error}</span>
            </div>
          )}

          {import.meta.env.DEV ? (
            <>
              <p className="text-xs text-slate-500">Tài khoản thử nghiệm chỉ dùng trong môi trường development.</p>
              <form onSubmit={handleSubmit} className="space-y-3">
                <label htmlFor="login-username" className="block text-xs font-semibold text-slate-700">
                  Tên đăng nhập thử nghiệm
                </label>
                <Input
                  id="login-username"
                  type="text"
                  value={username}
                  onChange={(event) => setUsername(event.target.value)}
                  autoComplete="username"
                />
                <Button type="submit" variant="primary" className="w-full" isLoading={loading}>
                  Đăng nhập
                </Button>
              </form>
              <div className="grid grid-cols-2 gap-2 border-t border-slate-100 pt-4">
                {demoPersonas.map(({ name, username: demoName, role, icon: Icon }) => (
                  <button
                    key={role}
                    type="button"
                    disabled={loading}
                    onClick={() => void signIn(demoName, role)}
                    className="flex items-center gap-2 rounded-lg border border-slate-200 bg-slate-50 p-2 text-left text-xs text-slate-700 hover:bg-slate-100 disabled:opacity-50"
                  >
                    <Icon className="h-4 w-4 shrink-0 text-indigo-600" />
                    {name}
                  </button>
                ))}
              </div>
            </>
          ) : (
            <p role="status" className="rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
              Đăng nhập doanh nghiệp chưa sẵn sàng. Vui lòng liên hệ quản trị viên.
            </p>
          )}
        </div>
      </div>
    </div>
  );
}
