# Burn-rate alert rules

Multi-window burn-rate alerts derived from the SLO table in `slo.md`.

## Fast burn

Consumes 2% of the error budget in 15 minutes. Fires immediately — page.

## Slow burn

Consumes 5% of the error budget in 6 hours. Fires on the next business day — ticket.

## Mapping to Prometheus rules

Each SLO in `slo.md` with a PromQL-based SLI gets two `PrometheusRule` entries:

| SLO | Fast burn alert | Slow burn alert |
|-----|-----------------|-----------------|
| opsbridge-api-availability | OpsBridgeAPIDownFastBurn | OpsBridgeAPIDownSlowBurn |
| opsbridge-api-latency-p95 | OpsBridgeAPILatencyFastBurn | OpsBridgeAPILatencySlowBurn |

Gate-based SLOs (zero-oversell, in-flight-saturation) are enforced by the
concurrency harness in `load-tests/` and do not generate burn-rate alerts.

## Severity

| Condition | Severity | Route |
|-----------|----------|-------|
| Service down (up == 0 for 2m) | critical | Alertmanager route `log-only` |
| Error budget burned > 2% in 15m | critical | Alertmanager route `log-only` |
| Error budget burned > 5% in 6h | warning | Alertmanager route `log-only` |
