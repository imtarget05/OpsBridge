#!/usr/bin/env bash
# Validate the RENDERED prod overlay, not just the source files.
#
# This gate exists because of a real Phase 7A/7C finding: CI built, scanned,
# pushed and digest-verified the order-api image, and the release was green —
# while `kubectl kustomize overlays/prod` still emitted
# `acrflashsale-placeholder.azurecr.io/order-api:latest`, because the overlay's
# `images:[].name` did not match the base image name and Kustomize silently
# skipped the transform. Nothing in the pipeline rendered the manifest, so
# nothing could see it. Image-level proofs do not prove deployability.
#
# Runs on any machine with kubectl (kustomize is built in) — no cluster needed.
set -euo pipefail

cd "$(dirname "$0")/.."
OVERLAY=infrastructure/kubernetes/overlays/prod
# The OpsBridge API artefact set is SELF-CONTAINED (see
# infrastructure/kubernetes/base/opsbridge-api/README.md): its image does not
# exist in a registry yet, so it is deliberately not referenced by the base
# kustomization and never reaches the prod overlay. It still has to be gated, or
# "the manifests exist" would be the only thing anyone ever verified about them.
OPSBRIDGE_DIR=infrastructure/kubernetes/base/opsbridge-api
# The OpsBridge WEB artefact set is self-contained for the same reason (see
# infrastructure/kubernetes/base/opsbridge-web/README.md): the image does not
# exist in a registry yet, so it is deliberately not referenced by the base
# kustomization and never reaches the prod overlay. It still has to be gated.
WEB_DIR=infrastructure/kubernetes/base/opsbridge-web

# Usage: validate-manifests.sh [pre-rendered-prod.yaml] [pre-rendered-opsbridge.yaml] [pre-rendered-web.yaml]
#
# With no argument, this script renders the prod overlay itself — the mode CI
# runs. With a file argument, it validates THAT file instead, which is how
# scripts/test-validate-manifests.py proves each assertion can actually fail.
# Without this seam, testing the tests would need a cluster (or a kubectl able to
# render mutated fixtures) and the assertions would stay unproven.
#
# The SECOND argument is the same seam for the OpsBridge set: it overrides the
# render of $OPSBRIDGE_DIR, so the mutation test can hand the validator a broken
# OpsBridge manifest and prove the new rules can fail.
#
# The THIRD argument is the same seam for the OpsBridge WEB set.
INPUT_FILE="${1:-}"
OPSBRIDGE_FILE="${2:-}"
WEB_FILE="${3:-}"

RENDERED=$(mktemp)
OB_RENDERED=$(mktemp)
WEB_RENDERED=$(mktemp)
trap 'rm -f "$RENDERED" "$OB_RENDERED" "$WEB_RENDERED"' EXIT

fail=0
err() { echo "FAIL: $*" >&2; fail=1; }

if [ -n "$INPUT_FILE" ]; then
  [ -f "$INPUT_FILE" ] || { echo "FAIL: no such rendered file: $INPUT_FILE" >&2; exit 1; }
  cp "$INPUT_FILE" "$RENDERED"
  echo "== validating pre-rendered input: $INPUT_FILE =="
else
  echo "== render $OVERLAY =="
  if ! kubectl kustomize "$OVERLAY" >"$RENDERED" 2>/tmp/kustomize.err; then
    cat /tmp/kustomize.err >&2
    echo "FAIL: kustomize render failed" >&2
    exit 1
  fi
fi
echo "rendered $(grep -c '^kind: ' "$RENDERED") objects"

# Extract one rendered document by Kind + metadata.name.
# Needed because a repo-wide grep is NOT a per-workload assertion: an early
# version of this script checked `grep -q 'key: rabbitmq-connection'`, which
# passed even with the env var deleted from order-api, because order-worker
# also references that key. A check that cannot fail is worse than no check —
# it manufactures false confidence, which is the exact Phase 7C bug class.
#
# The file is a parameter because the OpsBridge artefact set is validated from
# its OWN render (see section 9): a doc_of that always looked at $RENDERED
# could only ever assert about the prod overlay.
doc_of_in() {
  file=$1; kind=$2; name=$3
  # BEGIN { n = 0 } is REQUIRED, not cosmetic. Without it `n` is an
  # uninitialized variable, and an uninitialized awk variable used as an array
  # subscript becomes the STRING "" — so the first document was accumulated in
  # doc[""] while the search loop below walked the NUMERIC subscripts 0..n.
  # The consequence was that the FIRST rendered document was invisible to this
  # function. That was harmless while every target happened to be a later
  # document, and it stopped being harmless the moment a set whose first
  # document is a Service (opsbridge-api) had to be validated.
  awk -v kind="$kind" -v name="$name" '
    BEGIN { n = 0 }
    { doc[n] = doc[n] $0 "\n" }
    /^---$/ { n++ }
    END {
      for (i = 0; i <= n; i++)
        if (doc[i] ~ ("(^|\n)kind: " kind "(\n|$)") &&
            doc[i] ~ ("(^|\n)  name: " name "(\n|$)")) { printf "%s", doc[i]; found = 1; exit }
      exit !found
    }' "$file"
}
doc_of() { doc_of_in "$RENDERED" "$1" "$2"; }

# Prints the reason why a rendered file cannot be used as a manifest set, or
# nothing at all if it can.
#
# An empty output means "no problem", so the caller needs no exit-status
# trickery. The reasons are printed instead of a boolean because a gate that
# fails with "invalid" and a gate that fails with "the file is empty (0
# bytes)" send the reader to very different places.
#
# The function is generic on purpose: more than one artefact set is validated
# from its OWN render (the OpsBridge API set in section 9, the OpsBridge web
# set in section 10), and a copy of this parser per set is a copy that drifts.
render_problem() {
  if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 is not available, so the render cannot be parsed"
    return 0
  fi
  python3 - "$1" <<'PY'
import sys

path = sys.argv[1]
try:
    import yaml
except ImportError:
    # validate-objects.py already hard-fails without PyYAML, so the script as a
    # whole cannot be trusted without it. Saying so is better than parsing
    # the file "well enough" and reporting a coverage that did not happen.
    print("PyYAML is not installed, so the render cannot be parsed")
    sys.exit(0)

try:
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        text = fh.read()
except OSError as exc:
    print(f"the file cannot be read ({exc})")
    sys.exit(0)

if not text.strip():
    print("the file is empty (0 bytes)")
    sys.exit(0)

try:
    docs = [d for d in yaml.safe_load_all(text) if d is not None]
except Exception as exc:
    print(f"it is not parseable YAML ({exc})")
    sys.exit(0)

if not docs:
    print("it contains no YAML document")
    sys.exit(0)

for doc in docs:
    if not isinstance(doc, dict):
        print(f"it contains a non-mapping document ({type(doc).__name__})")
        sys.exit(0)
    if not doc.get("kind"):
        print("it contains a document with no kind")
        sys.exit(0)
    if not doc.get("apiVersion"):
        print(f"it contains a {doc.get('kind')} with no apiVersion")
        sys.exit(0)
PY
}


echo "== assertions =="
# 1. No placeholder registry may survive into rendered output.
grep -q 'placeholder' "$RENDERED" \
  && err "rendered manifest still contains 'placeholder' (image override did not apply)"

# 2. Mutable tags are forbidden (ADR-011).
grep -E 'image: [^ ]+:latest$' "$RENDERED" >/dev/null \
  && err "rendered manifest pins an image to :latest"

# 3. Our own images must come from the real ACR, pinned to a 40-char SHA. This
#    is a PER-DOCUMENT check on purpose: a repo-wide grep is satisfied by ANY
#    one image line, so when the migration Job (same artifact as the API) still
#    carries the right pin, a wrong-registry API image slips through and the
#    rollout ImagePullBackOffs with a green gate. The api_wrong_registry
#    mutation test exists because the first version of this rule had that hole.
#    Workload -> expected IMAGE REPO (not the workload name): the Job runs the
#    API artifact, so order-migrate maps to order-api.
pin_check() { kind=$1; name=$2; repo=$3
  D=$(doc_of "$kind" "$name") || { err "rendered manifest has no ${kind}/${name}"; return; }
  printf '%s' "$D" | grep -Eq "^[[:space:]]+image: acrflashsalep6\\.azurecr\\.io/${repo}:[0-9a-f]{40}[[:space:]]*$" \
    || err "${kind}/${name} is not pinned to acrflashsalep6.azurecr.io/${repo}:<40-char sha>"
}
pin_check Deployment order-api order-api
pin_check Deployment order-worker order-worker
pin_check Job order-migrate order-api

# 4. Both workloads must exist: the async flow is API -> queue -> worker, and a
#    manifest set without the worker cannot satisfy the Phase 7C gate. The Job is
#    in the same list because the migration contract is part of the desired state,
#    not an operator ritual that lives outside git.
for kind_name in "Deployment/order-api" "Deployment/order-worker" "Job/order-migrate"; do
  res="${kind_name%%/*}"; nm="${kind_name##*/}"
  awk -v r="$res" -v n="$nm" '
    /^kind: /   { k = $2 }
    /^  name: / { if (k == r && $2 == n) found = 1 }
    END         { exit !found }' "$RENDERED" \
    || err "rendered manifest has no ${kind_name}"
done

# 5. The data tier must be in-cluster and unreachable from outside.
grep -q 'type: LoadBalancer' "$RENDERED" \
  && err "a Service is exposed as LoadBalancer (public IP) — data tier must stay ClusterIP"
grep -q 'containerPort: 15672' "$RENDERED" \
  && err "RabbitMQ management port is exposed"
for dep in postgres redis rabbitmq; do
  grep -Eq "^  name: ${dep}$" "$RENDERED" || err "rendered manifest has no ${dep}"
done


# 6. Each app container must receive the connection strings it actually needs.
API_DOC=$(doc_of Deployment order-api) || API_DOC=""
WORKER_DOC=$(doc_of Deployment order-worker) || WORKER_DOC=""

if [ -n "$API_DOC" ]; then
  for kv in "ConnectionStrings__DefaultConnection:pg-connection" \
            "ConnectionStrings__Redis:redis-connection" \
            "ConnectionStrings__RabbitMQ:rabbitmq-connection" \
            "Auth__Jwt__SigningKey:jwt-signing-key"; do
    env_name=${kv%%:*}; secret_key=${kv##*:}
    printf '%s' "$API_DOC" | grep -q "name: ${env_name}$" \
      || err "order-api is missing ${env_name} (the app reads ConnectionStrings; without it the API silently falls back to localhost)"
    printf '%s' "$API_DOC" | grep -q "key: ${secret_key}$" \
      || err "order-api does not reference secret key ${secret_key}"
  done
  # Provider must be pinned explicitly, never inferred (ADR-005).
  printf '%s' "$API_DOC" | grep -q "name: Messaging__Provider$" \
    || err "order-api does not set Messaging__Provider explicitly"
fi

if [ -n "$WORKER_DOC" ]; then
  for env_name in ConnectionStrings__DefaultConnection ConnectionStrings__RabbitMQ; do
    printf '%s' "$WORKER_DOC" | grep -q "name: ${env_name}$" \
      || err "order-worker is missing ${env_name}"
  done
  printf '%s' "$WORKER_DOC" | grep -q "name: Messaging__Provider$" \
    || err "order-worker does not set Messaging__Provider explicitly"
  # The worker consumes queues; it must not listen on a port.
  printf '%s' "$WORKER_DOC" | grep -q "containerPort:" \
    && err "order-worker declares a containerPort but should expose nothing"
fi

# 7. The migration Job must be a migration, not a second API instance: it runs
#    --migrate, and it must depend on PostgreSQL ONLY. A Job that also needs the
#    brokers can never run before them, which inverts the ordering the whole
#    contract exists to guarantee.
#
#    The rules live in migrate_job_rules() because there is more than one schema
#    step in this repository now: `order-migrate` (FlashSale) and
#    `opsbridge-migrate` (OpsBridge, ADR-018). A second migration Job validated
#    by a copy of these rules would be a copy that drifts — and a rule that
#    exists only for the first Job is a rule the second Job can violate.
#
#    $1 kind, $2 name, $3 the rendered doc, $4 the PostgreSQL secret key the Job
#    must reference, $5... the secret keys it must NOT reference.
migrate_job_rules() {
  local kind=$1 name=$2 doc=$3 pgkey=$4
  shift 4
  [ -n "$doc" ] || return 0

  # NB: match the RENDERED form. Kustomize/PyYAML normalize `- "--migrate"` to
  # `- --migrate`, so asserting on the quoted source form fails against a
  # perfectly good manifest — the same class of bug as grepping source files for
  # a value the overlay rewrites.
  printf '%s' "$doc" | grep -Eq '^[[:space:]]*- --migrate$' \
    || err "${kind}/${name} does not pass --migrate (it would start a full API)"
  printf '%s' "$doc" | grep -q "key: ${pgkey}$" \
    || err "${kind}/${name} has no PostgreSQL connection string"
  # redis-connection and jwt-signing-key are equally forbidden: the Job must not
  # need the cache, and it must not need auth config for the same reason — a
  # signing key the Job is asked to provide is a signing key the migration path
  # would end up validating (ADR-013 §2).
  for forbidden in "$@"; do
    printf '%s' "$doc" | grep -q "key: ${forbidden}$" \
      && err "${kind}/${name} depends on ${forbidden}; migrations must need Postgres only"
  done
  # ttlSecondsAfterFinished keeps a finished Job from pinning pods forever.
  printf '%s' "$doc" | grep -q "ttlSecondsAfterFinished:" \
    || err "${kind}/${name} has no ttlSecondsAfterFinished (pods are never garbage collected)"
  # A migration must not be rescheduled onto a second node while the first
  # attempt may still hold the EF migration lock.
  printf '%s' "$doc" | grep -q "restartPolicy: Never" \
    || err "${kind}/${name} does not set restartPolicy: Never (a migration must not run twice at once)"
  # The Job name is fixed, so a second concurrent migration of the same schema
  # must not be possible.
  printf '%s' "$doc" | grep -q "completionMode: NonIndexed" \
    || err "${kind}/${name} does not set completionMode: NonIndexed (a fixed Job name must not stack a second migration)"
  # Bounded retry: a broken migration is not a transient fault.
  printf '%s' "$doc" | grep -q "backoffLimit:" \
    || err "${kind}/${name} has no backoffLimit (a broken migration must not be retried forever)"
  # The environment is stated explicitly rather than inherited from the empty
  # default, so the schema step is never a silently different environment.
  printf '%s' "$doc" | grep -q "name: ASPNETCORE_ENVIRONMENT" \
    || err "${kind}/${name} does not state ASPNETCORE_ENVIRONMENT explicitly"

  return 0
}

MIGRATE_DOC=$(doc_of Job order-migrate) || MIGRATE_DOC=""
migrate_job_rules "Job" "order-migrate" "$MIGRATE_DOC" "pg-connection" \
  rabbitmq-connection redis-connection jwt-signing-key

# 8. Only services that listen may be fronted by a Service.
SERVICES=$(awk '/^kind: Service$/{s=1} s&&/^  name: /{print $2; s=0}' "$RENDERED" | sort | tr '\n' ' ')
echo "services rendered: ${SERVICES}"
case " ${SERVICES}" in
  *" order-worker "*) err "order-worker has a Service but exposes no port";;
esac

# 8. Cross-reference the decoded objects with no cluster and no kubeconfig.
#    Deliberately NOT `kubectl apply/create --dry-run=client`: both still run API
#    discovery (GET /api, /openapi/v2) so kubectl can map Kind -> GVR, so against
#    a stopped cluster they fail on DNS, and they only appear to pass while
#    kubectl's cached discovery document is still fresh. A gate that flips with
#    cache expiry is not a gate. See scripts/validate-objects.py.
if ./scripts/validate-objects.py "$RENDERED"; then
  :
else
  err "object cross-reference failed (see FAIL lines above)"
fi

# 9. The OpsBridge API artefact set (deployment + service + migration Job).
#
#    It is NOT part of the prod overlay: the image does not exist in a registry
#    yet, and adding it to the base would break the CURRENT deployment with an
#    ImagePullBackOff (the prod overlay pins every image by digest, ADR-011).
#    It is validated here, on its own render, with the BASE conventions instead
#    of the prod ones: a registry-neutral image name with the
#    `placeholder-set-by-overlay` tag, not a digest.
#
#    The rule set is the point of the section: a second schema-migration Job
#    that is not held to the same rules as `order-migrate` is a Job whose
#    ordering contract nobody has checked.
#
#    KNOWN LIMITATION, stated here because this is where a reader of this
#    script looks: `scripts/validate-objects.py` — the cross-reference gate
#    that section 8 runs over the prod render — knows only `flashsale-secrets`
#    in its `SECRET_CONTRACT` constant. This section does NOT run that gate
#    over the OpsBridge render (section 8 runs it over the prod render only),
#    so the OpsBridge set is covered here instead. The moment the OpsBridge
#    set is wired into an overlay, that gate will reject it with
#    "unknown Secret 'opsbridge-secrets'" until a human adds
#    `opsbridge-secrets` and `opsbridge-migrate-secrets` to
#    `SECRET_CONTRACT` in `scripts/validate-objects.py`. The exact edit is
#    spelled out in infrastructure/kubernetes/base/opsbridge-api/README.md
#    ("What this set does NOT cover").
echo "== opsbridge-api artefact set =="
OB_RENDER_ERROR=""
if [ -n "$OPSBRIDGE_FILE" ]; then
  [ -f "$OPSBRIDGE_FILE" ] || { echo "FAIL: no such rendered opsbridge file: $OPSBRIDGE_FILE" >&2; exit 1; }
  cp "$OPSBRIDGE_FILE" "$OB_RENDERED"
  OB_SOURCE="$OPSBRIDGE_FILE"
  echo "== validating pre-rendered opsbridge input: $OPSBRIDGE_FILE =="
else
  OB_SOURCE="the render of $OPSBRIDGE_DIR"
  echo "== render $OPSBRIDGE_DIR =="
  if ! kubectl kustomize "$OPSBRIDGE_DIR" >"$OB_RENDERED" 2>/tmp/kustomize-opsbridge.err; then
    cat /tmp/kustomize-opsbridge.err >&2
    err "kustomize render of $OPSBRIDGE_DIR failed"
    OB_RENDER_ERROR="kustomize render of $OPSBRIDGE_DIR failed"
  fi
fi

# The OpsBridge section must never be skipped SILENTLY.
#
# An empty render used to make every section-9 assertion vacuous while the
# script still printed "... and the OpsBridge artefact set satisfies the same
# migration contract" and exited 0. A gate that cannot tell "checked and
# passed" from "never ran" manufactures exactly the false confidence this
# script was written to end — the Phase 7C bug class, one level up.
#
# So an unusable render is a FAILURE with the reason, whether the caller
# supplied the file (empty or unparseable) or the render failed right here
# (a failed render must never be reported as a pass either).
OB_RENDER_PROBLEM=""
if [ -z "$OB_RENDER_ERROR" ]; then
  # `|| true`: a crash in the helper must not abort the script before it can
  # report WHICH rule failed. An empty problem string falls through to the
  # section, whose own "the set has no Job/Deployment/Service" assertions then
  # fail loudly — never silently.
  OB_RENDER_PROBLEM=$(render_problem "$OB_RENDERED" || true)
fi
if [ -n "$OB_RENDER_PROBLEM" ]; then
  err "the OpsBridge render (${OB_SOURCE}) is not usable: ${OB_RENDER_PROBLEM}; every section-9 assertion was skipped, so this run must not report PASS"
fi

if [ -z "$OB_RENDER_ERROR" ] && [ -z "$OB_RENDER_PROBLEM" ]; then
  OB_JOB=$(doc_of_in "$OB_RENDERED" Job opsbridge-migrate) || OB_JOB=""
  OB_DEPLOY=$(doc_of_in "$OB_RENDERED" Deployment opsbridge-api) || OB_DEPLOY=""
  OB_SERVICE=$(doc_of_in "$OB_RENDERED" Service opsbridge-api) || OB_SERVICE=""

  # The three objects must exist: an API without a Service is unreachable, and
  # an API without a migration step has no defined path from an empty database
  # to a migrated one.
  [ -n "$OB_JOB" ] || err "opsbridge-api set has no Job/opsbridge-migrate"
  [ -n "$OB_DEPLOY" ] || err "opsbridge-api set has no Deployment/opsbridge-api"
  [ -n "$OB_SERVICE" ] || err "opsbridge-api set has no Service/opsbridge-api"

  # 9a. The migration Job is held to the SAME rules as order-migrate (the
  #     shared function), with two OpsBridge-specific additions:
  #       * it must use the DDL-capable credential (`pg-migration-connection`),
  #         not the runtime's DML credential (`pg-connection`) — ADR-018's
  #         "separate DDL step with its own identity";
  #       * it must not reference the runtime Secret at all, because the
  #         `--migrate` branch runs BEFORE the JWT gate and needs no
  #         application credential.
  if [ -n "$OB_JOB" ]; then
    migrate_job_rules "Job" "opsbridge-migrate" "$OB_JOB" "pg-migration-connection" \
      rabbitmq-connection redis-connection jwt-signing-key pg-connection
    printf '%s' "$OB_JOB" | grep -q "name: opsbridge-migrate-secrets" \
      || err "Job/opsbridge-migrate does not use the DDL-capable secret opsbridge-migrate-secrets"
    printf '%s' "$OB_JOB" | grep -q "name: opsbridge-secrets" \
      && err "Job/opsbridge-migrate references the runtime Secret opsbridge-secrets; the migration step needs no application credential"
    # The reverse direction of ADR-018's identity separation, asserted on the
    # CREDENTIAL and not only on the Secret's name.
    #
    # The DDL step must not be handed the runtime's DML key under ANY Secret
    # name: a Job that holds `pg-connection` is a schema step running with the
    # runtime role, which either fails for lack of DDL rights or (worse)
    # forces the runtime role to be given them. `migrate_job_rules` already
    # forbids the key `pg-connection`; this states the identity separation
    # explicitly and next to the Deployment-side rule, so the two directions
    # read as one contract instead of two scattered assertions.
    printf '%s' "$OB_JOB" | grep -Eq "key: ['\"]?pg-connection['\"]?\$" \
      && err "Job/opsbridge-migrate references the runtime DML secret key pg-connection; the migration step must hold the DDL identity only (ADR-018)"
  fi

  if [ -n "$OB_DEPLOY" ]; then
    # 9b. The runtime workload uses the DML credential and never the DDL one.
    printf '%s' "$OB_DEPLOY" | grep -q "key: pg-connection$" \
      || err "Deployment/opsbridge-api has no PostgreSQL connection string"
    printf '%s' "$OB_DEPLOY" | grep -q "key: jwt-signing-key$" \
      || err "Deployment/opsbridge-api does not reference secret key jwt-signing-key"
    printf '%s' "$OB_DEPLOY" | grep -q "name: opsbridge-secrets" \
      || err "Deployment/opsbridge-api does not use the runtime Secret opsbridge-secrets"
    printf '%s' "$OB_DEPLOY" | grep -q "name: opsbridge-migrate-secrets" \
      && err "Deployment/opsbridge-api references the DDL Secret opsbridge-migrate-secrets; the runtime identity must hold DML rights only"
    # The DDL/DML separation, asserted on the CREDENTIAL and not only on the
    # Secret's NAME.
    #
    # The rule above is necessary but NOT sufficient: the Secret's name is
    # only a label. A runtime pod handed `pg-migration-connection` under the
    # runtime Secret's name holds the DDL capability, whatever the Secret is
    # called — and before this rule existed the validator printed
    # "== RESULT: PASS ==" with exit 0 for exactly that manifest, which made
    # ADR-018's "the runtime identity holds DML rights only" unfalsifiable.
    #
    # So the DDL key is forbidden in the runtime workload by KEY, whatever
    # Secret carries it, and (symmetrically, see the Job block above) the DML
    # key is forbidden in the migration Job.
    printf '%s' "$OB_DEPLOY" | grep -Eq "key: ['\"]?pg-migration-connection['\"]?\$" \
      && err "Deployment/opsbridge-api references the DDL-capable secret key pg-migration-connection; the runtime identity must hold DML rights only (ADR-018)"
    # OpsBridge is a bounded context with no broker: the API must not be wired
    # to a queue or a cache it does not use.
    for forbidden in rabbitmq-connection redis-connection; do
      printf '%s' "$OB_DEPLOY" | grep -q "key: ${forbidden}$" \
        && err "Deployment/opsbridge-api depends on ${forbidden}; OpsBridge has no broker or cache"
    done
    # 9c. Hardening, identical to the rest of the tree (ADR-007).
    printf '%s' "$OB_DEPLOY" | grep -q "runAsNonRoot: true" \
      || err "Deployment/opsbridge-api does not set runAsNonRoot"
    printf '%s' "$OB_DEPLOY" | grep -q "runAsUser: 1654" \
      || err "Deployment/opsbridge-api does not run as uid 1654 (ADR-007)"
    printf '%s' "$OB_DEPLOY" | grep -q "readOnlyRootFilesystem: true" \
      || err "Deployment/opsbridge-api does not set readOnlyRootFilesystem"
    printf '%s' "$OB_DEPLOY" | grep -q "mountPath: /tmp" \
      || err "Deployment/opsbridge-api does not mount a writable /tmp (readOnlyRootFilesystem breaks .NET's Path.GetTempPath())"
    # 9d. The runtime must never auto-migrate outside Development/Test: the
    #     application refuses to start when it does, so an accidental
    #     `OpsBridge__AutoMigrate=true` would be a crash-loop, not a silent
    #     schema change. Assert it is NOT set.
    printf '%s' "$OB_DEPLOY" | grep -q "name: OpsBridge__AutoMigrate" \
      && err "Deployment/opsbridge-api sets OpsBridge__AutoMigrate; the runtime must never migrate the schema (ADR-018)"
  fi

  # 9e. The Job must be the EXACT artefact the Deployment rolls: the
  #     migrations are compiled into OpsBridge.Api.dll, so a Job running a
  #     different image is a schema step for a different version of the code.
  if [ -n "$OB_JOB" ] && [ -n "$OB_DEPLOY" ]; then
    # `|| true`: with `set -e` AND `pipefail`, a manifest whose image line is
    # missing would make the assignment itself fail and abort the script before
    # it could report WHICH rule failed. The comparison below is the rule.
    OB_JOB_IMAGE=$(printf '%s' "$OB_JOB" | grep -E '^[[:space:]]+image: ' | head -1 | sed 's/.*image: //' || true)
    OB_DEPLOY_IMAGE=$(printf '%s' "$OB_DEPLOY" | grep -E '^[[:space:]]+image: ' | head -1 | sed 's/.*image: //' || true)
    [ "$OB_JOB_IMAGE" = "$OB_DEPLOY_IMAGE" ] \
      || err "Job/opsbridge-migrate runs ${OB_JOB_IMAGE} but Deployment/opsbridge-api runs ${OB_DEPLOY_IMAGE}; the migration step must be the same artefact"
    # 9f. Base convention: registry-neutral name, placeholder tag. The prod
    #     overlay's `images:` transformer rewrites it to the pinned digest when
    #     the image exists.
    [ "$OB_DEPLOY_IMAGE" = "opsbridge-api:placeholder-set-by-overlay" ] \
      || err "Deployment/opsbridge-api image is ${OB_DEPLOY_IMAGE}, not the base convention opsbridge-api:placeholder-set-by-overlay"
  fi

  # 9g. The Service must not be publicly exposed, and must select the pods
  #     the Deployment actually creates.
  if [ -n "$OB_SERVICE" ]; then
    printf '%s' "$OB_SERVICE" | grep -q "type: LoadBalancer" \
      && err "Service/opsbridge-api is exposed as LoadBalancer (public IP)"
    printf '%s' "$OB_SERVICE" | grep -q "app: opsbridge-api" \
      || err "Service/opsbridge-api does not select app: opsbridge-api"
  fi
fi

# 10. The OpsBridge WEB artefact set (deployment + service + HTTPRoute).
#
#     It is NOT part of the prod overlay, for the same reason as the API set
#     (section 9): the image exists in no registry, and the prod overlay pins
#     every image by digest (ADR-011). So it is validated here, on its own
#     render, with the BASE conventions: a registry-neutral image name with
#     the `placeholder-set-by-overlay` tag, not a digest.
#
#     The rules below are the deployment contract of ADR-020, expressed as
#     assertions a manifest can fail. Each one has a negative control in
#     scripts/test-validate-manifests.py — a rule nobody can break is a rule
#     that documents nothing.
echo "== opsbridge-web artefact set =="
WEB_RENDER_ERROR=""
if [ -n "$WEB_FILE" ]; then
  [ -f "$WEB_FILE" ] || { echo "FAIL: no such rendered web file: $WEB_FILE" >&2; exit 1; }
  cp "$WEB_FILE" "$WEB_RENDERED"
  WEB_SOURCE="$WEB_FILE"
  echo "== validating pre-rendered opsbridge-web input: $WEB_FILE =="
else
  WEB_SOURCE="the render of $WEB_DIR"
  echo "== render $WEB_DIR =="
  if ! kubectl kustomize "$WEB_DIR" >"$WEB_RENDERED" 2>/tmp/kustomize-web.err; then
    cat /tmp/kustomize-web.err >&2
    err "kustomize render of $WEB_DIR failed"
    WEB_RENDER_ERROR="kustomize render of $WEB_DIR failed"
  fi
fi

# An unusable web render must be a FAILURE, for the same reason the OpsBridge
# one is (section 9): an empty render would make every section-10 assertion
# vacuous while the script still printed "== RESULT: PASS ==" and exited 0.
WEB_RENDER_PROBLEM=""
if [ -z "$WEB_RENDER_ERROR" ]; then
  WEB_RENDER_PROBLEM=$(render_problem "$WEB_RENDERED" || true)
fi
if [ -n "$WEB_RENDER_PROBLEM" ]; then
  err "the OpsBridge web render (${WEB_SOURCE}) is not usable: ${WEB_RENDER_PROBLEM}; every section-10 assertion was skipped, so this run must not report PASS"
fi

if [ -z "$WEB_RENDER_ERROR" ] && [ -z "$WEB_RENDER_PROBLEM" ]; then
  WEB_DEPLOY=$(doc_of_in "$WEB_RENDERED" Deployment opsbridge-web) || WEB_DEPLOY=""
  WEB_SERVICE=$(doc_of_in "$WEB_RENDERED" Service opsbridge-web) || WEB_SERVICE=""
  WEB_ROUTE=$(doc_of_in "$WEB_RENDERED" HTTPRoute opsbridge-web) || WEB_ROUTE=""

  # 10a. The three objects must exist. A web tier without a Service is
  #      unreachable; a web tier without a route is unreachable from outside
  #      the cluster; a web tier without a Deployment is a manifest set that
  #      describes nothing.
  [ -n "$WEB_DEPLOY" ] || err "opsbridge-web set has no Deployment/opsbridge-web"
  [ -n "$WEB_SERVICE" ] || err "opsbridge-web set has no Service/opsbridge-web"
  [ -n "$WEB_ROUTE" ] || err "opsbridge-web set has no HTTPRoute/opsbridge-web"

  if [ -n "$WEB_DEPLOY" ]; then
    # 10b. Base convention: registry-neutral name, placeholder tag. The prod
    #      overlay's `images:` transformer rewrites it to the pinned digest
    #      when the image exists.
    WEB_IMAGE=$(printf '%s' "$WEB_DEPLOY" | grep -E '^[[:space:]]+image: ' | head -1 | sed 's/.*image: //' || true)
    [ "$WEB_IMAGE" = "opsbridge-web:placeholder-set-by-overlay" ] \
      || err "Deployment/opsbridge-web image is ${WEB_IMAGE}, not the base convention opsbridge-web:placeholder-set-by-overlay"

    # 10c. The container port is 8080, not 80: the container runs as a
    #      non-root user and cannot bind a privileged port. The Service maps
    #      80 -> 8080.
    printf '%s' "$WEB_DEPLOY" | grep -q "containerPort: 8080" \
      || err "Deployment/opsbridge-web does not declare containerPort 8080 (the contract is 8080 so the container can run as a non-root user)"

    # 10d. Not privileged. A privileged container is root-equivalent whatever
    #      the user id says.
    printf '%s' "$WEB_DEPLOY" | grep -Eq '^[[:space:]]+privileged: true' \
      && err "Deployment/opsbridge-web sets privileged: true"

    # 10e. Not root, and the uid the tree uses (ADR-007).
    printf '%s' "$WEB_DEPLOY" | grep -q "runAsNonRoot: true" \
      || err "Deployment/opsbridge-web does not set runAsNonRoot"
    printf '%s' "$WEB_DEPLOY" | grep -Eq '^[[:space:]]+runAsUser: ["'\'']?0["'\'']?$' \
      && err "Deployment/opsbridge-web runs as root (runAsUser: 0)"
    printf '%s' "$WEB_DEPLOY" | grep -q "runAsUser: 1654" \
      || err "Deployment/opsbridge-web does not run as uid 1654 (ADR-007)"

    # 10f. Read-only root filesystem. nginx needs a writable scratch area, and
    #      the manifest provides it with emptyDir volumes — a writable root
    #      filesystem is the thing that must NOT be the answer.
    printf '%s' "$WEB_DEPLOY" | grep -q "readOnlyRootFilesystem: true" \
      || err "Deployment/opsbridge-web does not set readOnlyRootFilesystem: true"
    printf '%s' "$WEB_DEPLOY" | grep -q "readOnlyRootFilesystem: false" \
      && err "Deployment/opsbridge-web sets readOnlyRootFilesystem: false"

    # 10g. No host path. A hostPath volume reaches outside the pod: it is a
    #      node-level escape hatch and the classic way a "read-only" pod still
    #      writes to the host.
    printf '%s' "$WEB_DEPLOY" | grep -q "hostPath:" \
      && err "Deployment/opsbridge-web mounts a host path"

    # 10h. BOTH probes. A Deployment with only a liveness probe publishes a
    #      pod that cannot serve traffic; one with only a readiness probe
    #      restarts a pod that is merely busy.
    printf '%s' "$WEB_DEPLOY" | grep -q "livenessProbe:" \
      || err "Deployment/opsbridge-web declares no livenessProbe"
    printf '%s' "$WEB_DEPLOY" | grep -q "readinessProbe:" \
      || err "Deployment/opsbridge-web declares no readinessProbe"

    # 10i. API_UPSTREAM is an ADDRESS, not a credential. Three separate
    #      failure modes, three rules:
    #        * absent — the image falls back to its built-in default upstream
    #          and silently proxies to the wrong place;
    #        * read from a Secret — then the "address" is a credential, and a
    #          credential that has to be templated into a DNS name is a
    #          credential in a config file;
    #        * carrying credential material (userinfo, a token in the query
    #          string) — an address must be an address.
    printf '%s' "$WEB_DEPLOY" | grep -q "name: API_UPSTREAM" \
      || err "Deployment/opsbridge-web does not set API_UPSTREAM (the image has no other way to learn where the API is)"
    printf '%s' "$WEB_DEPLOY" | grep -A1 "name: API_UPSTREAM" | grep -q "valueFrom:" \
      && err "Deployment/opsbridge-web reads API_UPSTREAM from a Secret; the upstream is an in-cluster address, not a credential"
    # `|| true`: with `set -e` AND `pipefail`, a manifest with no API_UPSTREAM
    # would make the assignment itself fail and abort the script before it
    # could report WHICH rule failed. The checks below are the rules.
    WEB_UPSTREAM=$(printf '%s' "$WEB_DEPLOY" | grep -A1 "name: API_UPSTREAM" \
      | grep -E '^[[:space:]]+value:' | head -1 \
      | sed -E 's/^[[:space:]]*value:[[:space:]]*//' || true)
    # Credential material, checked FIRST so the message names the real
    # problem: '@' is userinfo (user:password@host), and the word list covers
    # a token or a key smuggled into the address.
    case "$WEB_UPSTREAM" in
      *@*) err "Deployment/opsbridge-web sets API_UPSTREAM to ${WEB_UPSTREAM}, which carries credential material (userinfo); the upstream must be a plain in-cluster address" ;;
    esac
    printf '%s' "$WEB_UPSTREAM" | grep -Eqi '(password|passwd|secret|token|api[-_]?key|credential)' \
      && err "Deployment/opsbridge-web sets API_UPSTREAM to ${WEB_UPSTREAM}, which carries credential material; the upstream must be a plain in-cluster address"
    # A plain in-cluster address: scheme://host[:port][/path], nothing else.
    # The character class excludes '@' (userinfo), '?' (query string) and '='
    # (a query parameter), so this is a STRICTER check than the credential
    # check above: a query string on an upstream address is meaningless even
    # when it carries no obvious secret.
    printf '%s' "$WEB_UPSTREAM" | grep -Eq '^https?://[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:[0-9]{1,5})?(/[A-Za-z0-9._~:!$&()*+,;%-]*)?$' \
      || err "Deployment/opsbridge-web sets API_UPSTREAM to ${WEB_UPSTREAM}, which is not a plain in-cluster address (expected scheme://host[:port])"

    # 10p. The upstream must address the API's SERVICE, on the port that
    #      SERVICE declares.
    #
    #      This is the rule that keeps the image's default and the manifest
    #      from being confused with each other:
    #
    #        * the IMAGE's default is the API's CONTAINER port (8080) - the
    #          right address for a Docker-to-Docker hop;
    #        * the MANIFEST must use the API's SERVICE port (80) - because a
    #          ClusterIP only answers the ports its Service DECLARES. A
    #          connection to :8080 is dropped by kube-proxy, even though the
    #          pod behind the Service is listening on 8080.
    #
    #      So the two halves are compatible only because the manifest sets the
    #      variable explicitly, and the port it sets has to be the Service's
    #      port. The Service is the OpsBridge API one (Service/opsbridge-api),
    #      which section 9 renders; if that render is unusable the rule below
    #      FAILS rather than passing silently.
    OB_API_SERVICE=$(doc_of_in "$OB_RENDERED" Service opsbridge-api) || OB_API_SERVICE=""
    if [ -z "$OB_API_SERVICE" ]; then
      err "the API Service/opsbridge-api could not be read, so the API_UPSTREAM port of Deployment/opsbridge-web could not be checked"
    else
      OB_API_SERVICE_NAME=$(printf '%s' "$OB_API_SERVICE" | grep -E '^  name: ' | head -1 | sed -E 's/^  name: //' || true)
      OB_API_SERVICE_PORT=$(printf '%s' "$OB_API_SERVICE" | grep -E '^[[:space:]]+port: [0-9]+$' | head -1 | sed -E 's/^[[:space:]]+port: //' || true)

      # The host of the upstream must be the API Service's name: the upstream
      # addresses a Service, and the Service's name is its DNS name.
      WEB_UPSTREAM_HOST=$(printf '%s' "$WEB_UPSTREAM" | sed -E 's#^https?://([^:/]+).*$#\1#' || true)
      [ -n "$OB_API_SERVICE_NAME" ] && [ "$WEB_UPSTREAM_HOST" = "$OB_API_SERVICE_NAME" ] \
        || err "Deployment/opsbridge-web sets API_UPSTREAM to ${WEB_UPSTREAM}, whose host is ${WEB_UPSTREAM_HOST}; the upstream must address the API Service ${OB_API_SERVICE_NAME}"

      # The port of the upstream must be the port that Service declares.
      #
      # The extraction is ANCHORED on purpose: the address is `host:port`
      # and nothing else, so the whole address has to match for a port to be
      # extracted. The previous expression (`s#^https?://[^:/]+:([0-9]+).*$#\1#`)
      # left the WHOLE URL in place when the address carried no port, so the
      # "carries no port" branch below could never trigger and the message
      # named the URL as the port ("whose port is http://opsbridge-api").
      # `sed -n ... p` prints nothing when the pattern does not match, which
      # is what makes the missing port detectable.
      WEB_UPSTREAM_PORT=$(printf '%s' "$WEB_UPSTREAM" \
        | sed -nE 's#^https?://[^:/]+:([0-9]{1,5})(/.*)?$#\1#p' || true)
      if [ -z "$WEB_UPSTREAM_PORT" ]; then
        err "Deployment/opsbridge-web sets API_UPSTREAM to ${WEB_UPSTREAM}, which carries no port; a ClusterIP only answers the ports its Service declares (Service/opsbridge-api declares port ${OB_API_SERVICE_PORT})"
      else
        [ "$WEB_UPSTREAM_PORT" = "$OB_API_SERVICE_PORT" ] \
          || err "Deployment/opsbridge-web sets API_UPSTREAM to ${WEB_UPSTREAM}, whose port is ${WEB_UPSTREAM_PORT}, but Service/opsbridge-api declares port ${OB_API_SERVICE_PORT}; a ClusterIP only answers the ports its Service declares, so the container port (8080) is dropped by kube-proxy"
      fi
    fi
  fi

  if [ -n "$WEB_SERVICE" ]; then
    # 10j. The Service stays inside the cluster; exposure is the HTTPRoute's
    #      job, and the HTTPRoute's job is the Gateway's.
    printf '%s' "$WEB_SERVICE" | grep -q "type: LoadBalancer" \
      && err "Service/opsbridge-web is exposed as LoadBalancer (public IP)"
    # 10k. 80 -> 8080: the Service is where the two ports are joined, because
    #      every backendRef in this tree addresses a Service on port 80.
    printf '%s' "$WEB_SERVICE" | grep -Eq '^[[:space:]]+port: 80$' \
      || err "Service/opsbridge-web does not expose port 80 (every backendRef in this tree addresses port 80)"
    printf '%s' "$WEB_SERVICE" | grep -Eq '^[[:space:]]+targetPort: 8080$' \
      || err "Service/opsbridge-web does not target port 8080 (the container listens on 8080)"
    printf '%s' "$WEB_SERVICE" | grep -q "app: opsbridge-web" \
      || err "Service/opsbridge-web does not select app: opsbridge-web"
  fi

  if [ -n "$WEB_ROUTE" ]; then
    # 10l. The route must point at THIS web Service. The check looks inside
    #      the backendRefs block, so a route whose metadata.name still says
    #      "opsbridge-web" but whose backendRef points somewhere else is
    #      caught — a repo-wide grep on the name would not catch it.
    printf '%s' "$WEB_ROUTE" | grep -q "backendRefs:" \
      || err "HTTPRoute/opsbridge-web declares no backendRefs"
    printf '%s' "$WEB_ROUTE" | grep -A3 "backendRefs:" | grep -q "name: opsbridge-web" \
      || err "HTTPRoute/opsbridge-web does not reference the web Service opsbridge-web"
    printf '%s' "$WEB_ROUTE" | grep -A3 "backendRefs:" | grep -Eq '^[[:space:]]+port: 80$' \
      || err "HTTPRoute/opsbridge-web does not address the web Service on port 80"
    # 10m. No wildcard hostname: a wildcard would expose this application to
    #      EVERY hostname the shared gateway routes, which is a
    #      tenant-isolation question this repository has not answered.
    printf '%s' "$WEB_ROUTE" | grep -Eq '^[[:space:]]*- .*\*' \
      && err "HTTPRoute/opsbridge-web uses a wildcard hostname"
    # 10n. The route must be attached to the shared Gateway, like every other
    #      route in this tree.
    printf '%s' "$WEB_ROUTE" | grep -q "parentRefs:" \
      || err "HTTPRoute/opsbridge-web declares no parentRefs (it would never be attached to the gateway)"
  fi

  # 10o. The web set is cross-referenced with the same object gate the prod
  #      overlay gets in section 8: resources declared, volumes declared,
  #      selector matches the pod labels, no LoadBalancer.
  if ./scripts/validate-objects.py "$WEB_RENDERED"; then
    :
  else
    err "opsbridge-web object cross-reference failed (see FAIL lines above)"
  fi
fi

if [ "$fail" -ne 0 ]; then
  echo "== RESULT: FAIL ==" >&2
  exit 1
fi
echo "== RESULT: PASS (rendered prod overlay is deployable, the OpsBridge API artefact set satisfies the same migration contract, and the OpsBridge web artefact set satisfies the ADR-020 deployment contract) =="
