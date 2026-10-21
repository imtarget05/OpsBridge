#!/usr/bin/env python3
"""L6 oversell proof on local kind: stock 10, 15 barrier-fired attempts.

Derived from oversell_demo.py (same POST shape, same conservation law).
Kind differences: API via port-forward, stock via kubectl exec psql into the
in-cluster Postgres, completion proven by polling /api/orders/{key}.
Exit 0 = PASS (exactly 10 accepted, final stock 0).
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

KUBECTL = ["kubectl", "-n", "flashsale"]
PG_CMD = ["exec", "postgres-0", "--", "psql", "-U", "postgres",
          "-d", "FlashSaleDb", "-At", "-c"]


def psql(query: str) -> str:
    out = subprocess.run(KUBECTL + PG_CMD + [query], capture_output=True, text=True)
    if out.returncode != 0:
        raise RuntimeError(f"psql failed: {out.stderr.strip()}")
    return out.stdout.strip()


def http_post(url: str, payload: dict, timeout: float = 15.0):
    data = json.dumps(payload).encode()
    req = urllib.request.Request(url, data=data,
                                 headers={"Content-Type": "application/json"}, method="POST")
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode(), (time.perf_counter() - start) * 1000
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode(errors="replace"), (time.perf_counter() - start) * 1000
    except Exception as exc:  # noqa: BLE001 - record transport failures
        return None, str(exc), (time.perf_counter() - start) * 1000


def wait_completed(base_url: str, keys: list, deadline_s: int) -> dict:
    pending = set(keys)
    done_at = time.time() + deadline_s
    while pending and time.time() < done_at:
        for key in list(pending):
            try:
                with urllib.request.urlopen(f"{base_url}/api/orders/{key}", timeout=10) as resp:
                    if json.loads(resp.read().decode()).get("status") == "completed":
                        pending.discard(key)
            except Exception:  # noqa: BLE001 - keep polling other keys
                pass
        if pending:
            time.sleep(1)
    return {key: (key not in pending) for key in keys}


def main() -> int:
    ap = argparse.ArgumentParser(description="L6 oversell proof on local kind.")
    ap.add_argument("--base-url", default="http://127.0.0.1:15080")
    ap.add_argument("--product-id", type=int, default=7)
    ap.add_argument("--stock", type=int, default=10)
    ap.add_argument("--requests", type=int, default=15)
    ap.add_argument("--settle-seconds", type=int, default=120)
    ap.add_argument("--ops-token", default=os.environ.get("OPS_TOKEN", ""),
                    help="STAFF/ADMIN bearer token for POST /internal/resync-stock, which is "
                         "no longer anonymous. Required, not optional: without the resync, "
                         "Redis keeps the pre-test counter and the proof below measures the "
                         "wrong number.")
    args = ap.parse_args()
    if not args.ops_token:
        print("RESULT: ABORTED - --ops-token (or OPS_TOKEN) is required; /internal/resync-stock "
              "is STAFF/ADMIN only and the oversell proof depends on the resync.")
        return 2

    psql(f'UPDATE "Products" SET "AvailableStock" = {args.stock} WHERE "Id" = {args.product_id};')
    with urllib.request.urlopen(
        f"{args.base_url}/internal/resync-stock/{args.product_id}?dryRun=false",
        data=b"{}",
        headers={"Authorization": f"Bearer {args.ops_token}"},
        timeout=10,
    ) as response:
        resync = json.load(response)
    if resync.get("postgres") != args.stock or not (
        resync.get("fixed") is True
        or (resync.get("fixed") is False and resync.get("redis") == args.stock)
    ):
        print(f"RESULT: ABORTED - Redis resync not applied: {resync}")
        return 2
    orders_before = int(psql(f'SELECT COUNT(*) FROM "Orders" WHERE "ProductId" = {args.product_id};'))
    print(f"initial stock={args.stock} attempts={args.requests} product=#{args.product_id} "
          f"orders_before={orders_before}")

    results = [None] * args.requests
    barrier = threading.Barrier(args.requests + 1)

    def worker(i: int) -> None:
        barrier.wait()
        results[i] = http_post(f"{args.base_url}/api/orders",
                               {"productId": args.product_id, "quantity": 1})

    threads = [threading.Thread(target=worker, args=(i,), daemon=True) for i in range(args.requests)]
    for t in threads:
        t.start()
    barrier.wait()
    for t in threads:
        t.join(timeout=120)

    accepted, rejected, failed, counts, lat, keys = 0, 0, 0, {}, [], []
    for r in results:
        status, body, ms = r
        if status is not None:
            lat.append(ms)
        if status is not None and 200 <= status < 300:
            accepted += 1
            try:
                keys.append(json.loads(body)["idempotencyKey"])
            except Exception:  # noqa: BLE001 - key missing keeps audit honest
                pass
        elif status is not None and 400 <= status < 500:
            rejected += 1
            counts[str(status)] = counts.get(str(status), 0) + 1
        else:
            failed += 1
    lat.sort()
    p95 = lat[min(len(lat) - 1, int(0.95 * len(lat)))] if lat else 0.0
    print(f"accepted={accepted} rejected={rejected}{counts} failed={failed} p95={p95:.0f}ms")
    print(f"accepted_keys={sorted(keys)}")

    settled = wait_completed(args.base_url, keys, args.settle_seconds)
    unsettled = [k for k, ok in settled.items() if not ok]
    orders_created = int(psql(f'SELECT COUNT(*) FROM "Orders" WHERE "ProductId" = {args.product_id};')) - orders_before
    final_stock = int(psql(f'SELECT "AvailableStock" FROM "Products" WHERE "Id" = {args.product_id};'))
    expected = args.stock - accepted
    print(f"settled={len(keys) - len(unsettled)}/{len(keys)} orders_created={orders_created} "
          f"final_stock={final_stock} expected={expected}")
    if unsettled:
        print(f"RESULT: INCONCLUSIVE - {len(unsettled)} accepted orders did not complete in time.")
        return 2
    if accepted + rejected + failed != args.requests or failed:
        print("RESULT: FAIL - response accounting broken.")
        return 1
    if final_stock != expected:
        print(f"RESULT: FAIL - inventory conservation violated (final {final_stock} != expected {expected}).")
        return 1
    if accepted != args.stock or final_stock != 0:
        print("RESULT: FAIL - expected exactly stock accepted and final 0.")
        return 1
    print(f"RESULT: PASS - exactly {accepted} accepted, final stock 0, no oversell, no lost updates.")
    return 0


if __name__ == "__main__":
    import sys as _s
    _s.exit(main())
