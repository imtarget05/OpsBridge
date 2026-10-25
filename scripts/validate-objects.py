#!/usr/bin/env python3
"""Cross-reference a rendered manifest set with no cluster and no kubeconfig.

Why not `kubectl apply --dry-run=client` or `kubectl create --dry-run=client
--validate=false`: both still perform API discovery (GET /api, /openapi/v2) so
the client can map Kind -> GVR. Against a stopped AKS cluster that is a DNS
failure; when it appears to succeed it is only because kubectl cached a
discovery document from an earlier run. A gate whose result depends on whether a
cache expired is not a gate -- that exact false-PASS then false-FAIL flip was
observed while writing this.

Image pinning, placeholder detection and exposure rules live in
validate-manifests.sh. Pod securityContext rules are already enforced by Trivy
(KSV-0012/0014/0118) in the security-scan job, so they are not duplicated here.
"""
import sys

try:
    import yaml
except ImportError:  # a silently skipped gate is the bug class found in 7A
    sys.exit("PyYAML is required: pip install pyyaml")

WORKLOADS = ("Deployment", "StatefulSet", "DaemonSet", "Job")

# Kinds whose .spec.selector the manifest author owns. A Job is deliberately NOT
# one of them: the Job controller injects `controller-uid`/`job-name` into the pod
# template, and a hand-written selector that omits controller-uid makes a re-run of
# the same Job adopt the pods of the PREVIOUS run. Demanding the field here would
# push manifests toward that bug, so for Job it is only checked when present.
SELECTOR_OWNED_KINDS = ("Deployment", "StatefulSet", "DaemonSet")

# infrastructure/kubernetes/README-secrets.md is the source of this contract.
# Encoding it here turns a mistyped key into a build failure instead of a pod
# stuck in CreateContainerConfigError on a live cluster.
SECRET_CONTRACT = {
    "flashsale-secrets": {
        "pg-connection", "pg-password", "redis-connection",
        "rabbitmq-connection", "rabbitmq-password", "jwt-signing-key",
        "agent-recovery-hmac-key",
    },
    "opsbridge-secrets": {"pg-connection", "rabbitmq-connection", "jwt-signing-key"},
    "opsbridge-migrate-secrets": {"pg-migration-connection"},
}


def container_problems(where, c, declared_volumes, errs):
    cname = c.get("name")
    if not c.get("image"):
        errs.append(f"{where}/{cname}: no image")

    res = c.get("resources", {})
    for section in ("requests", "limits"):
        for key in ("cpu", "memory"):
            if key not in res.get(section, {}):
                errs.append(f"{where}/{cname}: resources.{section}.{key} required")

    for m in c.get("volumeMounts", []):
        if m["name"] not in declared_volumes:
            errs.append(f"{where}/{cname}: mounts undeclared volume {m['name']!r} "
                        f"(declared: {sorted(declared_volumes) or 'none'})")

    for e in c.get("env", []):
        ref = (e.get("valueFrom") or {}).get("secretKeyRef")
        if not ref:
            continue
        secret, key = ref.get("name"), ref.get("key")
        if secret not in SECRET_CONTRACT:
            errs.append(f"{where}/{cname}: unknown Secret {secret!r}")
        elif key not in SECRET_CONTRACT[secret]:
            errs.append(f"{where}/{cname}: Secret {secret!r} has no key {key!r} "
                        f"(contract: {sorted(SECRET_CONTRACT[secret])})")


def main(path):
    docs = [d for d in yaml.safe_load_all(open(path).read()) if d]
    if not docs:
        print("FAIL: no documents parsed", file=sys.stderr)
        return 1

    errs = []
    seen = set()
    identities = {(d.get("kind"), (d.get("metadata") or {}).get("name")) for d in docs}

    for d in docs:
        kind = d.get("kind")
        meta = d.get("metadata") or {}
        name = meta.get("name")
        where = f"{kind}/{name}"

        for req in ("apiVersion", "kind"):
            if not d.get(req):
                errs.append(f"{where}: missing {req}")
        if not name:
            errs.append(f"{kind}: metadata.name is missing")
        if not meta.get("namespace"):
            errs.append(f"{where}: no namespace -- the overlay must set one")
        if (kind, name) in seen:
            errs.append(f"{where}: declared more than once")
        seen.add((kind, name))

        spec = d.get("spec") or {}
        if kind in WORKLOADS:
            pod = (spec.get("template") or {}).get("spec") or {}
            declared = {v["name"] for v in pod.get("volumes", [])}
            declared |= {v["metadata"]["name"]
                         for v in spec.get("volumeClaimTemplates", [])}
            for c in pod.get("containers", []):
                container_problems(where, c, declared, errs)

            sel = (spec.get("selector") or {}).get("matchLabels") or {}
            labels = ((spec.get("template") or {}).get("metadata") or {}).get("labels") or {}
            if not sel:
                # Only an authoring error for kinds that own their selector; see
                # SELECTOR_OWNED_KINDS.
                if kind in SELECTOR_OWNED_KINDS:
                    errs.append(f"{where}: selector.matchLabels is empty")
            elif not set(sel.items()) <= set(labels.items()):
                errs.append(f"{where}: selector {sel} does not match pod labels "
                            f"{labels} -- the ReplicaSet adopts nothing or wrong pods")

            if kind == "StatefulSet":
                sn = spec.get("serviceName")
                if not sn:
                    errs.append(f"{where}: StatefulSet has no serviceName")
                elif ("Service", sn) not in identities:
                    errs.append(f"{where}: serviceName {sn!r} does not exist")

        elif kind == "Service":
            if not spec.get("selector"):
                errs.append(f"{where}: Service has no selector")
            if not spec.get("ports"):
                errs.append(f"{where}: Service declares no ports")
            if spec.get("type") == "LoadBalancer":
                errs.append(f"{where}: type LoadBalancer provisions a public IP")

    print(f"cross-referenced {len(docs)} objects: "
          + ", ".join(sorted(f"{k}/{n}" for k, n in identities)))
    for e in errs:
        print(f"FAIL: {e}", file=sys.stderr)
    return 1 if errs else 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: validate-objects.py <rendered.yaml>")
    sys.exit(main(sys.argv[1]))
