#!/usr/bin/env python3
"""Mutation-test scripts/validate-manifests.sh: every assertion must be able to fail.

An assertion that cannot fail manufactures confidence, which is exactly the
Phase 7C bug class this gate was written to close (CI proved the image while the
rendered manifest pointed at a registry that did not exist). So the validator is
tested the way code is: each rule gets a broken manifest, and the test asserts
the rule catches IT - specifically, that the run exits non-zero and names the
broken thing.

This needs no cluster: it renders the prod overlay once with `kubectl kustomize`
(no API server, no kubeconfig), hands each mutant to the validator in
pre-rendered mode, and checks the validator's own verdict. Mutations are
text-level so the validator sees realistic formatting, not a PyYAML round-trip
that normalizes away the exact forms being asserted on.
"""
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OVERLAY = os.path.join(ROOT, "infrastructure", "kubernetes", "overlays", "prod")
# The OpsBridge API artefact set is self-contained (it is not in the prod
# overlay, because its image does not exist in a registry yet), so it is
# validated from its OWN render — see scripts/validate-manifests.sh section 9.
OPSBRIDGE_DIR = os.path.join(ROOT, "infrastructure", "kubernetes", "base", "opsbridge-api")
VALIDATOR = os.path.join(ROOT, "scripts", "validate-manifests.sh")

MUTATIONS = []


def split_docs(text):
    parts, cur = [], []
    for line in text.splitlines(keepends=True):
        if line.rstrip("\n") == "---":
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(line)
    parts.append("".join(cur))
    return parts


def join_docs(docs):
    out = []
    for d in docs:
        if out:
            out.append("---\n")
        out.append(d if d.endswith("\n") else d + "\n")
    return "".join(out)


def find_doc(docs, kind, name):
    for i, d in enumerate(docs):
        if re.search(rf"(?m)^kind: {kind}$", d) and re.search(rf"(?m)^  name: {name}$", d):
            return i
    raise KeyError(f"{kind}/{name} not present in rendered input")


def insert_after_image(doc, image_needle, lines):
    # Locate the IMAGE LINE directly rather than the container by name: rendered
    # containers are key-sorted (`env:` precedes `name:`), so a name-based search
    # lands on the wrong anchor. Each app doc has exactly one image line, and its
    # indent is the indent the inserted block needs.
    m = re.search(rf"(?m)^(\s+)image: .*{re.escape(image_needle)}.*$", doc)
    if not m:
        raise KeyError(f"no image line matching {image_needle!r}")
    indent = m.group(1)
    add = "".join(f"{indent}{line}\n" for line in lines)
    return doc[: m.end()] + "\n" + add.rstrip("\n") + doc[m.end():]


def rename_env(docs, kind, name, env_name):
    """Rename (not delete) an env var: the YAML stays parseable, so the thing
    under test is the validator noticing an ABSENT variable, not its ability to
    survive malformed YAML."""
    i = find_doc(docs, kind, name)
    needle = f"name: {env_name}\n"
    if needle not in docs[i]:
        raise KeyError(f"{kind}/{name} has no env {env_name}")
    docs[i] = docs[i].replace(needle, f"name: {env_name}X\n", 1)
    return docs


def mut_api_missing_rabbitmq_env(docs):
    return rename_env(docs, "Deployment", "order-api", "ConnectionStrings__RabbitMQ")


def mut_api_missing_provider(docs):
    return rename_env(docs, "Deployment", "order-api", "Messaging__Provider")


def mut_worker_missing_rabbitmq_env(docs):
    return rename_env(docs, "Deployment", "order-worker", "ConnectionStrings__RabbitMQ")


def mut_worker_missing_provider(docs):
    return rename_env(docs, "Deployment", "order-worker", "Messaging__Provider")


def mut_worker_adds_port(docs):
    i = find_doc(docs, "Deployment", "order-worker")
    docs[i] = insert_after_image(docs[i], "order-worker", ["ports:", "- containerPort: 8081"])
    return docs


def mut_worker_no_cpu_limit(docs):
    i = find_doc(docs, "Deployment", "order-worker")
    new = re.sub(r"(?m)^(\s+)limits:\n\s+cpu: .*(?:\n|$)", r"\1limits:\n", docs[i], count=1)
    if new == docs[i]:
        raise KeyError("worker limits.cpu block did not match; adjust the pattern")
    docs[i] = new
    return docs


def mut_api_latest_tag(docs):
    i = find_doc(docs, "Deployment", "order-api")
    new = re.sub(r"image: (\S*order-api):[0-9a-f]{40}", r"image: \1:latest", docs[i], count=1)
    if new == docs[i]:
        raise KeyError("order-api image line did not match the pinned-SHA form")
    docs[i] = new
    return docs


def mut_api_placeholder_registry(docs):
    i = find_doc(docs, "Deployment", "order-api")
    if "acrflashsalep6.azurecr.io" not in docs[i]:
        raise KeyError("order-api doc has no ACR registry to corrupt")
    docs[i] = docs[i].replace("acrflashsalep6.azurecr.io", "acrflashsale-placeholder.azurecr.io", 1)
    return docs


def mut_api_wrong_registry(docs):
    i = find_doc(docs, "Deployment", "order-api")
    docs[i] = docs[i].replace("acrflashsalep6.azurecr.io", "otherregistry.azurecr.io", 1)
    return docs


def mut_redis_loadbalancer(docs):
    i = find_doc(docs, "Service", "redis")
    # Rendered Services carry no `type:` line (ClusterIP is the default), so the
    # mutation ADDS one. It must be caught wherever it is written.
    docs[i] = re.sub(r"(?m)^spec:$", "spec:\n  type: LoadBalancer", docs[i], count=1)
    return docs


def mut_rabbitmq_mgmt_port(docs):
    i = find_doc(docs, "StatefulSet", "rabbitmq")
    docs[i] = insert_after_image(docs[i], "rabbitmq", ["ports:", "- containerPort: 15672"])
    return docs


def drop_doc(kind, name):
    def _mut(docs):
        i = find_doc(docs, kind, name)
        del docs[i]
        return docs
    return _mut


def mut_api_bad_secret_key(docs):
    i = find_doc(docs, "Deployment", "order-api")
    if "key: rabbitmq-connection" not in docs[i]:
        raise KeyError("order-api doc has no rabbitmq-connection key")
    docs[i] = docs[i].replace("key: rabbitmq-connection", "key: rabbitmq-connectoin", 1)
    return docs


def mut_api_missing_namespace(docs):
    i = find_doc(docs, "Deployment", "order-api")
    docs[i] = re.sub(r"(?m)^  namespace: .*\n", "", docs[i], count=1)
    return docs


def mut_api_undeclared_volume(docs):
    i = find_doc(docs, "Deployment", "order-api")
    # Drop only the volume DECLARATION, keep the mount: the validator must catch
    # a mount pointing at a volume the pod never declares. NB the rendered form
    # is key-sorted: `- emptyDir: {}` precedes `name: tmp`.
    new = re.sub(r"(?m)^      - emptyDir: \{\}\n        name: tmp\n", "", docs[i], count=1)
    if new == docs[i]:
        raise KeyError("rendered tmp volume block did not match; adjust the pattern")
    docs[i] = new
    return docs




def mut_job_needs_redis(docs):
    i = find_doc(docs, "Job", "order-migrate")
    anchor = "key: pg-connection\n"
    if anchor not in docs[i]:
        raise KeyError("Job doc has no pg-connection anchor")
    block = (
        "        - name: ConnectionStrings__Redis\n"
        "          valueFrom:\n"
        "            secretKeyRef:\n"
        "              name: flashsale-secrets\n"
        "              key: redis-connection\n"
    )
    docs[i] = docs[i].replace(anchor, anchor + block, 1)
    return docs


def mut_job_without_migrate_arg(docs):
    i = find_doc(docs, "Job", "order-migrate")
    new = re.sub(r"(?m)^\s+- --migrate\n", "", docs[i], count=1)
    if new == docs[i]:
        raise KeyError("Job doc has no `--migrate` arg to remove")
    docs[i] = new
    return docs


def mut_duplicate_api_service(docs):
    i = find_doc(docs, "Service", "order-api")
    docs.append(docs[i])
    return docs


def mut_api_missing_jwt_key(docs):
    """ADR-013 §2: a typo in the signing-key ref must fail the gate, not the pod.

    A wrong key name puts order-api in CreateContainerConfigError on a live
    cluster — the exact failure the secret contract exists to catch offline.
    """
    i = find_doc(docs, "Deployment", "order-api")
    if "key: jwt-signing-key" not in docs[i]:
        raise KeyError("order-api doc has no jwt-signing-key")
    docs[i] = docs[i].replace("key: jwt-signing-key", "key: jwt-signing-kev", 1)
    return docs


def mut_job_needs_jwt_key(docs):
    """The migration Job must never be handed auth config.

    JwtOptions fails fast when the key is absent, so a Job that is asked for one
    is a Job whose migration path would end up validating JWT configuration —
    which is what the --migrate branch ordering exists to prevent.
    """
    i = find_doc(docs, "Job", "order-migrate")
    anchor = "key: pg-connection\n"
    if anchor not in docs[i]:
        raise KeyError("Job doc has no pg-connection anchor")
    block = (
        "        - name: Auth__Jwt__SigningKey\n"
        "          valueFrom:\n"
        "            secretKeyRef:\n"
        "              name: flashsale-secrets\n"
        "              key: jwt-signing-key\n"
    )
    docs[i] = docs[i].replace(anchor, anchor + block, 1)
    return docs


# ----------------------------------------------------------------------
# Negative controls for the OpsBridge artefact set (validate-manifests.sh
# section 9). They are the same mutations the prod overlay is subjected to,
# applied to the OpsBridge render: a second schema-migration Job that is not
# held to the same rules as order-migrate is a Job whose ordering contract
# nobody has checked.
# ----------------------------------------------------------------------

def mut_ob_job_without_migrate_arg(docs):
    """The OpsBridge Job must run `--migrate`, not start a second API."""
    i = find_doc(docs, "Job", "opsbridge-migrate")
    new = re.sub(r"(?m)^\s+- --migrate\n", "", docs[i], count=1)
    if new == docs[i]:
        raise KeyError("Job doc has no `--migrate` arg to remove")
    docs[i] = new
    return docs


def mut_ob_job_needs_redis(docs):
    """The OpsBridge Job must depend on PostgreSQL ONLY."""
    i = find_doc(docs, "Job", "opsbridge-migrate")
    anchor = "key: pg-migration-connection\n"
    if anchor not in docs[i]:
        raise KeyError("Job doc has no pg-migration-connection anchor")
    block = (
        "        - name: ConnectionStrings__Redis\n"
        "          valueFrom:\n"
        "            secretKeyRef:\n"
        "              name: opsbridge-secrets\n"
        "              key: redis-connection\n"
    )
    docs[i] = docs[i].replace(anchor, anchor + block, 1)
    return docs


def mut_ob_job_uses_runtime_credential(docs):
    """ADR-018: the DDL step has its OWN identity.

    Pointing the migration Job at the runtime's DML credential destroys the
    separation the ADR is about — either the migration fails for lack of DDL
    rights, or (worse) the runtime role has to be given them.
    """
    i = find_doc(docs, "Job", "opsbridge-migrate")
    if "key: pg-migration-connection" not in docs[i]:
        raise KeyError("Job doc has no pg-migration-connection key")
    docs[i] = docs[i].replace("key: pg-migration-connection", "key: pg-connection", 1)
    return docs


def mut_ob_deploy_uses_ddl_credential(docs):
    """The runtime identity must hold DML rights only."""
    i = find_doc(docs, "Deployment", "opsbridge-api")
    if "name: opsbridge-secrets" not in docs[i]:
        raise KeyError("Deployment doc has no opsbridge-secrets reference")
    docs[i] = docs[i].replace("name: opsbridge-secrets", "name: opsbridge-migrate-secrets", 1)
    return docs


def mut_ob_deploy_holds_ddl_key(docs):
    """The DDL capability must be unreachable from the runtime workload.

    The Secret's NAME is only a label. A runtime pod handed
    `pg-migration-connection` under the runtime Secret's name holds the DDL
    capability, whatever the Secret is called — and the name-based rule
    (mut_ob_deploy_uses_ddl_credential above) cannot see it. This mutation
    is that exact hole: the DDL key under the RUNTIME secret's name, so the
    name-based rule stays silent and only the key-based rule can fire.
    """
    i = find_doc(docs, "Deployment", "opsbridge-api")
    anchor = "              key: pg-connection\n              name: opsbridge-secrets\n"
    if anchor not in docs[i]:
        raise KeyError("Deployment doc has no pg-connection/opsbridge-secrets anchor")
    block = (
        "        - name: ConnectionStrings__OpsBridgeDbDdl\n"
        "          valueFrom:\n"
        "            secretKeyRef:\n"
        "              key: pg-migration-connection\n"
        "              name: opsbridge-secrets\n"
    )
    docs[i] = docs[i].replace(anchor, anchor + block, 1)
    return docs


def mut_ob_job_holds_runtime_key(docs):
    """The reverse direction: the DDL step must not hold the runtime DML key.

    Mirror image of mut_ob_deploy_holds_ddl_key, and deliberately built so
    the NAME-based rules stay silent: the extra reference carries the DDL
    Secret's name, so only the key-level rule ("the Job must not use
    `pg-connection`") can catch it. That isolates the reverse direction of
    ADR-018's identity separation instead of leaving it implied.
    """
    i = find_doc(docs, "Job", "opsbridge-migrate")
    anchor = "              key: pg-migration-connection\n              name: opsbridge-migrate-secrets\n"
    if anchor not in docs[i]:
        raise KeyError("Job doc has no pg-migration-connection/opsbridge-migrate-secrets anchor")
    block = (
        "        - name: ConnectionStrings__OpsBridgeDbDml\n"
        "          valueFrom:\n"
        "            secretKeyRef:\n"
        "              key: pg-connection\n"
        "              name: opsbridge-migrate-secrets\n"
    )
    docs[i] = docs[i].replace(anchor, anchor + block, 1)
    return docs


def mut_ob_job_image_differs(docs):
    """The Job must be the exact artefact the Deployment rolls."""
    i = find_doc(docs, "Job", "opsbridge-migrate")
    if "opsbridge-api:placeholder-set-by-overlay" not in docs[i]:
        raise KeyError("Job doc has no base-convention image")
    docs[i] = docs[i].replace("opsbridge-api:placeholder-set-by-overlay",
                              "opsbridge-api:some-other-build", 1)
    return docs


MUTATIONS = [
    ("api_missing_rabbitmq_env", "order-api is missing ConnectionStrings__RabbitMQ", mut_api_missing_rabbitmq_env),
    ("api_missing_messaging_provider", "order-api does not set Messaging__Provider explicitly", mut_api_missing_provider),
    ("worker_missing_rabbitmq_env", "order-worker is missing ConnectionStrings__RabbitMQ", mut_worker_missing_rabbitmq_env),
    ("worker_missing_messaging_provider", "order-worker does not set Messaging__Provider explicitly", mut_worker_missing_provider),
    ("worker_adds_container_port", "order-worker declares a containerPort", mut_worker_adds_port),
    ("api_image_latest_tag", "pins an image to :latest", mut_api_latest_tag),
    ("api_placeholder_registry", "placeholder", mut_api_placeholder_registry),
    ("api_wrong_registry", "is not pinned to", mut_api_wrong_registry),
    ("redis_loadbalancer", "exposed as LoadBalancer", mut_redis_loadbalancer),
    ("rabbitmq_mgmt_port_exposed", "management port is exposed", mut_rabbitmq_mgmt_port),
    ("worker_deployment_missing", "rendered manifest has no Deployment/order-worker", drop_doc("Deployment", "order-worker")),
    ("migration_job_missing", "rendered manifest has no Job/order-migrate", drop_doc("Job", "order-migrate")),
    ("api_bad_secret_key", "has no key", mut_api_bad_secret_key),
    ("api_missing_namespace", "no namespace", mut_api_missing_namespace),
    ("api_undeclared_volume", "mounts undeclared volume", mut_api_undeclared_volume),
    ("worker_no_cpu_limit", "resources.limits.cpu required", mut_worker_no_cpu_limit),
    ("job_needs_redis", "depends on redis-connection", mut_job_needs_redis),
    ("job_needs_jwt_key", "depends on jwt-signing-key", mut_job_needs_jwt_key),
    ("api_missing_jwt_key", "order-api does not reference secret key jwt-signing-key", mut_api_missing_jwt_key),
    ("job_without_migrate_arg", "does not pass --migrate", mut_job_without_migrate_arg),
    ("duplicate_api_service", "declared more than once", mut_duplicate_api_service),
]

# The OpsBridge artefact set's own mutations. Same shape, different target:
# the validator must catch each of them in the OPSBRIDGE render.
OPSBRIDGE_MUTATIONS = [
    ("ob_job_without_migrate_arg", "does not pass --migrate", mut_ob_job_without_migrate_arg),
    ("ob_job_needs_redis", "depends on redis-connection", mut_ob_job_needs_redis),
    ("ob_job_uses_runtime_credential", "depends on pg-connection", mut_ob_job_uses_runtime_credential),
    ("ob_deploy_uses_ddl_credential", "references the DDL Secret", mut_ob_deploy_uses_ddl_credential),
    ("ob_deploy_holds_ddl_key", "references the DDL-capable secret key pg-migration-connection", mut_ob_deploy_holds_ddl_key),
    ("ob_job_holds_runtime_key", "references the runtime DML secret key pg-connection", mut_ob_job_holds_runtime_key),
    ("ob_job_image_differs", "must be the same artefact", mut_ob_job_image_differs),
]

# Negative controls on the OPERAND, not on the manifest: the OpsBridge render
# the validator is handed.
#
# An empty (or unparseable) render used to make the whole OpsBridge section
# vacuous while the script still printed "== RESULT: PASS ==" and exited 0 —
# the final message claimed a coverage that never happened. A gate that
# cannot tell "checked and passed" from "never ran" is a gate that
# manufactures confidence, so an unusable render has to be a FAILURE.
#
# Each case names the reason it expects, so a validator that failed for an
# unrelated reason would not pass this list.
INPUT_NEGATIVE_CONTROLS = [
    ("empty_opsbridge_render", "", "the file is empty (0 bytes)"),
    ("unparseable_opsbridge_render", "not: [valid yaml\n", "it is not parseable YAML"),
    ("non_kubernetes_opsbridge_render", "foo: bar\n", "it contains a document with no kind"),
    ("kind_without_apiversion_opsbridge_render",
     "kind: Service\nmetadata:\n  name: opsbridge-api\n",
     "no apiVersion"),
    ("list_document_opsbridge_render", "- a\n- b\n", "non-mapping document"),
]


def run_validator(path, opsbridge_path=None):
    cmd = ["bash", VALIDATOR, path]
    if opsbridge_path is not None:
        cmd.append(opsbridge_path)
    return subprocess.run(cmd, capture_output=True, text=True, timeout=120)


def main():
    render = subprocess.run(["kubectl", "kustomize", OVERLAY],
                            capture_output=True, text=True, timeout=120)
    if render.returncode != 0:
        sys.exit(f"kustomize render failed:\n{render.stderr}")
    clean = render.stdout

    ob_render = subprocess.run(["kubectl", "kustomize", OPSBRIDGE_DIR],
                               capture_output=True, text=True, timeout=120)
    if ob_render.returncode != 0:
        sys.exit(f"kustomize render of the OpsBridge artefact set failed:\n{ob_render.stderr}")
    clean_ob = ob_render.stdout

    # Sanity: the CLEAN manifest must PASS. If the validator rejects the honest
    # render, every mutation result below is meaningless.
    with tempfile.TemporaryDirectory() as tmp:
        clean_path = os.path.join(tmp, "clean.yaml")
        with open(clean_path, "w") as fh:
            fh.write(clean)
        clean_ob_path = os.path.join(tmp, "clean-opsbridge.yaml")
        with open(clean_ob_path, "w") as fh:
            fh.write(clean_ob)
        clean_run = run_validator(clean_path, clean_ob_path)
        if clean_run.returncode != 0:
            sys.exit("validator FAILS the clean render - fix that first:\n"
                     + (clean_run.stdout + clean_run.stderr)[-2000:])

        failures = []
        for name, expected, mutate in MUTATIONS:
            docs = split_docs(clean)
            try:
                docs = mutate(docs)
            except KeyError as exc:
                failures.append(f"{name}: mutation itself failed: {exc}")
                continue
            path = os.path.join(tmp, f"{name}.yaml")
            with open(path, "w") as fh:
                fh.write(join_docs(docs))

            proc = run_validator(path, clean_ob_path)
            combined = proc.stdout + proc.stderr
            if proc.returncode == 0:
                failures.append(f"{name}: validator PASSED a broken manifest (expected FAIL)")
            elif expected not in combined:
                failures.append(
                    f"{name}: validator failed but without the expected message\n"
                    f"    expected substring: {expected!r}\n"
                    f"    output: {combined.strip()[:400]}")
            else:
                print(f"  caught: {name}")

        # The OpsBridge set: the prod render stays clean, so every failure
        # below can only come from the OpsBridge mutation.
        for name, expected, mutate in OPSBRIDGE_MUTATIONS:
            docs = split_docs(clean_ob)
            try:
                docs = mutate(docs)
            except KeyError as exc:
                failures.append(f"{name}: mutation itself failed: {exc}")
                continue
            path = os.path.join(tmp, f"{name}.yaml")
            with open(path, "w") as fh:
                fh.write(join_docs(docs))

            proc = run_validator(clean_path, path)
            combined = proc.stdout + proc.stderr
            if proc.returncode == 0:
                failures.append(f"{name}: validator PASSED a broken manifest (expected FAIL)")
            elif expected not in combined:
                failures.append(
                    f"{name}: validator failed but without the expected message\n"
                    f"    expected substring: {expected!r}\n"
                    f"    output: {combined.strip()[:400]}")
            else:
                print(f"  caught: {name}")

        # The OPERAND controls: the OpsBridge render the validator is handed.
        # The prod render stays clean, so any failure below can only come from
        # the unusable OpsBridge render.
        for name, content, expected in INPUT_NEGATIVE_CONTROLS:
            path = os.path.join(tmp, f"{name}.yaml")
            with open(path, "w") as fh:
                fh.write(content)

            proc = run_validator(clean_path, path)
            combined = proc.stdout + proc.stderr
            if proc.returncode == 0:
                failures.append(
                    f"{name}: validator PASSED an unusable OpsBridge render (expected FAIL)")
            elif "is not usable" not in combined:
                failures.append(
                    f"{name}: validator failed but did not say the render was unusable\n"
                    f"    output: {combined.strip()[:400]}")
            elif expected not in combined:
                failures.append(
                    f"{name}: validator failed without the expected reason\n"
                    f"    expected substring: {expected!r}\n"
                    f"    output: {combined.strip()[:400]}")
            else:
                print(f"  caught: {name}")

    if failures:
        print("\nMUTATION TESTS FAILED:")
        for f in failures:
            print(f"  - {f}")
        return 1
    n_mut = len(MUTATIONS) + len(OPSBRIDGE_MUTATIONS)
    n_input = len(INPUT_NEGATIVE_CONTROLS)
    print(f"\nALL {n_mut} mutations + {n_input} unusable-render controls caught "
          f"- every assertion can fail.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
