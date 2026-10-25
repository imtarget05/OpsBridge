import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../../lib/api-client';
import { useAuth } from '../auth/AuthContext';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/Card';
import { Button } from '../../components/ui/Button';
import { Modal } from '../../components/ui/Modal';
import { formatDate } from '../../lib/formatters';
import type { DeadLetterEvent } from '../../types';
import {
  Network,
  RotateCw,
  AlertOctagon,
  CheckCircle2,
  Server,
  Activity,
  Layers,
  Code,
  RefreshCw,
  Lock,
  Cloud,
  Radio,
} from 'lucide-react';

export function IntegrationOpsPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const [selectedDlq, setSelectedDlq] = useState<DeadLetterEvent | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  const canReplay = user?.role === 'Admin' || user?.role === 'Operator';

  // Load Connectors
  const { data: connectors, refetch: refetchConnectors } = useQuery({
    queryKey: ['connectors'],
    queryFn: () => api.getConnectors(),
  });

  const isCloudEnv = typeof window !== 'undefined' &&
    window.location.hostname !== 'localhost' &&
    window.location.hostname !== '127.0.0.1';

  const factoryFlowConnector = connectors?.find((c: any) =>
    c.id === 'conn-factoryflow' || c.name?.toLowerCase().includes('factoryflow')
  );

  const activeBaseUrl = factoryFlowConnector?.baseUrl || 'Chưa cấu hình';

  const [pingResult, setPingResult] = useState<{
    success: boolean;
    latencyMs: number;
    message: string;
    targetUrl: string;
  } | null>(null);

  const pingMutation = useMutation({
    mutationFn: () => api.pingConnector(factoryFlowConnector?.id ?? 'conn-factoryflow'),
    onSuccess: (res) => {
      setPingResult(res);
      setTimeout(() => setPingResult(null), 8000);
    },
    onError: (error) => {
      // Show actual error from API instead of mock fallback
      setPingResult({
        success: false,
        latencyMs: 0,
        message: error instanceof Error ? error.message : 'Không thể kết nối đến connector',
        targetUrl: activeBaseUrl,
      });
      setTimeout(() => setPingResult(null), 8000);
    },
  });

  // Load Ingestion Events
  const { data: ingestedEvents, isLoading: loadingIngested, refetch: refetchIngested } = useQuery({
    queryKey: ['ingested-events'],
    queryFn: () => api.getIngestedEvents(),
    refetchInterval: 10000,
  });

  // Load DLQ Events
  const { data: dlqEvents, isLoading: loadingDlq, refetch: refetchDlq } = useQuery({
    queryKey: ['dead-letter-events'],
    queryFn: () => api.getDeadLetterEvents(),
    refetchInterval: 10000,
  });

  // Replay Mutation
  const replayMutation = useMutation({
    mutationFn: (eventId: string) => api.replayEvent(eventId),
    onSuccess: (_data, eventId) => {
      setSuccessMessage(`Event ${eventId} successfully replayed by ${user?.username} and queued for reprocessing.`);
      queryClient.invalidateQueries({ queryKey: ['dead-letter-events'] });
      queryClient.invalidateQueries({ queryKey: ['dashboard-metrics'] });
      setTimeout(() => setSuccessMessage(null), 6000);
    },
  });

  const handleReplay = (dlq: DeadLetterEvent) => {
    if (!canReplay || dlq.isReplayed) return;
    replayMutation.mutate(dlq.eventId);
  };

  const handleRefreshAll = () => {
    refetchIngested();
    refetchDlq();
    refetchConnectors();
  };

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-slate-900 flex items-center gap-2">
            <Network className="w-6 h-6 text-blue-600" />
            <span>Integration Operations & Event Mesh</span>
          </h1>
          <p className="text-sm text-slate-500 mt-1">
            FactoryFlow integration connectors, HMAC webhook ingestion telemetry, and RabbitMQ Dead-Letter Queue (DLQ) operations.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={handleRefreshAll}
            className="flex items-center gap-1.5"
          >
            <RefreshCw className="w-4 h-4 text-slate-500" />
            <span>Refresh Telemetry</span>
          </Button>
        </div>
      </div>

      {successMessage && (
        <div className="p-4 bg-emerald-50 border border-emerald-200 rounded-lg text-sm text-emerald-800 flex items-center gap-2">
          <CheckCircle2 className="w-5 h-5 flex-shrink-0 text-emerald-600" />
          <span>{successMessage}</span>
        </div>
      )}

      {/* Integration Services Architecture Cards */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <Card className="border-t-4 border-t-blue-500 shadow-sm">
          <CardHeader className="py-3 px-4 border-b border-slate-100 bg-slate-50">
            <div className="flex items-center justify-between">
              <span className="text-xs uppercase font-semibold text-slate-700 flex items-center gap-1.5">
                <Server className="w-4 h-4 text-blue-600" />
                FactoryFlow REST Connector
              </span>
              <span className={`text-[10px] font-semibold px-2 py-0.5 rounded-full flex items-center gap-1 ${
                isCloudEnv
                  ? 'bg-emerald-100 text-emerald-800 border border-emerald-200'
                  : 'bg-blue-100 text-blue-800 border border-blue-200'
              }`}>
                {isCloudEnv ? <Cloud className="w-3 h-3 text-emerald-600" /> : <Server className="w-3 h-3 text-blue-600" />}
                <span>{isCloudEnv ? 'CLOUD UI' : 'LOCAL DEV'}</span>
              </span>
            </div>
          </CardHeader>
          <CardContent className="p-4 text-xs space-y-2.5">
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Connector Type:</span>
              <span className="font-mono font-medium text-slate-800">REST Client Adapter</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Target System:</span>
              <span className="font-mono text-slate-800">FactoryFlow / MiniERP</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Base URL:</span>
              <span className="font-mono text-blue-600 font-semibold truncate max-w-[210px]" title={activeBaseUrl}>
                {activeBaseUrl}
              </span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Auth Mechanism:</span>
              <span className="text-slate-800">Bearer Token / Mutual TLS</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Runtime Env:</span>
              <span className="text-slate-700 font-medium">{isCloudEnv ? 'Cloud (Azure Container Apps)' : 'Local Workstation (:5001)'}</span>
            </div>

            <div className="pt-2 border-t border-slate-100 space-y-2">
              <Button
                variant="outline"
                size="sm"
                onClick={() => pingMutation.mutate()}
                disabled={pingMutation.isPending || !factoryFlowConnector}
                className="w-full flex items-center justify-center gap-1.5 text-xs border-indigo-200 text-indigo-700 bg-indigo-50/50 hover:bg-indigo-100/70 cursor-pointer shadow-xs"
              >
                <Radio className={`w-3.5 h-3.5 ${pingMutation.isPending ? 'animate-pulse text-indigo-600' : ''}`} />
                <span>{pingMutation.isPending ? 'Đang kiểm tra kết nối Cloud...' : 'Kiểm tra kết nối (Test Ping Cloud)'}</span>
              </Button>

              {pingResult && (
                <div role="status" className={`p-2 rounded-lg border text-[11px] flex items-center gap-2 ${
                  pingResult.success
                    ? 'bg-emerald-50 border-emerald-200 text-emerald-800'
                    : 'bg-rose-50 border-rose-200 text-rose-800'
                }`}>
                  <span className="flex items-center gap-1">
                    {pingResult.success
                      ? <CheckCircle2 className="w-3.5 h-3.5 text-emerald-600 shrink-0" />
                      : <AlertOctagon className="w-3.5 h-3.5 text-rose-600 shrink-0" />}
                    <span>{pingResult.success ? `Kết nối thành công · ${pingResult.latencyMs}ms` : 'Kết nối thất bại'}: {pingResult.message}</span>
                  </span>
                </div>
              )}
            </div>
          </CardContent>
        </Card>

        <Card className="border-t-4 border-t-purple-500 shadow-sm">
          <CardHeader className="py-3 px-4 border-b border-slate-100 bg-slate-50">
            <div className="flex items-center justify-between">
              <span className="text-xs uppercase font-semibold text-slate-700 flex items-center gap-1.5">
                <Activity className="w-4 h-4 text-purple-600" />
                Webhook Ingestion Engine
              </span>
              <span className="text-[10px] font-semibold px-2 py-0.5 rounded-full bg-emerald-100 text-emerald-800 border border-emerald-200">
                LISTENING
              </span>
            </div>
          </CardHeader>
          <CardContent className="p-4 text-xs space-y-2.5">
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Webhook Endpoint:</span>
              <span className="font-mono text-purple-700 font-medium truncate max-w-[210px]" title={isCloudEnv ? `${typeof window !== 'undefined' ? window.location.origin : ''}/api/v1/webhooks/factoryflow` : '/api/v1/webhooks/factoryflow'}>
                {isCloudEnv && typeof window !== 'undefined' ? `${window.location.origin}/api/v1/webhooks/factoryflow` : '/api/v1/webhooks/factoryflow'}
              </span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Security:</span>
              <span className="text-slate-800">HMAC-SHA256 (Svix format)</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Timestamp Tolerance:</span>
              <span className="font-mono text-slate-800">±300 seconds</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Inbox Guarantee:</span>
              <span className="text-emerald-700 font-medium">PostgreSQL Transactional</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-slate-500">Mesh Topology:</span>
              <span className="text-slate-700 font-medium">{isCloudEnv ? 'Edge Ingress Gateway (Global)' : 'Local Development Server'}</span>
            </div>
          </CardContent>
        </Card>

        <Card className="border-t-4 border-t-amber-500 shadow-sm">
          <CardHeader className="py-3 px-4 border-b border-slate-100 bg-slate-50">
            <div className="flex items-center justify-between">
              <span className="text-xs uppercase font-semibold text-slate-700 flex items-center gap-1.5">
                <Layers className="w-4 h-4 text-amber-600" />
                RabbitMQ Message Broker
              </span>
              <span className="text-[10px] font-semibold px-2 py-0.5 rounded-full bg-emerald-100 text-emerald-800 border border-emerald-200">
                DURABLE
              </span>
            </div>
          </CardHeader>
          <CardContent className="p-4 text-xs space-y-2">
            <div className="flex justify-between">
              <span className="text-slate-500">Exchange:</span>
              <span className="font-mono text-amber-800">factoryflow.exchange (direct)</span>
            </div>
            <div className="flex justify-between">
              <span className="text-slate-500">Primary Queue:</span>
              <span className="font-mono text-slate-800">factoryflow.goods-receipt</span>
            </div>
            <div className="flex justify-between">
              <span className="text-slate-500">Dead-Letter Queue:</span>
              <span className="font-mono text-rose-700">opsbridge.dlq</span>
            </div>
            <div className="flex justify-between">
              <span className="text-slate-500">Delivery Mode:</span>
              <span className="text-slate-800">Persistent (DeliveryMode 2)</span>
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Dead-Letter Queue (DLQ) Table */}
      <Card>
        <CardHeader className="border-b border-slate-100 pb-3 flex flex-col sm:flex-row sm:items-center justify-between gap-2">
          <div>
            <CardTitle className="text-base font-semibold text-slate-900 flex items-center gap-2">
              <AlertOctagon className="w-4 h-4 text-rose-600" />
              <span>Dead-Letter Queue (DLQ) & Manual Replay Engine</span>
            </CardTitle>
            <p className="text-xs text-slate-500 mt-0.5">
              Poison messages and unprocessable payloads isolated after exhausting retry limits.
            </p>
          </div>
          {!canReplay && (
            <div className="flex items-center gap-1.5 text-xs text-amber-700 bg-amber-50 px-2.5 py-1 rounded border border-amber-200">
              <Lock className="w-3.5 h-3.5" />
              <span>Operator or Admin role required to replay DLQ</span>
            </div>
          )}
        </CardHeader>
        <div className="overflow-x-auto">
          {loadingDlq ? (
            <div className="p-8 text-center text-sm text-slate-500">Loading dead-letter events...</div>
          ) : !dlqEvents || dlqEvents.length === 0 ? (
            <div className="p-10 text-center">
              <CheckCircle2 className="w-8 h-8 text-emerald-500 mx-auto mb-2" />
              <p className="text-sm font-semibold text-slate-900">DLQ is Empty</p>
              <p className="text-xs text-slate-500 mt-0.5">
                All background message consumers are healthy with zero discarded payloads.
              </p>
            </div>
          ) : (
            <table className="w-full text-left text-sm text-slate-600">
              <thead className="bg-slate-50 text-xs uppercase font-semibold text-slate-500 border-b border-slate-100">
                <tr>
                  <th className="py-3 px-4">Event ID</th>
                  <th className="py-3 px-4">Event Type</th>
                  <th className="py-3 px-4">Topic / Routing</th>
                  <th className="py-3 px-4">Failure Reason</th>
                  <th className="py-3 px-4 text-center">Retries</th>
                  <th className="py-3 px-4">Failed At</th>
                  <th className="py-3 px-4">Status</th>
                  <th className="py-3 px-4 text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 font-normal">
                {dlqEvents.map((dlq) => (
                  <tr key={dlq.id} className="hover:bg-slate-50/75 transition-colors">
                    <td className="py-3 px-4 font-mono text-xs text-slate-900 font-semibold truncate max-w-[140px]">
                      {dlq.eventId}
                    </td>
                    <td className="py-3 px-4 font-mono text-xs text-blue-600">
                      {dlq.eventType}
                    </td>
                    <td className="py-3 px-4 font-mono text-xs text-slate-500">
                      {dlq.topic}
                    </td>
                    <td className="py-3 px-4 text-xs text-rose-700 font-medium truncate max-w-[200px]" title={dlq.reason}>
                      {dlq.reason}
                    </td>
                    <td className="py-3 px-4 text-center font-mono text-xs font-bold text-slate-700">
                      {dlq.retryCount}
                    </td>
                    <td className="py-3 px-4 text-xs text-slate-500 whitespace-nowrap">
                      {formatDate(dlq.failedAt)}
                    </td>
                    <td className="py-3 px-4">
                      {dlq.isReplayed ? (
                        <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[10px] font-semibold bg-emerald-100 text-emerald-800">
                          Replayed
                        </span>
                      ) : (
                        <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[10px] font-semibold bg-rose-100 text-rose-800">
                          Dead Letter
                        </span>
                      )}
                    </td>
                    <td className="py-3 px-4 text-right">
                      <div className="flex items-center justify-end gap-1.5">
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={() => setSelectedDlq(dlq)}
                          title="View Raw Payload"
                          className="h-8 w-8 p-0"
                        >
                          <Code className="w-4 h-4 text-slate-600" />
                        </Button>
                        <Button
                          variant="outline"
                          size="sm"
                          disabled={!canReplay || dlq.isReplayed || replayMutation.isPending}
                          onClick={() => handleReplay(dlq)}
                          className="flex items-center gap-1 text-xs py-1 px-2.5 h-8 border-rose-200 text-rose-700 hover:bg-rose-50 disabled:opacity-40"
                        >
                          <RotateCw className={`w-3.5 h-3.5 ${replayMutation.isPending ? 'animate-spin' : ''}`} />
                          <span>{dlq.isReplayed ? 'Replayed' : 'Replay'}</span>
                        </Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </Card>

      {/* Ingestion Stream */}
      <Card>
        <CardHeader className="border-b border-slate-100 pb-3">
          <CardTitle className="text-base font-semibold text-slate-900 flex items-center gap-2">
            <Activity className="w-4 h-4 text-purple-600" />
            <span>Recent Ingested Events Stream</span>
          </CardTitle>
        </CardHeader>
        <div className="overflow-x-auto">
          {loadingIngested ? (
            <div className="p-8 text-center text-sm text-slate-500">Loading ingested stream...</div>
          ) : !ingestedEvents || ingestedEvents.length === 0 ? (
            <div className="p-8 text-center text-sm text-slate-400">No ingested webhook events recorded yet.</div>
          ) : (
            <table className="w-full text-left text-sm text-slate-600">
              <thead className="bg-slate-50 text-xs uppercase font-semibold text-slate-500 border-b border-slate-100">
                <tr>
                  <th className="py-3 px-4">Event ID</th>
                  <th className="py-3 px-4">Type</th>
                  <th className="py-3 px-4">Source</th>
                  <th className="py-3 px-4">Occurred At</th>
                  <th className="py-3 px-4">Status</th>
                  <th className="py-3 px-4">Received At</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 font-mono text-xs">
                {ingestedEvents.map((ev: any) => (
                  <tr key={ev.eventId} className="hover:bg-slate-50/75">
                    <td className="py-2.5 px-4 font-semibold text-slate-900 truncate max-w-[150px]">
                      {ev.eventId}
                    </td>
                    <td className="py-2.5 px-4 text-blue-600">{ev.eventType}</td>
                    <td className="py-2.5 px-4 text-slate-700">{ev.source}</td>
                    <td className="py-2.5 px-4 text-slate-500 font-sans">{formatDate(ev.occurredAt)}</td>
                    <td className="py-2.5 px-4 font-sans">
                      <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-[10px] font-semibold ${
                        ev.status === 'Processed'
                          ? 'bg-emerald-100 text-emerald-800'
                          : ev.status === 'Failed'
                          ? 'bg-rose-100 text-rose-800'
                          : 'bg-blue-100 text-blue-800'
                      }`}>
                        {ev.status}
                      </span>
                    </td>
                    <td className="py-2.5 px-4 text-slate-500 font-sans">{formatDate(ev.receivedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </Card>

      {/* Payload Modal */}
      {selectedDlq && (
        <Modal
          isOpen={!!selectedDlq}
          onClose={() => setSelectedDlq(null)}
          title={`DLQ Payload: ${selectedDlq.eventType}`}
          className="max-w-2xl"
        >
          <div className="space-y-3">
            <div className="p-3 bg-rose-50 border border-rose-200 rounded-md text-xs space-y-1">
              <div className="font-semibold text-rose-900">Failure Details</div>
              <p className="text-rose-700 font-mono">{selectedDlq.reason}</p>
              <div className="text-slate-500 pt-1">
                Failed At: {formatDate(selectedDlq.failedAt)} | Retries: {selectedDlq.retryCount}
              </div>
            </div>

            <div>
              <label className="text-xs font-semibold text-slate-700 block mb-1">Raw Message Body</label>
              <pre className="p-3 bg-slate-900 text-slate-100 rounded-md text-xs font-mono overflow-x-auto max-h-96">
                {(() => {
                  try {
                    return JSON.stringify(JSON.parse(selectedDlq.payload), null, 2);
                  } catch {
                    return selectedDlq.payload;
                  }
                })()}
              </pre>
            </div>

            <div className="flex justify-between items-center pt-2">
              <Button variant="outline" size="sm" onClick={() => setSelectedDlq(null)}>
                Close
              </Button>
              <Button
                variant="primary"
                size="sm"
                disabled={!canReplay || selectedDlq.isReplayed || replayMutation.isPending}
                onClick={() => {
                  handleReplay(selectedDlq);
                  setSelectedDlq(null);
                }}
                className="flex items-center gap-1.5"
              >
                <RotateCw className="w-3.5 h-3.5" />
                <span>Replay to RabbitMQ Outbox</span>
              </Button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}
