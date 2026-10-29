# OpsBridge SLO / SLI Definitions

This file documents the machine-readable Service-Level Objectives for the OpsBridge
bounded context. It mirrors `observability/slo.yaml` and is the canonical source
for burn-rate alert thresholds.

## Convention

Every SLO carries: name, SLI (PromQL or gate), target, window, error budget, and
alert. `sli: gate` means the objective is enforced by a test/load harness rather
than by a Prometheus metric.

## SLIs

| SLI | Source | Target | Window | Error budget | Alert |
|-----|--------|--------|--------|--------------|-------|
| zero-oversell | gate (concurrency harness) | 0 | 30d | 0 | ServiceDown |
| in-flight-saturation | `flashsale_http_in_flight` | 50 | 30d | 20% | ServiceDown |
| opsbridge-api-availability | `up{job="opsbridge"}` | 99.9% | 30d | 0.1% | ServiceDown |
| opsbridge-api-latency-p95 | `histogram_quantile(0.95, rate(opsbridge_http_request_duration_seconds_bucket[5m]))` | 2s | 30d | 10% | ServiceSlow |

## Burn-rate alerts

Fast burn (burning 2% of the error budget in 15 min) and slow burn (burning 5%
of the error budget in 6h) alerts are derived from each SLO above. The alert
rules live in `observability/prometheus/alerts.yml` and are evaluated by the
Prometheus stack bound to AKS via `ServiceMonitor` + `Alertmanager` route
(see `infrastructure/kubernetes/base/observability/`).

## Review cadence

SLO targets are reviewed quarterly and after any incident that consumed more
than 50% of the error budget in a single window.
