#!/usr/bin/env python3
"""Apply only the OpsBridge API and its route after the schema gate."""

import json
import pathlib
import subprocess
import sys

import yaml


ROOT = pathlib.Path(__file__).resolve().parents[1]
CONTEXT = "aks-portfolio-dev"
NAMESPACE = "flashsale"
SOURCE_SHA = "d8a9220a88556de305a67e6eb5ff1f80f7da79bb"
IMAGE = f"acrflashsalep6.azurecr.io/opsbridge-api:{SOURCE_SHA}"


def kubectl(*args: str, input_text: str | None = None) -> str:
    result = subprocess.run(
        ["kubectl", "--context", CONTEXT, *args],
        input=input_text, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
    )
    if result.returncode:
        raise RuntimeError(f"kubectl command failed (exit {result.returncode})")
    return result.stdout


def main() -> int:
    if len(sys.argv) != 2 or sys.argv[1] != "--apply":
        print("Usage: python3 scripts/deploy-azure-opsbridge-api.py --apply")
        return 2
    job = json.loads(kubectl("-n", NAMESPACE, "get", "job", "opsbridge-migrate", "-o", "json"))
    if job.get("status", {}).get("succeeded", 0) < 1:
        raise RuntimeError("migration has not completed")
    if job["spec"]["template"]["spec"]["containers"][0]["image"] != IMAGE:
        raise RuntimeError("migration image differs from API image")

    rendered = kubectl("kustomize", str(ROOT / "infrastructure/kubernetes/overlays/azure-cloud"))
    objects = {(obj["kind"], obj["metadata"]["name"]): obj for obj in yaml.safe_load_all(rendered) if obj}
    deployment = objects[("Deployment", "opsbridge-api")]
    if deployment["spec"]["template"]["spec"]["containers"][0]["image"] != IMAGE:
        raise RuntimeError("API image is not pinned to the migrated schema")
    route = objects[("HTTPRoute", "flashsale")]
    if not any(
        match["path"]["value"] == "/opsbridge/api" and rule["backendRefs"][0]["name"] == "opsbridge-api"
        for rule in route["spec"]["rules"]
        for match in rule.get("matches", [])
    ):
        raise RuntimeError("OpsBridge API route is missing")

    for key in (("Service", "opsbridge-api"), ("Deployment", "opsbridge-api")):
        kubectl("-n", NAMESPACE, "apply", "-f", "-", input_text=yaml.safe_dump(objects[key]))
    kubectl("-n", NAMESPACE, "rollout", "status", "deployment/opsbridge-api", "--timeout=5m")
    kubectl("-n", NAMESPACE, "apply", "-f", "-", input_text=yaml.safe_dump(route))
    print("OpsBridge API rolled out after migration; Azure HTTPRoute applied.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, KeyError, ValueError) as exc:
        print(f"Deployment failed: {type(exc).__name__}", file=sys.stderr)
        raise SystemExit(1)
