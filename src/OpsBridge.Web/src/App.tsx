import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider } from './features/auth/AuthContext';
import { ProtectedRoute } from './features/auth/ProtectedRoute';
import { LoginPage } from './features/auth/LoginPage';
import { AppLayout } from './components/layout/AppLayout';
import { ErrorBoundary } from './components/ui/ErrorBoundary';

import { DashboardPage } from './features/dashboard/DashboardPage';
import { PurchaseOrdersPage } from './features/purchase-orders/PurchaseOrdersPage';
import { GoodsReceiptsPage } from './features/goods-receipts/GoodsReceiptsPage';
import { InvoicesPage } from './features/invoices/InvoicesPage';
import { MatchingWorkspacePage } from './features/reconciliation/MatchingWorkspacePage';
import { DiscrepanciesPage } from './features/discrepancies/DiscrepanciesPage';
import { IntegrationOpsPage } from './features/integrations/IntegrationOpsPage';
import { AuditTimelinePage } from './features/audit/AuditTimelinePage';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5000,
      retry: 1,
      refetchOnWindowFocus: false,
    },
  },
});

export default function App() {
  return (
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <BrowserRouter>
            <Routes>
              {/* Public Auth Route */}
              <Route path="/login" element={<LoginPage />} />

              {/* Protected Enterprise Routes */}
              <Route
                path="/"
                element={
                  <ProtectedRoute>
                    <AppLayout />
                  </ProtectedRoute>
                }
              >
                <Route index element={<Navigate to="/dashboard" replace />} />
                <Route path="dashboard" element={<DashboardPage />} />
                <Route path="purchase-orders" element={<PurchaseOrdersPage />} />
                <Route path="goods-receipts" element={<GoodsReceiptsPage />} />
                <Route path="invoices" element={<InvoicesPage />} />
                <Route path="reconciliation" element={<MatchingWorkspacePage />} />
                <Route path="discrepancies" element={<DiscrepanciesPage />} />
                <Route path="integrations" element={<IntegrationOpsPage />} />
                <Route path="audit" element={<AuditTimelinePage />} />
              </Route>

              {/* Fallback */}
              <Route path="*" element={<Navigate to="/dashboard" replace />} />
            </Routes>
          </BrowserRouter>
        </AuthProvider>
      </QueryClientProvider>
    </ErrorBoundary>
  );
}
