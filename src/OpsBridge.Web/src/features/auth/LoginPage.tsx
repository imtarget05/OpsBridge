import React, { useState, useEffect, useCallback } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { useAuth } from './AuthContext';
import type { UserRole } from '../../types';
import { Button } from '../../components/ui/Button';
import { Input } from '../../components/ui/Input';
import { Layers, ShieldCheck, UserCheck, AlertCircle } from 'lucide-react';

const GOOGLE_CLIENT_ID = '791168472055-sv1l6q6o0rmsbojismes144qi2g1fn0e.apps.googleusercontent.com';

export function LoginPage() {
  const { login, loginWithGoogle } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = location.state?.from?.pathname || '/dashboard';

  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const handleGoogleSuccess = useCallback(async (credential: string) => {
    setError(null);
    setLoading(true);
    try {
      await loginWithGoogle(credential);
      navigate(from, { replace: true });
    } catch (err: any) {
      setError(err.message || 'Đăng nhập Google thất bại.');
    } finally {
      setLoading(false);
    }
  }, [loginWithGoogle, navigate, from]);

  useEffect(() => {
    // Load Google Identity Services script
    const scriptId = 'google-gsi-client';
    if (!document.getElementById(scriptId)) {
      const script = document.createElement('script');
      script.id = scriptId;
      script.src = 'https://accounts.google.com/gsi/client';
      script.async = true;
      script.defer = true;
      script.onload = () => {
        try {
          if ((window as any).google?.accounts?.id) {
            (window as any).google.accounts.id.initialize({
              client_id: GOOGLE_CLIENT_ID,
              callback: (response: any) => {
                if (response?.credential) {
                  handleGoogleSuccess(response.credential);
                }
              },
            });
            const btnEl = document.getElementById('google-btn-container');
            if (btnEl) {
              (window as any).google.accounts.id.renderButton(btnEl, {
                theme: 'outline',
                size: 'large',
                width: 384,
                text: 'signin_with',
                shape: 'rectangular',
              });
            }
          }
        } catch (e) {
          console.warn('Google GSI init notice:', e);
        }
      };
      document.body.appendChild(script);
    }
  }, [handleGoogleSuccess]);

  const handleGoogleQuickDemo = async () => {
    setError(null);
    setLoading(true);
    try {
      // Demo Google credential payload
      const mockPayload = {
        name: 'Google User',
        email: 'operator@gmail.com',
        sub: 'google-oauth-791168472055',
      };
      const mockJwt = 'mock.' + btoa(JSON.stringify(mockPayload)) + '.sig';
      await loginWithGoogle(mockJwt);
      navigate(from, { replace: true });
    } catch (err: any) {
      setError(err.message || 'Không thể xác thực với Google.');
    } finally {
      setLoading(false);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!username.trim()) {
      setError('Vui lòng nhập Tên đăng nhập hoặc Email.');
      return;
    }
    setError(null);
    setLoading(true);

    try {
      await login(username);
      navigate(from, { replace: true });
    } catch (err: any) {
      setError(err.message || 'Xác thực thất bại. Vui lòng kiểm tra lại tài khoản.');
    } finally {
      setLoading(false);
    }
  };

  const handleQuickPersona = async (personaUsername: string, personaRole: UserRole) => {
    setError(null);
    setLoading(true);
    try {
      await login(personaUsername, personaRole);
      navigate(from, { replace: true });
    } catch (err: any) {
      setError(err.message || 'Không thể chuyển đổi vai trò.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="min-h-screen bg-slate-50 flex flex-col justify-center items-center py-12 px-4 sm:px-6 lg:px-8">
      <div className="w-full max-w-md space-y-6">
        {/* Brand Header */}
        <div className="text-center space-y-2">
          <div className="inline-flex p-3 rounded-2xl bg-indigo-600 text-white shadow-sm ring-4 ring-indigo-50">
            <Layers className="w-8 h-8" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight text-slate-900">OpsBridge Platform</h1>
          <p className="text-sm text-slate-500">
            Hệ thống Đối soát 3 Bên (PO - Goods Receipt - Invoice) & Điều Phối Vận Hành
          </p>
        </div>

        {/* Login Card */}
        <div className="bg-white border border-slate-200/90 rounded-2xl shadow-sm p-6 sm:p-8 space-y-5">
          <div className="border-b border-slate-100 pb-4">
            <h2 className="text-base font-semibold text-slate-900">
              Đăng nhập bàn làm việc vận hành
            </h2>
            <p className="text-xs text-slate-500 mt-1">
              Truy cập tài khoản nội bộ hoặc tiếp tục bằng cổng xác thực liên kết
            </p>
          </div>

          {error && (
            <div className="p-3 rounded-lg bg-rose-50 border border-rose-200 text-rose-700 text-xs flex items-start gap-2.5">
              <AlertCircle className="w-4 h-4 shrink-0 text-rose-500 mt-0.5" />
              <span className="leading-relaxed">{error}</span>
            </div>
          )}

          {/* Google OAuth Sign-In Section */}
          <div className="space-y-2">
            <div id="google-btn-container" className="flex justify-center min-h-[40px] empty:hidden"></div>

            {/* Standard Enterprise SSO button */}
            <button
              type="button"
              onClick={handleGoogleQuickDemo}
              disabled={loading}
              className="w-full flex items-center justify-center gap-3 py-2.5 px-4 rounded-lg border border-slate-300 bg-white hover:bg-slate-50 text-slate-700 text-xs font-medium transition-colors shadow-xs cursor-pointer focus:outline-none focus:ring-2 focus:ring-offset-1 focus:ring-indigo-500 disabled:opacity-50"
            >
              <svg className="w-4 h-4 shrink-0" viewBox="0 0 24 24">
                <path
                  fill="#4285F4"
                  d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"
                />
                <path
                  fill="#34A853"
                  d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"
                />
                <path
                  fill="#FBBC05"
                  d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.06H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.94l2.85-2.22.81-.63z"
                />
                <path
                  fill="#EA4335"
                  d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.06l3.66 2.84c.87-2.6 3.3-4.52 6.16-4.52z"
                />
              </svg>
              <span>Đăng nhập với Google Workspace (SSO)</span>
            </button>
          </div>

          <div className="relative my-4">
            <div className="absolute inset-0 flex items-center">
              <div className="w-full border-t border-slate-200"></div>
            </div>
            <div className="relative flex justify-center text-[11px] uppercase">
              <span className="bg-white px-3 text-slate-400 font-medium">Hoặc đăng nhập với tài khoản nội bộ</span>
            </div>
          </div>

          <form onSubmit={handleSubmit} className="space-y-4">
            <div>
              <label htmlFor="login-username" className="block text-xs font-semibold text-slate-700 mb-1">
                Tên đăng nhập / Email
              </label>
              <Input
                id="login-username"
                type="text"
                placeholder="ví dụ: alice_finance"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                className="bg-white border-slate-300 text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:ring-indigo-500"
              />
            </div>

            <div>
              <div className="flex items-center justify-between mb-1">
                <label htmlFor="login-password" className="block text-xs font-semibold text-slate-700">
                  Mật khẩu
                </label>
                <span className="text-[11px] text-indigo-600 hover:text-indigo-700 cursor-pointer font-medium">
                  Quên mật khẩu?
                </span>
              </div>
              <Input
                id="login-password"
                type="password"
                placeholder="••••••••"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="bg-white border-slate-300 text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:ring-indigo-500"
              />
            </div>

            <Button
              type="submit"
              variant="primary"
              className="w-full bg-indigo-600 hover:bg-indigo-700 text-white font-medium py-2.5 rounded-lg shadow-xs"
              isLoading={loading}
            >
              Đăng Nhập Hệ Thống
            </Button>
          </form>

          {/* Quick Persona Section for Reviewers and Testers */}
          <div className="mt-6 pt-5 border-t border-slate-100">
            <div className="flex items-center justify-between mb-3">
              <span className="text-[11px] font-semibold text-slate-500 uppercase tracking-wider">
                Chọn vai trò nhanh (Demo Sandbox)
              </span>
              <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-indigo-50 text-indigo-700 border border-indigo-100">
                1-Click
              </span>
            </div>

            <div className="grid grid-cols-2 gap-2">
              <button
                type="button"
                aria-label="Finance Reviewer"
                onClick={() => handleQuickPersona('alice_finance', 'FinanceReviewer')}
                className="flex items-center gap-2.5 p-2.5 rounded-lg border border-slate-200 bg-slate-50/70 hover:bg-slate-100/80 hover:border-slate-300 transition-all text-left cursor-pointer group"
              >
                <div className="w-7 h-7 rounded-md bg-emerald-100 text-emerald-700 flex items-center justify-center shrink-0 group-hover:scale-105 transition-transform">
                  <ShieldCheck className="w-4 h-4" />
                </div>
                <div className="min-w-0">
                  <div className="text-xs font-semibold text-slate-800 truncate">Kế toán Đối soát</div>
                  <div className="text-[10px] text-slate-500 truncate">Finance Reviewer</div>
                </div>
              </button>

              <button
                type="button"
                aria-label="Operator"
                onClick={() => handleQuickPersona('bob_operator', 'Operator')}
                className="flex items-center gap-2.5 p-2.5 rounded-lg border border-slate-200 bg-slate-50/70 hover:bg-slate-100/80 hover:border-slate-300 transition-all text-left cursor-pointer group"
              >
                <div className="w-7 h-7 rounded-md bg-blue-100 text-blue-700 flex items-center justify-center shrink-0 group-hover:scale-105 transition-transform">
                  <UserCheck className="w-4 h-4" />
                </div>
                <div className="min-w-0">
                  <div className="text-xs font-semibold text-slate-800 truncate">Thủ kho Vận hành</div>
                  <div className="text-[10px] text-slate-500 truncate">Operator</div>
                </div>
              </button>

              <button
                type="button"
                aria-label="Administrator"
                onClick={() => handleQuickPersona('admin_system', 'Admin')}
                className="flex items-center gap-2.5 p-2.5 rounded-lg border border-slate-200 bg-slate-50/70 hover:bg-slate-100/80 hover:border-slate-300 transition-all text-left cursor-pointer group"
              >
                <div className="w-7 h-7 rounded-md bg-purple-100 text-purple-700 flex items-center justify-center shrink-0 group-hover:scale-105 transition-transform">
                  <UserCheck className="w-4 h-4" />
                </div>
                <div className="min-w-0">
                  <div className="text-xs font-semibold text-slate-800 truncate">Quản trị viên</div>
                  <div className="text-[10px] text-slate-500 truncate">Administrator</div>
                </div>
              </button>

              <button
                type="button"
                aria-label="Auditor"
                onClick={() => handleQuickPersona('compliance_auditor', 'Auditor')}
                className="flex items-center gap-2.5 p-2.5 rounded-lg border border-slate-200 bg-slate-50/70 hover:bg-slate-100/80 hover:border-slate-300 transition-all text-left cursor-pointer group"
              >
                <div className="w-7 h-7 rounded-md bg-amber-100 text-amber-700 flex items-center justify-center shrink-0 group-hover:scale-105 transition-transform">
                  <ShieldCheck className="w-4 h-4" />
                </div>
                <div className="min-w-0">
                  <div className="text-xs font-semibold text-slate-800 truncate">Kiểm toán viên</div>
                  <div className="text-[10px] text-slate-500 truncate">Auditor</div>
                </div>
              </button>
            </div>
          </div>
        </div>

        {/* Footer & Compliance Notice */}
        <div className="text-center space-y-1.5 pt-2">
          <div className="inline-flex items-center gap-1.5 text-[11px] text-slate-500">
            <span className="w-1.5 h-1.5 rounded-full bg-emerald-500"></span>
            <span>Mã hóa bảo mật TLS 1.3 · Audit Trail lưu vết theo chuẩn kiểm toán</span>
          </div>
          <p className="text-xs text-slate-400">
            OpsBridge Reliability Platform &copy; 2026. Microservices &amp; 3-Way Matching Engine.
          </p>
        </div>
      </div>
    </div>
  );
}
