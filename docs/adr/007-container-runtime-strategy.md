# ADR-007: Container Runtime Strategy

- **Status**: Accepted
- **Date**: 2026-09-20

## Context
P01 compose was dev-only: root runtime, `wget` probe that cannot work
(aspnet image has no wget/curl — verified), Kafka started by default though
unused, no resource bounds, hard-coded dev credentials, ephemeral DLQ.

## Decision
- **Multi-stage** SDK→aspnet kept; `HealthProbe` (framework-dependent,
  `dotnet exec`) published alongside the API for HEALTHCHECK without new
  packages; `.dockerignore` cuts context (git, docs, tfstate, bin/obj).
- **Non-root**: built-in `app` user (uid 1654, verified `id` in both
  containers); publish output chowned, `/app/logs` writable for file DLQ.
- **Health**: `/health/live` (process alive) + `/healthz` alias;
  `/health/ready` gates on Postgres `CanConnectAsync` only. Redis/RabbitMQ
  are RUNTIME OPTIONAL (Task-2 fallback proven) so they never gate readiness.
  RabbitMQ stays STARTUP REQUIRED via `depends_on healthy` because the
  blocking ctor fails boot without a broker (known limitation).
- **Compose**: health-aware ordering; local/demo limits (api 1 CPU/1G,
  worker 0.5/512M, pg 1/1G, redis 0.5/256M, rabbit 0.5/512M),
  `unless-stopped`, 10m×3 log rotation; named volumes
  `postgres-data`/`rabbitmq-data`/`dlq-data`; Redis runs persistence-off
  (reservation tier rebuilds from Postgres each boot).
- **Kafka** → `profiles: [experimental]` opt-in; dead code confirmed ABSENT:
  quét toàn bộ `src/` và `tests/` — không có file nguồn `*KafkaActivityTracker*`
  (không có class `KafkaActivityTracker` nào trong codebase), do đó không có
  dead code để xoá và KHÔNG promote thành profile real. Dịch vụ `kafka` trong
  `docker-compose.yml` giữ nguyên profile `experimental`, không khởi động
  mặc định, không bị các service của OpsBridge phụ thuộc (xem compose comment
  tại dòng Kafka UNUSED).
- **Env**: `${VAR:-default}` everywhere + `.env.example` (`API_PORT=5000`
  default; macOS ControlCenter squats 5000, so local runs use
  `API_PORT=5065`); `.env` git-ignored (no `.env` committed).

## Trade-offs
- `libgssapi_krb5.so.2` Npgsql/Kerberos notice in logs is harmless (no GSS).
- HealthProbe adds a second publish step (~MBs) for a truthful health signal.
- DLQ volume preserves local evidence across restarts but is NOT a
  production DLQ story (Service Bus native DLQ on Azure).

## Consequences
- `docker compose build/up/ps` reproducible; API healthy in ~40s;
  restart keeps Postgres orders/stock; images ~705/710MB (SDK-based,
  debuggability over minimal size).

## Addendum — Phase 4 container gate (2026-09-20)

The gate (fresh clone → `compose up --build` → order flow → probe drills) is
recorded in `docs/evidence/container/phase4-container-gate.md`. Two corrections
to the original decision text:

1. **Readiness must consume the boolean.** The first implementation called
   `await db.Database.CanConnectAsync()` and ignored its return value, catching
   only exceptions. `CanConnectAsync` returns **false** for an unreachable
   server (it does not throw), so with PostgreSQL stopped the probe still
   answered `200 {"status":"ready"}` — a false-ready instance that Kubernetes
   would keep in the service endpoints. Fixed:

   ```csharp
   bool canConnect;
   try { canConnect = await db.Database.CanConnectAsync(); }
   catch (Exception ex) { /* log */ canConnect = false; }
   return canConnect ? Ok("ready") : Json("not-ready", 503);   // verified: 503 while PG down
   ```

   Contract for every future probe: *a probe that cannot fail is not a probe.*

2. **Readiness only applies after a successful start.** `DatabaseInitializer`
   runs migrations at boot, so with PostgreSQL unavailable the container exits
   and restart-loops instead of staying up as `not-ready`. `depends_on:
   service_healthy` covers compose; Kubernetes needs an init/ordering story in
   Phase 7 (initContainer or migration Job), not just a readiness probe.

Also confirmed by the gate: liveness stays `healthy` while the database is down
(correct separation — liveness must not depend on dependencies), and the Docker
healthcheck flips to `unhealthy` after the 10s × 5 retry window and recovers
automatically. The `libgssapi_krb5.so.2` notice is confirmed non-fatal log noise
on `aspnet:10.0`/Ubuntu 24.04.
