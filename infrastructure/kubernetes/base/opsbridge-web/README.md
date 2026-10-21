# OpsBridge web — deployment contract

Artefact set for the OpsBridge browser front end (`src/OpsBridge.Web`):

| File | Object | Purpose |
|---|---|---|
| `deployment.yaml` | `Deployment/opsbridge-web` | the runtime workload (serves the bundle, proxies `/api`) |
| `service.yaml` | `Service/opsbridge-web` | ClusterIP in front of the workload |
| `httproute.yaml` | `HTTPRoute/opsbridge-web` | the Gateway API route (self-contained, not in any overlay) |
| `kustomization.yaml` | — | renders the set on its own |
| `../../../../docs/adr/020-*.md` | ADR-020 | the decision and the accepted risk |

The decision itself (and the risk this set does **not** remove) is recorded in
[ADR-020](../../../../docs/adr/020-opsbridge-web-deployment-and-client-token-risk.md);
this file states how the manifests implement it.

The web tier is **not** part of `infrastructure/kubernetes/base/kustomization.yaml`
and therefore not part of any overlay. Its image exists in no registry yet, and
the prod overlay pins every image by digest (ADR-011) — see
["Adoption steps"](#6-adoption-steps-in-this-order).

---

## 1. The image contract

The image (`Dockerfile.web`, written by another slice) is built to this
contract. Every line here is a requirement on the image, and the rules in
`scripts/validate-manifests.sh` section 10 hold the *manifest* to the same
contract, so the two sides cannot drift apart silently.

| Requirement | Why |
|---|---|
| Listens on **8080** | A non-root process cannot bind a privileged port (<1024). 80 is the *Service* port, 8080 is the *container* port. |
| Runs as **uid 1654** | The tree's identity (ADR-007). The manifest declares it so kubelet enforces it. |
| Writes to **exactly three paths**: `/tmp/nginx/conf.d`, `/tmp/nginx/cache`, `/tmp/nginx/run` | `readOnlyRootFilesystem: true` means every path the process writes to has to be a mounted volume. Section 3 lists them and says why each one exists; the contract test fails when the image and this manifest disagree about the set. |
| `/api/*` → `${API_UPSTREAM}`, **request URI unchanged** | The client calls `/api/v1/...` (`src/OpsBridge.Web/src/lib/api-client.ts:19`); the API must see the path it routes on. |
| every other path → `index.html` | Client-side routing: `/invoices` must return the shell, not a 404. |
| `/healthz` → **200** | The probe target. It is answered by the image itself, not proxied. |
| access/error logs → **stdout/stderr** | The pod has no writable log directory, and a container log that does not reach the collector is a log nobody has. |
| **no `root`** in the image, and the `USER` directive must be 1654 | The pod's `runAsNonRoot: true` refuses to start a root process. |

### The routes the image must serve

```
GET /api/v1/...   ->  ${API_UPSTREAM}/api/v1/...   (URI unchanged, headers forwarded)
GET /invoices     ->  index.html                   (SPA fallback)
GET /healthz      ->  200                          (answered by the image)
```

## 2. Ports, Service and route

| Thing | Value | Where it is joined |
|---|---|---|
| container port | `8080` | `deployment.yaml` |
| Service port | `80` | `service.yaml` (`targetPort: 8080`) |
| HTTPRoute backendRef | `opsbridge-web:80` | `httproute.yaml` |
| API upstream | `http://opsbridge-api:80` | `deployment.yaml` (`API_UPSTREAM`) |

**One rule, one place.** The Service is where `80` and `8080` are joined; every
`backendRef` in this tree addresses a Service on port `80`, and the container
listens on `8080` because it cannot bind `80`.

### The upstream port is the SERVICE port, not the container port

`API_UPSTREAM=http://opsbridge-api:80` is correct; `http://opsbridge-api:8080`
is **not** — a ClusterIP only listens on the ports its Service declares, so a
connection to `:8080` is dropped by kube-proxy even though the pod is listening
there. `Service/opsbridge-api` declares `port: 80` → `targetPort: 8080`.

**The image's own default is the CONTAINER port (`http://opsbridge-api:8080`),
and that is not a contradiction — it is a different world:**

| Where the web tier runs | What the upstream is | The port to use | Why |
|---|---|---|---|
| Kubernetes (this manifest) | the `opsbridge-api` **ClusterIP Service** | **80** (the Service's `port`) | a ClusterIP answers only the ports its Service declares; `Service/opsbridge-api` declares `port: 80` → `targetPort: 8080` |
| Docker-to-Docker (the CI `opsbridge-stack-smoke` job) | the API **container** | **8080** (the container's listening port) | a container is reachable on the port it listens on; `Dockerfile.opsbridge` exposes 8080 |

So: **a ClusterIP address uses the Service port; a container address uses the
container port.** The manifest sets `API_UPSTREAM` explicitly rather than
inheriting the image's default, because the image's default is the container
port and would be wrong here. The distinction is asserted, not just described:
the validator (`scripts/validate-manifests.sh`, rule 10p) fails the manifest
when `API_UPSTREAM`'s port is not the port the API's own Service declares, and
`scripts/test-validate-manifests.py` has the negative control that proves the
rule can fail.

## 3. Exactly which paths must be writable

`readOnlyRootFilesystem: true` is a container-level setting, and nginx needs
write access to a small, known set of paths. Each one is provided here as an
`emptyDir`, so nothing is written to the node and nothing survives the pod.

**The paths are the ones the IMAGE declares**, not the ones the stock nginx
image happens to use. `Dockerfile.web` creates them at build time and chowns
them to `opsbridge:opsbridge` (uid/gid 1654):

| Path | Why nginx needs it | How it is provided |
|---|---|---|
| `/tmp/nginx/conf.d` | The image renders its server block there at start-up: `NGINX_ENVSUBST_OUTPUT_DIR=/tmp/nginx/conf.d`, and the nginx image's entrypoint writes the envsubst output into it. `/etc/nginx` is not writable with a read-only root filesystem, so this is where the output has to go. | `emptyDir` `nginx-conf-d` |
| `/tmp/nginx/cache` | `nginx.conf` points `client_body_temp_path`, `proxy_temp_path`, `fastcgi_temp_path`, `uwsgi_temp_path` and `scgi_temp_path` under it. nginx **creates** those directories at start-up and refuses to start if it cannot. | `emptyDir` `nginx-cache` |
| `/tmp/nginx/run` | `nginx.conf` writes its pid file to `/tmp/nginx/run/nginx.pid`. nginx creates the pid file's directory if it is missing, and refuses to start if it cannot write the file. | `emptyDir` `nginx-run` |

#### The two failures a wrong writable-path set produces

Both strings below were **measured**, not guessed: each one was reproduced by
running `opsbridge-web:stack-smoke` with that path missing.

| What is wrong | The error nginx prints |
|---|---|
| `/tmp` is mounted **wholesale** (the mistake this manifest used to make), so the image's whole `/tmp/nginx` tree is gone and `/tmp/nginx/cache` is **not there at all**. nginx has to create its temp directories and the `mkdir` fails on the missing parent. | `mkdir() "/tmp/nginx/cache/client_body" failed (2: No such file or directory)` |
| The `/tmp/nginx/run` **volume is missing**, so the pid file is written into the image's own (read-only) directory. nginx does not need to create the directory here — it exists — but it cannot write into it. | `[emerg] open() "/tmp/nginx/run/nginx.pid" failed (30: Read-only file system)` |

The two errnos differ, and the difference is not an accident:

* nginx **creates** its temp directories (`mkdir`), so a **missing** parent is
  `ENOENT` — `(2: No such file or directory)`;
* nginx only **opens** the pid file (`open`), so a directory that **exists but
  is not writable** is `EROFS` — `(30: Read-only file system)`.

One more variant, for completeness: if `/tmp/nginx/cache` is not mounted but
`/tmp` itself is left alone, the image's own (read-only) `cache` directory is
used and the same line reads
`mkdir() "/tmp/nginx/cache/client_body" failed (30: Read-only file system)`.

Reproduce them with the image (no cluster needed):

```bash
# /tmp mounted wholesale -> the cache directory is absent
docker run --rm --read-only --user 1654:1654 --tmpfs /tmp:uid=1654,gid=1654,mode=0770 \
  -e API_UPSTREAM=http://127.0.0.1:9999 opsbridge-web:stack-smoke
#   nginx: [emerg] mkdir() "/tmp/nginx/cache/client_body" failed (2: No such file or directory)

# the /tmp/nginx/run volume missing -> the image's own read-only directory
docker run --rm --read-only --user 1654:1654 \
  --tmpfs /tmp/nginx/conf.d:uid=1654,gid=1654,mode=0770 \
  --tmpfs /tmp/nginx/cache:uid=1654,gid=1654,mode=0770 \
  -e API_UPSTREAM=http://127.0.0.1:9999 opsbridge-web:stack-smoke
#   nginx: [emerg] open() "/tmp/nginx/run/nginx.pid" failed (30: Read-only file system)
```

### Why the three are mounted SEPARATELY — and why `/tmp` must NOT be mounted

Mounting `/tmp` wholesale is **not** an equivalent way of saying the same
thing, and it is the mistake this manifest used to make:

* an `emptyDir` mounted at `/tmp` **replaces the image's whole `/tmp` tree**.
  The three directories the image created at build time — and chowned to uid
  1654 — are gone, and the container is left with a volume it would have to
  create them in itself, which is exactly what `readOnlyRootFilesystem: true`
  forbids;
* mounting each path separately hides only that path, and the `emptyDir` that
  replaces it is writable by uid 1654 because of `fsGroup` below.

`/var/cache/nginx` and `/var/run` are the **stock** nginx image's paths. This
image does not use them (`nginx.conf` moved the temp paths and the pid file
under `/tmp/nginx`), so mounting them would give the container writable
directories it never writes to — and hide nothing that matters.

**What must NOT be writable:** the root filesystem (that is the point of
`readOnlyRootFilesystem: true`), and `/var/log/nginx` — the image must log to
stdout/stderr, and a writable log directory would silently swallow the
container's output.

`fsGroup: 1654` + `runAsGroup: 1654` are what make the three `emptyDir`s
writable by the nginx processes: kubelet makes an `emptyDir` group-writable for
`fsGroup`, and the processes are in that group.

### The contract is enforced, not just described

`src/OpsBridge.Web/contract/image-manifest-contract.test.mjs` reads
`Dockerfile.web`, works out which paths the image creates and owns, and
compares them with the `mountPath`s of this Deployment. It fails in **both**
directions:

* a `mountPath` that the image does not declare (a stray mount, or `/tmp`
  mounted wholesale) is a failure;
* a writable path the image declares that the manifest does not mount is a
  failure.

**Every container is checked, not only the first one.** A sidecar that mounts
`/tmp` wholesale runs in the same pod and gets the same volumes, so it breaks
the contract exactly as much as a broken main container does — and the first
version of the test only read the FIRST `volumeMounts:` block, so it reported
"4 pass" for a manifest that cannot start. The test now scans every
`containers:` list, every container in it and every `volumeMounts:` block of
every container, and a sidecar with a bad mount is a negative control in the
test file itself.

The writable set is derived from **two** independent facts of the image — the
`mkdir -p` list and the `chown` target — not from the `chown` alone. A
narrowed `chown` (together with a documentation comment narrowed to match)
would otherwise have shrunk the contract silently; now it is a hard failure.

Run it with `npm run test:contract` in `src/OpsBridge.Web`.

### And the manifests are validated against the Kubernetes schema catalog

The contract test above checks the CONTRACT of this set (the names, the
ports, the mounts, the hardening). It cannot check that the manifests are
*valid* — a typo in a field name, a wrong type, an unknown key is invisible to
a grep and fatal at apply time. `kubeconform -strict` checks that, and the
`manifest-validation` CI job runs it over the prod overlay, the OpsBridge API
set and this set.

The **Gateway API** CRDs (`HTTPRoute`) are not in the Kubernetes schema
catalog, so a second schema location is required — without it `kubeconform`
exits 1 on every `HTTPRoute` ("could not find schema"). Locally:

```bash
kubectl kustomize infrastructure/kubernetes/base/opsbridge-web > /tmp/web.yaml
kubeconform -strict -summary \
  -schema-location default \
  -schema-location 'https://raw.githubusercontent.com/datreeio/CRDs-catalog/501527ab9c40c827144c8f45945b25a023e344ce/{{ .Group }}/{{ .ResourceKind }}_{{ .ResourceAPIVersion }}.json' \
  /tmp/web.yaml
# Summary: 3 resources found in 1 file - Valid: 3, Invalid: 0, Errors: 0, Skipped: 0
```

The catalog location is pinned to a commit, so the gate cannot start failing
(or passing) because an upstream branch moved.

## 4. `API_UPSTREAM` is an address, not a credential

`API_UPSTREAM` names where the API is. It is a **plain in-cluster address** and
it is set as a literal value:

```yaml
- name: API_UPSTREAM
  value: http://opsbridge-api:80
```

It is deliberately **not** a Secret reference. A Secret in the web pod would be
a credential the web tier does not need, and the web tier is the tier that
holds a bearer token in `localStorage` (see ADR-020) — it should hold as few
credentials as possible.

The validator (`scripts/validate-manifests.sh`, rule 10i) rejects an
`API_UPSTREAM` that is absent, that comes from a `valueFrom`, that carries
credential material (userinfo, a token, a key), or that is not a plain
`scheme://host[:port]` address. Each of those has a negative control in
`scripts/test-validate-manifests.py`.

## 5. Security headers the image must set

The image (not the manifest — nginx sets response headers, Kubernetes does not)
must send, on every response. The values below are **exactly** what the image
sends today, read off a running container
(`curl -sI http://127.0.0.1:18081/` against `opsbridge-web:stack-smoke`):

| Header | Value |
|---|---|
| `Content-Security-Policy` | `default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; frame-src 'none'; form-action 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; manifest-src 'self'; worker-src 'self'` |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `no-referrer` |
| `X-Frame-Options` | `DENY` (legacy fallback for `frame-ancestors 'none'`) |
| `Permissions-Policy` | `geolocation=(), camera=(), microphone=(), payment=(), usb=()` |

**This policy is STRICTER than the one this document used to claim.** The
earlier version of this table listed `style-src 'self' 'unsafe-inline'` and
omitted `frame-src 'none'`, `form-action 'self'`, `manifest-src 'self'` and
`worker-src 'self'`. The image never sent that: it sends
`style-src 'self'` — **no** `'unsafe-inline'` — plus those four extra
directives. The documentation was understating the shipped strictness, which
is the wrong way round: a reader who trusted the table would have believed the
browser allowed inline styles, and would have had no idea that framing, form
submission, the web manifest and the service worker are all denied.

Two properties are worth stating explicitly, because they are the ones people
assume wrongly:

* `script-src 'self'` — **no** `'unsafe-inline'`, **no** `'unsafe-eval'` — and
  `style-src 'self'` — **no** `'unsafe-inline'` either. The built bundle loads
  no inline script and no inline style, so the strict policy works. There is
  **no** `'unsafe-inline'` anywhere in the policy the image sends; a change
  that added one would change the contract this table states.
* `frame-src 'none'` and `frame-ancestors 'none'` (plus `X-Frame-Options:
  DENY` as the legacy fallback), `form-action 'self'` and `object-src 'none'`
  deny the three things a compromised or injected page would want most: framing
  the app, posting a form to another origin, and embedding a plugin object.

What this buys and what it does not is stated plainly in ADR-020: it raises the
cost of an injection, it does **not** remove the exposure of a bearer token
stored in `localStorage`.

## 6. Adoption steps (in this order)

This directory is **deliberately not referenced** by
`infrastructure/kubernetes/base/kustomization.yaml`, and therefore not by
`infrastructure/kubernetes/overlays/prod`. The image does not exist in any
registry yet, and the prod overlay pins every image by digest (ADR-011): adding
a digest-pinned placeholder for an image that was never pushed would turn every
rollout of the *existing* FlashSale services into an `ImagePullBackOff`. The
HTTPRoute is held back for the same reason, plus one: a route that points at a
workload that cannot start sends real north-south traffic to nothing.

Do these steps in order:

1. **Build and push the image.** `Dockerfile.web` is the source of truth for
   the artefact. In the CI release job, add the counterpart of the existing
   `order-api` / `order-worker` / `payment-service` steps: build with
   `file: ./Dockerfile.web`, push as `<acr-host>/opsbridge-web:<commit-sha>`,
   and resolve the digest the same way the other three images are resolved.
   Add a Trivy image-scan step for it — an image nobody has looked at is an
   image nobody has looked at.
2. **Pin the digest in the prod overlay.** Add to the `images:` list of
   `infrastructure/kubernetes/overlays/prod/kustomization.yaml`:
   ```yaml
   - name: opsbridge-web
     newName: <acr-host>/opsbridge-web
     newTag: <40-char-digest-or-sha>
   ```
   and add `opsbridge-web` to the `WANTED` tuple in
   `scripts/gitops-pin-overlay.py`, so the GitOps job pins it on every release
   instead of leaving it behind.
3. **Wire the directory into the overlay.** Add `../base/opsbridge-web` to the
   `resources:` list of the prod overlay, and remove the "self-contained" note
   from `kustomization.yaml`. This is the step that brings the
   `HTTPRoute/opsbridge-web` into existence for that environment — the route
   must not be applied before the image can be pulled, or the gateway will
   send traffic to a pod that never starts.
4. **Set the hostname for the environment.** The base ships
   `opsbridge.prod`; the local overlay needs `opsbridge.local`. Use a
   `patches:` entry on the HTTPRoute (or an overlay-local copy of
   `httproute.yaml`) — and do **not** use a wildcard hostname: the validator
   rejects one, and the shared gateway would otherwise expose this application
   to every hostname it routes.
5. **Enable the Deployment and the Service.** Apply the set, then check that
   the pods become Ready:
   ```bash
   kubectl apply -k <overlay-that-includes-this-directory>
   kubectl rollout status deployment/opsbridge-web --timeout=5m
   kubectl logs -l app=opsbridge-web --tail=50
   ```
   A pod that is `CrashLoopBackOff` with
   `mkdir() "/tmp/nginx/cache/client_body" failed (2: No such file or directory)`
   means `/tmp` is mounted wholesale (or the cache path is not there at all),
   and `[emerg] open() "/tmp/nginx/run/nginx.pid" failed (30: Read-only file system)`
   means the `/tmp/nginx/run` volume is missing — either way the writable paths
   in section 3 are wrong for the image, so re-check them (and run the
   contract test) before anything else.
6. **Check the proxy, from outside the pod.** The whole point of the tier is
   that `/api/v1/...` is served by the SAME origin as the bundle:
   ```bash
   kubectl port-forward svc/opsbridge-web 8080:80
   curl -i http://localhost:8080/healthz            # 200
   curl -i http://localhost:8080/api/v1/auth/me     # 401 (no token) — NOT a CORS error
   curl -i http://localhost:8080/invoices            # 200 + index.html
   ```
   A `404` on `/api/v1/...` means the image's `location` block is missing; a
   `502` means `API_UPSTREAM` does not resolve (check the Service name, the
   port, and the namespace).

7. **Only then, expose it.** The HTTPRoute is applied with the overlay in
   step 3. Before the web tier faces public internet traffic, the two
   prerequisites in section 7 have to be done — they are not optional and
   they are not part of this slice.

   **In the same change as step 3**, add `opsbridge-web` to the `WANTED` tuple
   in `scripts/gitops-pin-overlay.py` (step 2) and check whether
   `scripts/validate-objects.py`'s `SECRET_CONTRACT` needs an entry: this set
   references **no Secret at all**, so it does not — but if the set is ever
   given one, that is where the name and its keys must be registered.

## 7. What this set does NOT cover

Stated explicitly, because a manifest set that looks complete and is not is
worse than no manifest set:

* **The image and the nginx configuration.** They are written by another
  slice, to the contract in section 1. The **volume contract** between the two
  is proven by the contract test (`src/OpsBridge.Web/contract/image-manifest-contract.test.mjs`),
  but nothing in this directory proves the image honours the rest of the
  contract — the browser E2E suite does.
* **A public-internet-safe deployment.** The web client stores its bearer
  token in `localStorage` (`src/OpsBridge.Web/src/lib/api-client.ts`), so any
  XSS in the application can read it. This deployment **must not** be
  described as safe for public exposure before the token moves to an HttpOnly,
  Secure, SameSite cookie with CSRF protection and server-side authorization
  (ADR-020, "The accepted risk"). It is safe for a trusted-network or
  internal audience today, and the security headers above raise the cost of an
  injection without removing the exposure.
* **Real authentication.** The login endpoint is Development/Test only and
  lets the client assert its own role (`src/OpsBridge.Api/Endpoints/AuthEndpoints.cs`).
  The role check in the browser is UI gating, not a security control —
  server-side authorization is what enforces roles, and that is proven by the
  .NET suites, not by the browser.
* **A namespace for the environment.** The base renders into `opsbridge`; the
  prod overlay's namespace is its own business.
* **The `HTTPRoute` in any overlay.** It is self-contained here, and is
  applied in the same change that pins the image digest (step 3).
