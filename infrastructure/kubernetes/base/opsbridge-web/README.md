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

**The upstream port is the SERVICE port, not the container port.**
`API_UPSTREAM=http://opsbridge-api:80` is correct; `http://opsbridge-api:8080`
is **not** — a ClusterIP only listens on the ports its Service declares, so a
connection to `:8080` is dropped by kube-proxy even though the pod is listening
there. `Service/opsbridge-api` declares `port: 80` → `targetPort: 8080`.

## 3. Exactly which paths must be writable

`readOnlyRootFilesystem: true` is a container-level setting, and nginx needs
write access to a small, known set of paths. Each one is provided here as an
`emptyDir`, so nothing is written to the node and nothing survives the pod:

| Path | Why nginx needs it | How it is provided |
|---|---|---|
| `/var/cache/nginx` | The stock nginx image puts `client_body_temp_path`, `proxy_temp_path`, `fastcgi_temp_path`, `uwsgi_temp_path` and `scgi_temp_path` under it. nginx creates those directories at startup and **refuses to start** if it cannot: `mkdir() "/var/cache/nginx/client_temp" failed (30: Read-only file system)`. | `emptyDir` `nginx-cache` |
| `/var/run` | The stock `nginx.conf` writes its pid file to `/var/run/nginx.pid`; with a read-only root filesystem that write fails and nginx exits: `[emerg] open() "/var/run/nginx.pid" failed (30: Read-only file system)`. An image whose config sets `pid /tmp/nginx.pid` does not need this mount — having it is harmless. | `emptyDir` `nginx-run` |
| `/tmp` | The conventional scratch path, and the fallback pid location. | `emptyDir` `tmp` |

**What must NOT be writable:** the root filesystem (that is the point of
`readOnlyRootFilesystem: true`), and `/var/log/nginx` — the image must log to
stdout/stderr, and a writable log directory would silently swallow the
container's output.

`fsGroup: 1654` + `runAsGroup: 1654` are what make the three `emptyDir`s
writable by the nginx processes: kubelet makes an `emptyDir` group-writable for
`fsGroup`, and the processes are in that group.

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
must send, on every response:

| Header | Value |
|---|---|
| `Content-Security-Policy` | `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'` |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `X-Frame-Options` | `DENY` (legacy fallback for `frame-ancestors 'none'`) |
| `Permissions-Policy` | `geolocation=(), camera=(), microphone=(), payment=()` |

`script-src 'self'` — **no** `'unsafe-inline'`, **no** `'unsafe-eval'` — is the
part that matters. `style-src` may keep `'unsafe-inline'` because inline styles
are common in React component libraries and a stylesheet cannot execute script.

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
   `mkdir() "/var/cache/nginx/client_temp" failed (30: Read-only file system)`
   means the writable paths in section 3 are wrong for the image — re-check
   them before anything else.
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
  slice, to the contract in section 1. Nothing in this directory proves the
  image honours it — the browser E2E suite does.
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
