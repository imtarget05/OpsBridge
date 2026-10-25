#!/usr/bin/env python3
"""
Phase 2 Experiment: Reproduce the Flash-Sale Overselling Race Condition.

Business problem
----------------
A flash sale has limited inventory. Many clients purchase simultaneously.
The naive API does: SELECT stock -> check -> decrement in memory -> save.
Between SELECT and UPDATE, other requests read the same stale value, so the
same unit of inventory is sold more than once (overselling).

This script is the *evidence generator*: it resets stock to a small known
value, fires N concurrent purchase requests, then audits the database.

Acceptance criteria for the current zero-oversell gate
------------------------------------------------------
All requests complete; exactly min(requests, stock // quantity) are accepted,
the rest receive 409; persisted orders equal accepted requests; and final
PostgreSQL stock equals initial stock minus accepted units. A conserved but
underfilled sale is a failure, not a PASS.

Run
---
  python3 load-tests/concurrency/oversell_demo.py --stock 10 --requests 50
Requires: python3 (stdlib only), the Order API running locally, and psql
access either via docker exec or a local psql binary.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field


@dataclass
class Result:
    status: int | None = None
    body: str = ""
    error: str | None = None
    latency_ms: float = 0.0


@dataclass
class Summary:
    total: int = 0
    accepted: int = 0
    rejected: int = 0
    failed: int = 0
    status_counts: dict = field(default_factory=dict)
    latencies_ms: list = field(default_factory=list)

    def p95(self) -> float:
        if not self.latencies_ms:
            return 0.0
        s = sorted(self.latencies_ms)
        return s[min(len(s) - 1, int(0.95 * len(s)))]

def http_post(url: str, payload: dict, timeout: float = 10.0) -> "Result":
    data = json.dumps(payload).encode()
    req = urllib.request.Request(
        url, data=data, headers={"Content-Type": "application/json"}, method="POST"
    )
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return Result(status=resp.status, body=resp.read().decode(),
                          latency_ms=(time.perf_counter() - start) * 1000)
    except urllib.error.HTTPError as exc:  # 4xx/5xx are expected outcomes here
        return Result(status=exc.code, body=exc.read().decode(errors="replace"),
                      latency_ms=(time.perf_counter() - start) * 1000)
    except Exception as exc:  # noqa: BLE001 - record transport failures
        return Result(error=str(exc), latency_ms=(time.perf_counter() - start) * 1000)


def fire_batch(base_url: str, product_id: int, quantity: int, n: int) -> list:
    results = [Result() for _ in range(n)]
    start_barrier = threading.Barrier(n + 1)  # +1 for the coordinator thread

    def worker(i: int) -> None:
        start_barrier.wait()  # everyone fires at the same instant
        results[i] = http_post(f"{base_url}/api/orders", {"productId": product_id, "quantity": quantity})

    threads = [threading.Thread(target=worker, args=(i,), daemon=True) for i in range(n)]
    for t in threads:
        t.start()
    start_barrier.wait()  # release the flood
    for t in threads:
        t.join(timeout=120)
    return results


def classify(results: list, summary: "Summary") -> None:
    for r in results:
        summary.total += 1
        if r.error is not None:
            summary.failed += 1
        elif r.status is not None and 200 <= r.status < 300:
            # 200 = placed (sync path), 202 = reserved + queued (async path).
            summary.accepted += 1
        elif r.status is not None and 400 <= r.status < 500:
            summary.rejected += 1
            key = str(r.status)
            summary.status_counts[key] = summary.status_counts.get(key, 0) + 1
        else:
            summary.failed += 1
        if r.latency_ms:
            summary.latencies_ms.append(r.latency_ms)


def psql_scalar(query: str, container: str | None, retries: int = 5, delay: float = 1.0) -> str:
    """Run a scalar query with retries — during a burst the server may briefly
    refuse new connections (max_connections), so audit reads must be patient."""
    cmd = (["docker", "exec", container, "psql", "-U", "postgres", "-d", "FlashSaleDb", "-At", "-c", query]
           if container else ["psql", "-h", "localhost", "-U", "postgres", "-d", "FlashSaleDb", "-At", "-c", query])
    env = dict(os.environ, PGPASSWORD="postgres")
    last_err: Exception | None = None
    for attempt in range(retries):
        out = subprocess.run(cmd, capture_output=True, text=True, env=env)
        if out.returncode == 0:
            return out.stdout.strip()
        last_err = RuntimeError(out.stderr.strip() or f"psql exit {out.returncode}")
        time.sleep(delay)
    raise last_err  # type: ignore[misc]


def reset_stock(container: str | None, product_id: int, stock: int) -> None:
    psql_scalar(f'UPDATE "Products" SET "AvailableStock" = {stock} WHERE "Id" = {product_id};', container)


def read_stock(container: str | None, product_id: int) -> int:
    return int(psql_scalar(f'SELECT "AvailableStock" FROM "Products" WHERE "Id" = {product_id};', container))


def count_orders(container: str | None, product_id: int) -> int:
    return int(psql_scalar(f'SELECT COUNT(*) FROM "Orders" WHERE "ProductId" = {product_id};', container))


def resync_applied(result: dict, stock: int) -> bool:
    """The endpoint reports fixed=false when Redis was already at stock."""
    return (result.get("postgres") == stock
            and (result.get("fixed") is True
                 or (result.get("fixed") is False and result.get("redis") == stock)))


def audit_failures(summary: Summary, requests: int, stock: int, quantity: int,
                   final_stock: int, orders_created: int) -> list[str]:
    expected_accepted = min(requests, stock // quantity)
    expected_rejected = requests - expected_accepted
    failures = []
    if summary.total != requests or summary.failed:
        failures.append(f"responses: {summary.total}/{requests}, failures: {summary.failed}")
    if summary.accepted != expected_accepted:
        failures.append(f"accepted: {summary.accepted}, expected: {expected_accepted}")
    # Hợp đồng rate limiting (ADR-025): 429 = rate limit vượt ngưỡng (tiêu chí hạ tầng),
    # 409 = xung đột nghiệp prática (hết hàng, trùng khóa idempotency). Cả hai đều có nghĩa
    # là "yêu cầu không được chấp nhận". Harness kiểm tra bảo tồn tồn kho, không phải kiểm tra
    # chính xác mã HTTP của từng phản ứng từ chối — do đó cả 409 và 429 đều tính là rejected.
    total_rejections = summary.status_counts.get("409", 0) + summary.status_counts.get("429", 0)
    if summary.rejected != expected_rejected or total_rejections != expected_rejected:
        failures.append(f"rejections (409+429): {total_rejections}, expected: {expected_rejected} (409={summary.status_counts.get('409', 0)}, 429={summary.status_counts.get('429', 0)})")
    expected_stock = stock - summary.accepted * quantity
    if final_stock != expected_stock or final_stock < 0:
        failures.append(f"final stock: {final_stock}, expected from accepted: {expected_stock}")
    if orders_created != summary.accepted:
        failures.append(f"persisted orders: {orders_created}, accepted: {summary.accepted}")
    return failures


def main() -> int:
    ap = argparse.ArgumentParser(description="Reproduce the flash-sale overselling race condition.")
    ap.add_argument("--base-url", default="http://localhost:5065")
    ap.add_argument("--product-id", type=int, default=1)
    ap.add_argument("--quantity", type=int, default=1)
    ap.add_argument("--stock", type=int, default=10, help="Initial stock before the experiment.")
    ap.add_argument("--requests", type=int, default=50, help="Concurrent requests fired at once.")
    ap.add_argument("--psql-container", default="flash-sale-platform-postgres-1",
                    help="Docker container name running psql (empty string to use local psql).")
    ap.add_argument("--settle-seconds", type=int, default=2,
                    help="Seconds to wait before the DB audit so async fulfillment settles (Phase 5+).")
    ap.add_argument("--ops-token", default=os.environ.get("OPS_TOKEN", ""),
                    help="STAFF/ADMIN bearer token for POST /internal/resync-stock. That endpoint "
                         "is no longer anonymous, and without this resync Redis keeps the "
                         "pre-test counter, so the audit below would measure the wrong "
                         "accepted count while still printing PASS.")
    args = ap.parse_args()
    if args.stock < 0 or args.requests < 1 or args.quantity < 1:
        ap.error("stock must be nonnegative; requests and quantity must be positive")
    container = args.psql_container or None

    print(f"Target              : {args.base_url}")
    print(f"Product             : #{args.product_id}")
    print(f"Initial stock       : {args.stock}")
    print(f"Concurrent requests : {args.requests}")
    print("-" * 60)

    reset_stock(container, args.product_id, args.stock)

    # Redis is the fast-path stock mirror (Phase 5+): keep it consistent with the
    # DB we just reset, via the operational runbook endpoint (ADR-003).
    resync_headers = {"Authorization": f"Bearer {args.ops_token}"} if args.ops_token else {}
    if not args.ops_token:
        print("RESULT: ABORTED - --ops-token (or OPS_TOKEN) is required to reset Redis.")
        return 2
    try:
        resync = urllib.request.Request(
            f"{args.base_url}/internal/resync-stock/{args.product_id}?dryRun=false",
            headers=resync_headers, method="POST")
        with urllib.request.urlopen(resync, timeout=5) as response:
            resync_result = json.load(response)
        if not resync_applied(resync_result, args.stock):
            raise ValueError(f"resync did not apply stock {args.stock}: {resync_result}")
    except Exception as exc:  # noqa: BLE001 - abort without a verdict
        print(f"RESULT: ABORTED - Redis resync failed ({exc}); no concurrency verdict.")
        return 2

    orders_before = count_orders(container, args.product_id)
    print(f"Stock reset to {args.stock}. Orders before: {orders_before}. Firing...")

    results = fire_batch(args.base_url, args.product_id, args.quantity, args.requests)
    summary = Summary()
    classify(results, summary)

    # Phase 5+: fulfillment is asynchronous (queue + worker). Give the worker a
    # moment so the audit measures the settled end state, not the in-flight one.
    time.sleep(args.settle_seconds)

    final_stock = read_stock(container, args.product_id)
    orders_created = count_orders(container, args.product_id) - orders_before
    expected_stock = args.stock - (summary.accepted * args.quantity)

    print("-" * 60)
    print("HTTP outcomes        :")
    print(f"  200 accepted       : {summary.accepted}")
    print(f"  4xx rejected       : {summary.rejected} {summary.status_counts or ''}")
    print(f"  transport failures : {summary.failed}")
    print(f"  p95 latency        : {summary.p95():.0f} ms")
    print("Inventory audit      :")
    print(f"  initial stock      : {args.stock}")
    print(f"  accepted units     : {summary.accepted * args.quantity}")
    print(f"  expected stock     : {expected_stock}")
    print(f"  final stock in DB  : {final_stock}")
    print(f"  orders created     : {orders_created}")
    print("-" * 60)

    failures = audit_failures(summary, args.requests, args.stock, args.quantity,
                              final_stock, orders_created)
    if failures:
        print("RESULT: FAIL - " + "; ".join(failures))
        return 1
    print("RESULT: PASS - exact fill, 409 rejects, persisted orders and stock verified.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

