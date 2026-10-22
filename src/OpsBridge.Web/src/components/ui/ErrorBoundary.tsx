import React, { Component, type ReactNode } from 'react';
import { AlertTriangle, RefreshCw, Home } from 'lucide-react';
import { Button } from './Button';

interface Props {
  children: ReactNode;
}

interface State {
  hasError: boolean;
  error: Error | null;
}

export class ErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    console.error('ErrorBoundary caught an unhandled error:', error, errorInfo);
  }

  handleReset = () => {
    this.setState({ hasError: false, error: null });
    window.location.reload();
  };

  handleGoHome = () => {
    this.setState({ hasError: false, error: null });
    window.location.href = '/dashboard';
  };

  render() {
    if (this.state.hasError) {
      return (
        <div className="min-h-screen bg-slate-50 flex items-center justify-center p-4">
          <div className="max-w-md w-full bg-white rounded-2xl shadow-xl border border-slate-200 p-6 text-center space-y-4">
            <div className="w-14 h-14 rounded-2xl bg-rose-50 text-rose-600 flex items-center justify-center mx-auto border border-rose-100">
              <AlertTriangle className="w-8 h-8" />
            </div>
            <div>
              <h2 className="text-xl font-bold text-slate-900">Đã xảy ra sự cố hiển thị</h2>
              <p className="text-xs text-slate-500 mt-1">
                OpsBridge phát hiện một ngoại lệ render. Dữ liệu của bạn vẫn an toàn.
              </p>
            </div>
            {this.state.error && (
              <div className="p-3 bg-slate-50 border border-slate-200 rounded-lg text-left">
                <span className="text-[11px] font-mono text-rose-600 block break-all">
                  {this.state.error.message || 'Lỗi không xác định'}
                </span>
              </div>
            )}
            <div className="flex items-center justify-center gap-3 pt-2">
              <Button
                variant="outline"
                size="sm"
                onClick={this.handleReset}
                className="flex items-center gap-1.5"
              >
                <RefreshCw className="w-4 h-4" />
                <span>Tải lại trang</span>
              </Button>
              <Button
                variant="primary"
                size="sm"
                onClick={this.handleGoHome}
                className="flex items-center gap-1.5 bg-indigo-600 hover:bg-indigo-700"
              >
                <Home className="w-4 h-4" />
                <span>Về Tổng quan</span>
              </Button>
            </div>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}
