import React, { useState, useEffect, useCallback } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { useAuth } from './AuthContext';
import type { UserRole } from '../../types';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/Card';
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
                theme: 'filled_blue',
                size: 'large',
                width: 360,
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
    <div className="min-h-screen bg-slate-900 flex flex-col justify-center items-center p-4">
      <div className="w-full max-w-md space-y-6">
        {/* Brand Header */}
        <div className="text-center space-y-2">
          <div className="inline-flex p-3 rounded-2xl bg-indigo-600/20 text-indigo-400 border border-indigo-500/30">
            <Layers className="w-8 h-8" />
          </div>
          <h1 className="text-2xl font-bold tracking-tight text-white">OpsBridge Platform</h1>
          <p className="text-xs text-slate-400">
            Hệ Thống Đối Soát 3 Bên (PO - Goods Receipt - Invoice) & Điều Phối Vận Hành
          </p>
        </div>

        {/* Login Card */}
        <Card className="border-slate-800 bg-slate-800/80 backdrop-blur-sm shadow-2xl">
          <CardHeader className="border-slate-700/60 pb-3">
            <CardTitle className="text-white text-base text-center">
              Đăng Nhập Bàn Làm Việc Vận Hành
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 pt-3">
            {error && (
              <div className="p-3 rounded-lg bg-rose-500/10 border border-rose-500/30 text-rose-400 text-xs flex items-center gap-2">
                <AlertCircle className="w-4 h-4 shrink-0" />
                <span>{error}</span>
              </div>
            )}

            {/* Google OAuth Sign-In Section */}
            <div className="space-y-2">
              <div id="google-btn-container" className="flex justify-center min-h-[40px]"></div>

              {/* One-click Google Sign-In fallback */}
              <button
                type="button"
                onClick={handleGoogleQuickDemo}
                disabled={loading}
                className="w-full flex items-center justify-center gap-3 py-2.5 px-4 rounded-xl border border-slate-700 bg-slate-900/80 hover:bg-slate-700/60 text-slate-200 text-xs font-medium transition-all shadow-xs cursor-pointer group"
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
                <span>Đăng nhập với Google (Project 791168472055)</span>
              </button>
            </div>

            <div className="relative my-3">
              <div className="absolute inset-0 flex items-center">
                <div className="w-full border-t border-slate-700"></div>
              </div>
              <div className="relative flex justify-center text-[11px] uppercase">
                <span className="bg-slate-800 px-2 text-slate-400 font-medium">Hoặc đăng nhập với tài khoản nội bộ</span>
              </div>
            </div>

            <form onSubmit={handleSubmit} className="space-y-3">
              <div>
                <label className="block text-xs font-semibold text-slate-300 mb-1">
                  Tên đăng nhập / Email
                </label>
                <Input
                  type="text"
                  placeholder="ví dụ: alice_finance"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  className="bg-slate-900/60 border-slate-700 text-white placeholder:text-slate-500"
                />
              </div>

              <div>
                <label className="block text-xs font-semibold text-slate-300 mb-1">
                  Mật khẩu
                </label>
                <Input
                  type="password"
                  placeholder="••••••••"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="bg-slate-900/60 border-slate-700 text-white placeholder:text-slate-500"
                />
              </div>

              <Button type="submit" variant="primary" className="w-full bg-indigo-600 hover:bg-indigo-700" isLoading={loading}>
                Đăng Nhập Hệ Thống
              </Button>
            </form>

            <div className="relative my-3">
              <div className="absolute inset-0 flex items-center">
                <div className="w-full border-t border-slate-700"></div>
              </div>
              <div className="relative flex justify-center text-[10px] uppercase">
                <span className="bg-slate-800 px-2 text-slate-400 font-medium">Hoặc Chọn Vai Trò Nhanh (Demo)</span>
              </div>
            </div>

            {/* Quick Persona Buttons */}
            <div className="grid grid-cols-2 gap-2">
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => handleQuickPersona('alice_finance', 'FinanceReviewer')}
                className="bg-slate-900/40 border-slate-700 text-slate-300 hover:text-white hover:bg-slate-700/50 justify-start text-xs"
              >
                <ShieldCheck className="w-3.5 h-3.5 text-emerald-400 shrink-0" />
                <span className="truncate">Kế toán Đối soát</span>
              </Button>

              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => handleQuickPersona('bob_operator', 'Operator')}
                className="bg-slate-900/40 border-slate-700 text-slate-300 hover:text-white hover:bg-slate-700/50 justify-start text-xs"
              >
                <UserCheck className="w-3.5 h-3.5 text-blue-400 shrink-0" />
                <span className="truncate">Thủ kho Vận hành</span>
              </Button>

              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => handleQuickPersona('admin_system', 'Admin')}
                className="bg-slate-900/40 border-slate-700 text-slate-300 hover:text-white hover:bg-slate-700/50 justify-start text-xs"
              >
                <UserCheck className="w-3.5 h-3.5 text-purple-400 shrink-0" />
                <span className="truncate">Quản trị viên</span>
              </Button>

              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => handleQuickPersona('compliance_auditor', 'Auditor')}
                className="bg-slate-900/40 border-slate-700 text-slate-300 hover:text-white hover:bg-slate-700/50 justify-start text-xs"
              >
                <ShieldCheck className="w-3.5 h-3.5 text-amber-400 shrink-0" />
                <span className="truncate">Kiểm toán viên</span>
              </Button>
            </div>
          </CardContent>
        </Card>

        <p className="text-center text-xs text-slate-500">
          OpsBridge Reliability Platform &copy; 2026. Microservices & 3-Way Matching Engine.
        </p>
      </div>
    </div>
  );
}
