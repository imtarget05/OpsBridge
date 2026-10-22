import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api-client';
import { useAuth } from '../auth/AuthContext';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/Card';
import { Button } from '../../components/ui/Button';
import { Modal } from '../../components/ui/Modal';
import { Select } from '../../components/ui/Select';
import { StatusBadge, DiscrepancySeverityBadge } from '../../components/ui/Badge';
import { formatCurrency, formatDate } from '../../lib/formatters';
import type { Discrepancy, DiscrepancyStatus, ReviewAction } from '../../types';
import {
  AlertTriangle,
  CheckCircle2,
  XCircle,
  Clock,
  Search,
  Filter,
  Lock,
  UserCheck,
} from 'lucide-react';

export function DiscrepanciesPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const [statusFilter, setStatusFilter] = useState<string>('ALL');
  const [searchTerm, setSearchTerm] = useState('');
  const [selectedDiscrepancy, setSelectedDiscrepancy] = useState<Discrepancy | null>(null);

  // Review Modal State
  const [reviewAction, setReviewAction] = useState<ReviewAction>('ApprovedWithTolerance');
  const [reviewNotes, setReviewNotes] = useState('Reviewed and approved variance per procurement authorization.');

  const canReview = user?.role === 'FinanceReviewer' || user?.role === 'Admin';

  const { data: discrepancies, isLoading, error } = useQuery({
    queryKey: ['discrepancies', statusFilter],
    queryFn: () =>
      api.getDiscrepancies(
        statusFilter === 'ALL' ? undefined : (statusFilter as DiscrepancyStatus)
      ),
  });

  const reviewMutation = useMutation({
    mutationFn: ({ id, action, notes }: { id: string; action: ReviewAction; notes: string }) =>
      api.reviewDiscrepancy(id, action, notes),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['discrepancies'] });
      queryClient.invalidateQueries({ queryKey: ['invoices'] });
      queryClient.invalidateQueries({ queryKey: ['dashboard-metrics'] });
      setSelectedDiscrepancy(null);
    },
  });

  const handleReviewSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!selectedDiscrepancy || !canReview) return;
    reviewMutation.mutate({
      id: selectedDiscrepancy.id,
      action: reviewAction,
      notes: reviewNotes,
    });
  };

  const filtered = (discrepancies || []).filter((d) => {
    const matchesSearch =
      d.type.toLowerCase().includes(searchTerm.toLowerCase()) ||
      d.itemSku?.toLowerCase().includes(searchTerm.toLowerCase()) ||
      d.details.toLowerCase().includes(searchTerm.toLowerCase());
    return matchesSearch;
  });

  const counts = {
    total: discrepancies?.length || 0,
    open: discrepancies?.filter((d) => d.status === 'Open').length || 0,
    underReview: discrepancies?.filter((d) => d.status === 'UnderReview').length || 0,
    resolved: discrepancies?.filter((d) => d.status === 'Resolved' || d.status === 'ApprovedWithVariance').length || 0,
    rejected: discrepancies?.filter((d) => d.status === 'Rejected').length || 0,
  };

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-slate-900 flex items-center gap-2">
            <AlertTriangle className="w-6 h-6 text-rose-600" />
            <span>Discrepancy Investigation & Review</span>
          </h1>
          <p className="text-sm text-slate-500 mt-1">
            Accounts payable variances, price/quantity mismatches, and compliance review workflows.
          </p>
        </div>
        <div className="flex items-center gap-2">
          {!canReview && (
            <div className="flex items-center gap-1.5 px-3 py-1.5 bg-amber-50 border border-amber-200 rounded-md text-xs text-amber-800">
              <Lock className="w-3.5 h-3.5 text-amber-600" />
              <span>Read-Only: Switch to <b>FinanceReviewer</b> to approve or reject</span>
            </div>
          )}
        </div>
      </div>

      {/* Summary KPI Cards */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <Card>
          <CardContent className="p-4 flex items-center justify-between">
            <div>
              <p className="text-xs font-medium text-slate-500 uppercase tracking-wider">Open Variances</p>
              <p className="text-2xl font-bold text-rose-600 mt-1">{counts.open}</p>
            </div>
            <div className="w-10 h-10 rounded-lg bg-rose-50 flex items-center justify-center text-rose-600">
              <AlertTriangle className="w-5 h-5" />
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-4 flex items-center justify-between">
            <div>
              <p className="text-xs font-medium text-slate-500 uppercase tracking-wider">Under Review</p>
              <p className="text-2xl font-bold text-amber-600 mt-1">{counts.underReview}</p>
            </div>
            <div className="w-10 h-10 rounded-lg bg-amber-50 flex items-center justify-center text-amber-600">
              <Clock className="w-5 h-5" />
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-4 flex items-center justify-between">
            <div>
              <p className="text-xs font-medium text-slate-500 uppercase tracking-wider">Resolved / Approved</p>
              <p className="text-2xl font-bold text-emerald-600 mt-1">{counts.resolved}</p>
            </div>
            <div className="w-10 h-10 rounded-lg bg-emerald-50 flex items-center justify-center text-emerald-600">
              <CheckCircle2 className="w-5 h-5" />
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardContent className="p-4 flex items-center justify-between">
            <div>
              <p className="text-xs font-medium text-slate-500 uppercase tracking-wider">Rejected</p>
              <p className="text-2xl font-bold text-slate-600 mt-1">{counts.rejected}</p>
            </div>
            <div className="w-10 h-10 rounded-lg bg-slate-100 flex items-center justify-center text-slate-600">
              <XCircle className="w-5 h-5" />
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Filter and Search Bar */}
      <Card>
        <CardContent className="p-4">
          <div className="flex flex-col sm:flex-row items-center gap-3">
            <div className="relative flex-1 w-full">
              <Search className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" />
              <input
                type="text"
                placeholder="Search by discrepancy type, SKU, details..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                className="w-full pl-9 pr-4 py-2 border border-slate-200 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
            </div>
            <div className="flex items-center gap-2 w-full sm:w-auto">
              <Filter className="w-4 h-4 text-slate-400" />
              <Select
                value={statusFilter}
                onChange={(e) => setStatusFilter(e.target.value)}
                className="text-sm min-w-[160px]"
              >
                <option value="ALL">All Statuses</option>
                <option value="Open">Open</option>
                <option value="UnderReview">Under Review</option>
                <option value="Resolved">Resolved</option>
                <option value="Rejected">Rejected</option>
              </Select>
            </div>
          </div>
        </CardContent>
      </Card>

      {/* Table */}
      <Card>
        <CardHeader className="border-b border-slate-100 pb-3">
          <CardTitle className="text-base font-semibold text-slate-900 flex items-center gap-2">
            <AlertTriangle className="w-4 h-4 text-rose-500" />
            <span>Discrepancies Log ({filtered.length})</span>
          </CardTitle>
        </CardHeader>
        <div className="overflow-x-auto">
          {isLoading ? (
            <div className="p-8 text-center text-sm text-slate-500">Loading discrepancies...</div>
          ) : error ? (
            <div className="p-8 text-center text-sm text-rose-600">Failed to load discrepancies.</div>
          ) : filtered.length === 0 ? (
            <div className="p-12 text-center">
              <CheckCircle2 className="w-10 h-10 text-emerald-400 mx-auto mb-3" />
              <p className="text-sm font-medium text-slate-900">Zero discrepancies found</p>
              <p className="text-xs text-slate-500 mt-1">
                All accounts payable invoices matched within policy tolerances.
              </p>
            </div>
          ) : (
            <table className="w-full text-left text-sm text-slate-600">
              <thead className="bg-slate-50 text-xs uppercase font-semibold text-slate-500 border-b border-slate-100">
                <tr>
                  <th className="py-3 px-4">Type</th>
                  <th className="py-3 px-4">Severity</th>
                  <th className="py-3 px-4">Item SKU</th>
                  <th className="py-3 px-4">Expected Value</th>
                  <th className="py-3 px-4">Actual Value</th>
                  <th className="py-3 px-4 text-right">Variance</th>
                  <th className="py-3 px-4">Status</th>
                  <th className="py-3 px-4 text-right">Action</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 font-normal">
                {filtered.map((d) => (
                  <tr key={d.id} className="hover:bg-slate-50/75 transition-colors">
                    <td className="py-3 px-4">
                      <div className="font-semibold text-slate-900">{d.type}</div>
                      <div className="text-xs text-slate-400 truncate max-w-[280px]">{d.details}</div>
                    </td>
                    <td className="py-3 px-4">
                      <DiscrepancySeverityBadge severity={d.severity} />
                    </td>
                    <td className="py-3 px-4 font-mono font-medium text-slate-800">
                      {d.itemSku || '—'}
                    </td>
                    <td className="py-3 px-4 font-mono text-xs text-slate-500">
                      {d.expectedValue}
                    </td>
                    <td className="py-3 px-4 font-mono text-xs font-semibold text-rose-700">
                      {d.actualValue}
                    </td>
                    <td className="py-3 px-4 text-right font-mono font-semibold">
                      {d.varianceAmount ? formatCurrency(d.varianceAmount, 'VND') : '—'}
                    </td>
                    <td className="py-3 px-4">
                      <StatusBadge status={d.status} />
                    </td>
                    <td className="py-3 px-4 text-right">
                      <Button
                        variant={d.status === 'Open' ? 'primary' : 'outline'}
                        size="sm"
                        onClick={() => setSelectedDiscrepancy(d)}
                        className="text-xs py-1 px-2.5 h-8"
                      >
                        {d.status === 'Open' ? 'Review Variance' : 'View Decision'}
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </Card>

      {/* Review Modal */}
      {selectedDiscrepancy && (
        <Modal
          isOpen={!!selectedDiscrepancy}
          onClose={() => setSelectedDiscrepancy(null)}
          title={`Discrepancy Review: ${selectedDiscrepancy.type}`}
          className="max-w-2xl"
        >
          <div className="space-y-4">
            {/* Details Summary */}
            <div className="p-3 bg-slate-50 border border-slate-200 rounded-lg text-xs space-y-2">
              <div className="grid grid-cols-2 gap-2">
                <div>
                  <span className="text-slate-500 block">Severity:</span>
                  <DiscrepancySeverityBadge severity={selectedDiscrepancy.severity} />
                </div>
                <div>
                  <span className="text-slate-500 block">Current Status:</span>
                  <StatusBadge status={selectedDiscrepancy.status} />
                </div>
              </div>
              <div>
                <span className="text-slate-500 block">Item SKU:</span>
                <span className="font-mono font-bold text-slate-900">{selectedDiscrepancy.itemSku || '—'}</span>
              </div>
              <div className="grid grid-cols-2 gap-2">
                <div>
                  <span className="text-slate-500 block">Expected:</span>
                  <span className="font-mono text-slate-700">{selectedDiscrepancy.expectedValue}</span>
                </div>
                <div>
                  <span className="text-slate-500 block">Actual:</span>
                  <span className="font-mono font-bold text-rose-700">{selectedDiscrepancy.actualValue}</span>
                </div>
              </div>
              <div>
                <span className="text-slate-500 block">Details:</span>
                <p className="text-slate-700">{selectedDiscrepancy.details}</p>
              </div>
            </div>

            {/* If already decided, display decision record */}
            {selectedDiscrepancy.reviewDecision && (
              <div className="p-3 bg-blue-50 border border-blue-200 rounded-lg text-xs space-y-1.5">
                <div className="font-semibold text-blue-900 flex items-center gap-1.5">
                  <UserCheck className="w-4 h-4 text-blue-600" />
                  <span>Existing Review Decision</span>
                </div>
                <div className="grid grid-cols-2 gap-2 text-slate-700">
                  <div>
                    <span className="text-slate-500 block">Action:</span>
                    <span className="font-bold text-blue-800">{selectedDiscrepancy.reviewDecision.action}</span>
                  </div>
                  <div>
                    <span className="text-slate-500 block">Decided At:</span>
                    <span>{formatDate(selectedDiscrepancy.reviewDecision.decidedAt)}</span>
                  </div>
                  <div>
                    <span className="text-slate-500 block">Reviewer:</span>
                    <span>{selectedDiscrepancy.reviewDecision.reviewerUserId} ({selectedDiscrepancy.reviewDecision.reviewerRole})</span>
                  </div>
                  <div>
                    <span className="text-slate-500 block">Notes:</span>
                    <span>{selectedDiscrepancy.reviewDecision.notes}</span>
                  </div>
                </div>
              </div>
            )}

            {/* Review Form */}
            {selectedDiscrepancy.status === 'Open' || selectedDiscrepancy.status === 'UnderReview' ? (
              <form onSubmit={handleReviewSubmit} className="space-y-3 pt-2 border-t border-slate-100">
                {!canReview && (
                  <div className="p-3 bg-amber-50 border border-amber-200 rounded text-xs text-amber-800 flex items-center gap-2">
                    <Lock className="w-4 h-4 flex-shrink-0 text-amber-600" />
                    <span>
                      Your current role (<b>{user?.role}</b>) does not have authorization to record review decisions. Switch persona to <b>FinanceReviewer</b>.
                    </span>
                  </div>
                )}

                <div>
                  <label className="text-xs font-semibold text-slate-700 block mb-1">
                    Select Review Action *
                  </label>
                  <Select
                    value={reviewAction}
                    onChange={(e) => setReviewAction(e.target.value as ReviewAction)}
                    disabled={!canReview}
                    className="w-full text-xs font-medium"
                  >
                    <option value="ApprovedWithTolerance">
                      ApprovedWithTolerance (Accept variance & commit partial/full allocations)
                    </option>
                    <option value="Rejected">
                      Rejected (Reject invoice & release held goods receipt inventory)
                    </option>
                    <option value="Adjusted">
                      Adjusted (Manual price or quantity adjustment)
                    </option>
                    <option value="HoldForInvestigation">
                      HoldForInvestigation (Keep open for vendor inquiry)
                    </option>
                  </Select>
                </div>

                <div>
                  <label className="text-xs font-semibold text-slate-700 block mb-1">
                    Reviewer Notes / Justification *
                  </label>
                  <textarea
                    required
                    rows={3}
                    disabled={!canReview}
                    value={reviewNotes}
                    onChange={(e) => setReviewNotes(e.target.value)}
                    className="w-full text-xs p-2.5 border border-slate-200 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                    placeholder="Provide compliance notes or reason for approval/rejection..."
                  />
                </div>

                {reviewMutation.isError && (
                  <div className="p-3 bg-rose-50 border border-rose-200 rounded text-xs text-rose-700">
                    {(reviewMutation.error as Error).message}
                  </div>
                )}

                <div className="flex justify-end gap-2 pt-2">
                  <Button type="button" variant="outline" onClick={() => setSelectedDiscrepancy(null)}>
                    Cancel
                  </Button>
                  <Button
                    type="submit"
                    variant="primary"
                    disabled={!canReview || reviewMutation.isPending}
                  >
                    {reviewMutation.isPending ? 'Submitting...' : 'Submit Decision'}
                  </Button>
                </div>
              </form>
            ) : (
              <div className="flex justify-end pt-2">
                <Button variant="outline" onClick={() => setSelectedDiscrepancy(null)}>
                  Close
                </Button>
              </div>
            )}
          </div>
        </Modal>
      )}
    </div>
  );
}
