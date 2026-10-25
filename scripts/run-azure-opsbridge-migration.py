#!/usr/bin/env python3
"""Run the pinned OpsBridge schema Job before the AKS API rollout."""

import pathlib
import subprocess
import sys

import yaml


ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE_SHA = "3e7fb0d30dd2a5caffd3a7b9c73ec24367e11bf0"
IMAGE = f"acrflashsalep6.azurecr.io/opsbridge-api:{SOURCE_SHA}"
CONTEXT = "aks-portfolio-dev"
NAMESPACE = "flashsale"


def run(*args: str, input_text: str | None = None) -> str:
    result = subprocess.run(
        list(args), input=input_text, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
    )
    if result.returncode:
        raise RuntimeError(f"command failed (exit {result.returncode}): {args[0]}")
    return result.stdout


def main() -> int:
    if len(sys.argv) != 2 or sys.argv[1] != "--apply":
        print("Usage: python3 scripts/run-azure-opsbridge-migration.py --apply")
        return 2
    rendered = run("kubectl", "kustomize", str(ROOT / "infrastructure/kubernetes/base/opsbridge-api"))
    jobs = [obj for obj in yaml.safe_load_all(rendered) if obj and obj["kind"] == "Job"]
    if len(jobs) != 1 or jobs[0]["metadata"]["name"] != "opsbridge-migrate":
        raise RuntimeError("unexpected migration Job render")
    job = jobs[0]
    job["metadata"]["namespace"] = NAMESPACE
    job["spec"]["template"]["spec"]["containers"][0]["image"] = IMAGE
    if job["spec"]["template"]["spec"]["containers"][0].get("args") != ["--migrate"]:
        raise RuntimeError("unexpected migration command")
    for name in ("opsbridge-migrate-secrets", "opsbridge-secrets"):
        run("kubectl", "--context", CONTEXT, "-n", NAMESPACE, "get", "secret", name, "-o", "name")
    run("kubectl", "--context", CONTEXT, "-n", NAMESPACE, "create", "-f", "-", input_text=yaml.safe_dump(job))
    run("kubectl", "--context", CONTEXT, "-n", NAMESPACE, "wait", "--for=condition=complete", "job/opsbridge-migrate", "--timeout=10m")
    print(f"Migration completed with image {IMAGE}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, KeyError, ValueError) as exc:
        print(f"Migration failed: {type(exc).__name__}", file=sys.stderr)
        raise SystemExit(1)
