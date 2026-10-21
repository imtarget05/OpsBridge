# OpsBridge API — deployment contract

Artefact set for the OpsBridge bounded context (`src/OpsBridge.Api`):

| File | Object | Purpose |
|---|---|---|
| `deployment.yaml` | `Deployment/opsbridge-api` | the runtime workload (serves HTTP) |
| `service.yaml` | `Service/opsbridge-api` | ClusterIP in front of the workload |
| `migration-job.yaml` | `Job/opsbridge-migrate` | the dedicated schema step (`--migrate`) |
| `../../../../Dockerfile.opsbridge` | image | builds `OpsBridge.Api.dll` |

The schema contract itself is decided in
[ADR-018](../../../../docs/adr/018-opsbridge-migration-and-legacy-baseline-strategy.md);
this file states how the manifests implement it.

---

## 1. What the Job does

`Job/opsbridge-migrate` runs **the same image as the Deployment** with the
argument `--migrate`, i.e. `dotnet OpsBridge.Api.dll --migrate`.

The EF Core migrations are compiled into the API assembly, so the schema step and
the code that runs against the schema are guaranteed to be the same build. The
Job applies every pending migration and exits with ADR-018's exit-code contract:

| Exit code | Meaning | Effect on the rollout |
|---|---|---|
| `0` | success — **including** "the schema was already at head" | the Deployment may roll |
| `1` | failure (unreachable database, refused credentials, timeout, broken migration) | the Deployment must NOT roll |
| `2` | drift / unknown migration state (reported by `--baseline-legacy`) | the Deployment must NOT roll |

"Already at head" is a success on purpose: deployment steps are re-run on every
release and on every retry, so the second run must not fail the pipeline.

### Ordering contract

> **The Job brings the schema to head, and only then may the API roll.**

Kubernetes has no "run this Job before those Deployments" primitive inside a
single `apply`, so the ordering is expressed the same way the FlashSale tree
expresses it (`base/migration/job.yaml`): the Job is applied and awaited to
completion, and the Deployment is applied afterwards. The Job's own spec makes
the ordering *safe* to retry:

* `completionMode: NonIndexed` and a fixed Job name — a re-sync does not stack a
  second concurrent migration;
* `restartPolicy: Never` — the migration is never rescheduled onto a second node
  while the first attempt may still hold the EF migration lock;
* `backoffLimit: 3` — a broken migration is not retried forever;
* `ttlSecondsAfterFinished: 3600` — the finished pod is garbage collected after
  an hour of readable evidence.

EF Core 10's own migration locking (`LOCK TABLE "__EFMigrationsHistory" IN ACCESS
EXCLUSIVE MODE`, inside the migration transaction) is what makes two concurrent
runs safe if they do happen: exactly one applies the DDL, the others find the
schema at head. ADR-018 deliberately adds **no** advisory lock of its own.

## 2. What identity the Job needs

The Job needs a **DDL-capable** PostgreSQL role: it creates tables, primary keys,
foreign keys and EF's `__EFMigrationsHistory` table.

* Secret: `opsbridge-migrate-secrets`
* Key: `pg-migration-connection`
* Environment variable: `ConnectionStrings__OpsBridgeDb`

That role needs `CREATE` on the target database (and `CREATE` on the `public`
schema). For the **legacy-baseline** tool (`--baseline-legacy`, a separate,
operator-triggered run — NOT part of this Job) the role additionally needs
**`CREATEDB`** on the server, because the baseline creates a scratch database to
compare against. See ADR-018 §4.

The Job is given **no application credential at all**:

* no JWT signing key — the `--migrate` branch in `src/OpsBridge.Api/Program.cs`
  runs *before* the JWT validation block, so a key-less schema step is a
  schema step that does not need auth configuration;
* no Redis, no RabbitMQ — OpsBridge has neither, and a schema step must not be
  blocked by a broker;
* no `OpsBridge__AutoMigrate` — that switch is off by default and the
  application *refuses to start* outside Development/Test when it is on.

## 3. What identity the runtime needs

The Deployment's role needs **DML only**: `SELECT`, `INSERT`, `UPDATE`,
`DELETE`. It must **not** be able to change the schema — otherwise every rollout
would be a schema event, which is exactly what ADR-018 forbids.

* Secret: `opsbridge-secrets`
* Key `pg-connection` → `ConnectionStrings__OpsBridgeDb` (the DML role)
* Key `jwt-signing-key` → `Jwt__SigningKey` (the API's own auth configuration)

The two credentials live in **two different Secrets on purpose**. The runtime pod
can never read the DDL credential, and the migration Job never reads the runtime
credential. `scripts/validate-manifests.sh` (section 9) asserts both directions,
so the separation cannot be broken by an edit that "just points at the other
secret".

The gate asserts the separation on the **credential (the Secret key)**, not
only on the Secret's *name*, because a name is only a label: a runtime pod
handed `pg-migration-connection` under the runtime Secret's name holds the DDL
capability, whatever the Secret is called. Section 9 of the validator rejects
the DDL key anywhere in the runtime Deployment and the DML key anywhere in the
migration Job, in both directions, and `scripts/test-validate-manifests.py`
carries a negative control for each of those cross-wirings.

## 4. PostgreSQL version requirement

**PostgreSQL 13 or newer.**

The runtime API only needs a supported PostgreSQL, but the `--baseline-legacy`
tool drops its scratch database with `DROP DATABASE … WITH (FORCE)`, and the
`FORCE` clause requires PostgreSQL 13+. Since both tools ship in the same
artefact, 13+ is the minimum supported server version for the OpsBridge
deployment as a whole. See ADR-018 §4 ("Privileges, and the scratch database").

## 5. Adoption steps (in this order)

This directory is **deliberately not referenced** by
`infrastructure/kubernetes/base/kustomization.yaml`, and therefore not by
`infrastructure/kubernetes/overlays/prod`. The image does not exist in any
registry yet, and the prod overlay pins every image by digest (ADR-011): adding
a digest-pinned placeholder for an image that was never pushed would turn every
rollout of the *existing* FlashSale services into an `ImagePullBackOff`.

Do these steps in order:

1. **Build and push the image.**
   `Dockerfile.opsbridge` is the source of truth for the artefact. In the CI
   release job, add the counterpart of the existing `order-api` / `order-worker`
   / `payment-service` steps: build with `file: ./Dockerfile.opsbridge`, push
   as `<acr-host>/opsbridge-api:<commit-sha>`, and resolve the digest the same
   way the other three images are resolved. Add a Trivy image-scan step for it
   (`docker-build-scan` builds all three today; a fourth image that is never
   scanned is an image nobody has looked at).
2. **Pin the digest in the prod overlay.** Add to the `images:` list of
   `infrastructure/kubernetes/overlays/prod/kustomization.yaml`:
   ```yaml
   - name: opsbridge-api
     newName: <acr-host>/opsbridge-api
     newTag: <40-char-digest-or-sha>
   ```
   and add `opsbridge-api` to the `WANTED` tuple in
   `scripts/gitops-pin-overlay.py`, so the GitOps job pins it on every release
   instead of leaving it behind.
3. **Create the roles and the Secrets** in the target environment:
   * `opsbridge-migrate-secrets` / `pg-migration-connection` → the DDL role;
   * `opsbridge-secrets` / `pg-connection` → the DML role;
   * `opsbridge-secrets` / `jwt-signing-key` → the API's HS256 signing key
     (≥ 32 bytes, and **not** the built-in development key — the API refuses to
     start in Production otherwise).
   The `postgres` container in `base/data/postgres.yaml` is the in-cluster
   development data tier; a production environment uses a managed PostgreSQL.
4. **Enable the Job.** Apply `Job/opsbridge-migrate` alone and wait for it to
   reach `Complete`:
   ```bash
   kubectl apply -k <overlay-that-includes-this-directory>
   kubectl wait --for=condition=complete job/opsbridge-migrate --timeout=10m
   kubectl logs job/opsbridge-migrate
   ```
   The log must show `ExitCode=0`. A non-zero exit stops the rollout — do not
   enable the Deployment.
5. **Enable the Deployment.** Only after the Job has completed successfully:
   apply `Deployment/opsbridge-api` and `Service/opsbridge-api`. Rolling the
   Deployment before the Job has completed means serving traffic against a
   schema that may not exist yet.
6. **Wire the directory into the overlay.** Once steps 1–5 have succeeded in a
   real environment, add `../base/opsbridge-api` to the `resources:` list of the
   prod overlay and remove the "self-contained" note from
   `kustomization.yaml`. Until then the directory stays out of the base, so the
   existing deployment is unaffected.

   **In the same change**, add `opsbridge-secrets` and
   `opsbridge-migrate-secrets` to the `SECRET_CONTRACT` dict in
   `scripts/validate-objects.py` — otherwise the cross-reference gate starts
   rejecting the overlay with `unknown Secret 'opsbridge-secrets'`. The exact
   edit (and the exact keys) is in section 6 below, under
   "`scripts/validate-objects.py`'s secret contract".

## 6. What this set does NOT cover

Stated explicitly, because a manifest set that looks complete and is not is
worse than no manifest set:

* **The web front end.** `src/OpsBridge.Web` has **no Dockerfile and no
  manifest** — not in this directory, not anywhere else. It is a separate gap.
  Until it is built, the OpsBridge API is reachable only through port-forwarding
  or an ingress that someone else provides.
* **Ingress / HTTPRoute.** `Service/opsbridge-api` is ClusterIP. Nothing routes
  external traffic to it. The FlashSale tree's `overlays/*/httproute.yaml` is
  for the order API and does not cover OpsBridge.
* **The image, the digest and the Secrets.** They do not exist yet; see step 1
  and step 3 above.
* **`scripts/validate-objects.py`'s secret contract.** This is the one known
  gap that will bite whoever wires this directory into an overlay, so it is
  spelled out exactly rather than alluded to.

  * **The file:** `scripts/validate-objects.py` (repository root).
  * **The constant:** `SECRET_CONTRACT`, the module-level dict in that file.
    It currently contains exactly one entry, `flashsale-secrets`, and the
    cross-reference gate rejects any `secretKeyRef` whose Secret is not a key
    of that dict.
  * **The failure it produces:** once this directory is rendered as part of an
    overlay, `scripts/validate-objects.py <rendered overlay>` fails with
    `FAIL: Deployment/opsbridge-api/opsbridge-api: unknown Secret 'opsbridge-secrets'`
    (and `FAIL: Job/opsbridge-migrate/migrate: unknown Secret 'opsbridge-migrate-secrets'`),
    followed by
    `FAIL: object cross-reference failed` from
    `scripts/validate-manifests.sh`.
  * **What a human must add** to `SECRET_CONTRACT` in
    `scripts/validate-objects.py` before the overlay wiring is complete:
    ```python
    SECRET_CONTRACT = {
        "flashsale-secrets": {
            "pg-connection", "pg-password", "redis-connection",
            "rabbitmq-connection", "rabbitmq-password", "jwt-signing-key",
        },
        # OpsBridge (ADR-018): the runtime (DML) Secret and the DDL Secret are
        # deliberately two different Secrets — see section 2 and 3 above.
        "opsbridge-secrets": {"pg-connection", "jwt-signing-key"},
        "opsbridge-migrate-secrets": {"pg-migration-connection"},
    }
    ```
    The key lists must match the references in `deployment.yaml` and
    `migration-job.yaml` exactly: the runtime pod reads `pg-connection` and
    `jwt-signing-key` from `opsbridge-secrets`, and the migration Job reads
    `pg-migration-connection` from `opsbridge-migrate-secrets`.

  Until that edit is made, `scripts/validate-manifests.sh` (section 9) is the
  gate that covers this set — it renders the directory on its own and asserts
  the whole migration contract, including the DDL/DML identity separation, and
  it does not depend on `SECRET_CONTRACT`. Note that `validate-objects.py`
  cross-checks the SECRET NAMES and their keys, but it cannot check the
  identity separation on its own: the name-based contract cannot tell that
  `pg-migration-connection` must never reach the runtime workload. That
  assertion lives in `scripts/validate-manifests.sh` section 9, which is why
  both gates exist.
