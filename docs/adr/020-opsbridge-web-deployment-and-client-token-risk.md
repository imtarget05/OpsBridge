# ADR-020: OpsBridge web deployment and the client-side token risk

- **Status**: Accepted
- **Date**: 2026-10-21
- **Supersedes**: nothing. Extends ADR-017 (OpsBridge bounded context) and is
  independent of ADR-018 (migration strategy).

## Context

`src/OpsBridge.Web` is a Vite/React application that has only ever run behind
`npm run dev` on port 5173, with the Vite dev server proxying `/api` to the
API. It had no image, no manifests and no deployment story: the browser
journey in `src/OpsBridge.Web/e2e/` was driven against a locally started dev
server, and the only thing the Kubernetes tree knew about the front end was
nothing at all.

The API side had just been given a real deployment
(`infrastructure/kubernetes/base/opsbridge-api/`, ADR-018). The web side had
not. That asymmetry is what this ADR closes.

Three facts about the client decide the shape of the deployment.

**1. The client uses a relative base URL.**

```ts
// src/OpsBridge.Web/src/lib/api-client.ts:19
const API_BASE = '/api/v1';
```

Every call is `fetch(`${API_BASE}${endpoint}`)`. The client has no idea what
host the API lives on, and it does not need one: `/api/v1/...` is resolved
against whatever origin served the page.

**2. The client stores its bearer token in `localStorage`.**

```ts
// src/OpsBridge.Web/src/lib/api-client.ts:25,31,33
this.token = localStorage.getItem('opsbridge_token');
localStorage.setItem('opsbridge_token', token);
localStorage.removeItem('opsbridge_token');
```

`localStorage` is readable by any JavaScript running in the page's origin.
That is a fact about the web platform, not about this application, and it is
the single most important thing in this ADR — see "The accepted risk" below.

**3. The browser's role check is UI gating, not a security control.**

`src/OpsBridge.Web/src/features/auth/ProtectedRoute.tsx` hides a screen from a
role that is not allowed to see it. The role itself comes from the client:

```ts
// src/OpsBridge.Web/src/features/auth/AuthContext.tsx:42-45
const login = async (username: string, role?: UserRole) => {
  const res = await api.login(username, role);
```

and the API takes that role at face value — but only in Development and Test:

```csharp
// src/OpsBridge.Api/Endpoints/AuthEndpoints.cs:20-36
if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
{
    group.MapPost("/login", (LoginRequest request, IOpsBridgeTokenIssuer tokenIssuer) =>
    {
        // Persona selection is a local demo convenience, never production authentication.
        var role = request.Role ?? request.Username switch { ... };
```

So the persona is **client-asserted by a Development-only demo endpoint**, and
the browser's `allowedRoles` check is a convenience for the operator. It is
not a security boundary and must never be described as one: it is trivially
bypassed by anyone who can run JavaScript in the page — which, by fact 2, is
exactly the party we are worried about. Server-side authorization is what
enforces roles, and it is proven by the .NET suites, not by the browser. A
green browser journey that asserts "an Operator cannot see the invoices
screen" is evidence about the *UI*, not about authorization.

## Decision

**The built bundle is served from an nginx image that reverse-proxies `/api` to
the API, with the upstream configurable at runtime.**

- The web tier is an `nginx` image containing the built Vite bundle. It
  listens on **8080** (not 80) so the container can run as a **non-root**
  user — uid 1654, the tree's identity (ADR-007). It serves:
  * `/api/*` → `${API_UPSTREAM}`, **with the request URI unchanged** (the
    client calls `/api/v1/...`; the API must see the path it routes on);
  * every other path → `index.html` (client-side routing: a deep link such as
    `/invoices` must return the shell, not a 404);
  * `/healthz` → `200`, answered by the image itself.
- The upstream is supplied by the environment variable **`API_UPSTREAM`**
  (e.g. `http://opsbridge-api:80`), substituted by the nginx image's template
  mechanism. It is a plain in-cluster **address**, not a credential: it is set
  as a literal value in the Deployment, never read from a Secret. The web pod
  holds no Secret at all.
- **Which port the upstream names depends on what the upstream IS.** The image
  defaults to `http://opsbridge-api:8080` — the API's **container** port, which
  is the right address for a Docker-to-Docker hop (a container is reachable on
  the port it listens on) and is what the CI `opsbridge-stack-smoke` job uses.
  The **manifest** sets `http://opsbridge-api:80` — the API **Service** port,
  because a ClusterIP only answers the ports its Service declares
  (`Service/opsbridge-api`: `port: 80` → `targetPort: 8080`), and a connection
  to `:8080` would be dropped by kube-proxy even though the pod behind the
  Service is listening on 8080. The rule is one line: **a ClusterIP address
  uses the Service port; a container address uses the container port.** The
  two halves are compatible only because the manifest sets the variable
  explicitly — nothing about the image's default makes it right for
  Kubernetes — and the validator asserts that the manifest's port is the port
  the API's own Service declares.
- The **Service** is `ClusterIP`, exposing port **80 → targetPort 8080**. Port
  80 is what every `backendRef` in this tree addresses; the 80 → 8080 mapping
  is the Service's job and nowhere else.
- The workload runs with `readOnlyRootFilesystem: true`,
  `allowPrivilegeEscalation: false`, `capabilities: drop [ALL]`,
  `runAsNonRoot: true`, `runAsUser: 1654`, `runAsGroup: 1654`,
  `fsGroup: 1654`, and **exactly the three paths the image declares as
  writable** as three separate `emptyDir` volumes:
  `/tmp/nginx/conf.d` (the rendered server block), `/tmp/nginx/cache` (the
  nginx temp directories) and `/tmp/nginx/run` (the pid file). The exact paths
  and the exact nginx failure each one prevents are in
  `infrastructure/kubernetes/base/opsbridge-web/README.md` §3.
  **The three are mounted one by one, and `/tmp` is never mounted wholesale:**
  an `emptyDir` at `/tmp` *replaces* the image's whole `/tmp` tree, so the
  directories the image created and chowned at build time disappear and the
  container is left with a volume it would have to create them in itself —
  the very thing `readOnlyRootFilesystem: true` forbids. `/var/cache/nginx` and
  `/var/run` are the *stock* nginx image's paths; this image does not write to
  them, so mounting them would grant writable directories the container never
  uses. The contract is enforced in both directions by
  `src/OpsBridge.Web/contract/image-manifest-contract.test.mjs`, which reads
  `Dockerfile.web` and fails when the image and the manifest disagree about
  the set of writable paths.
- Routing is **Gateway API `HTTPRoute`**, matching the rest of this
  repository — `parentRefs: [{name: portfolio-gateway}]`, a concrete hostname
  (never a wildcard), and one rule with `backendRefs: [{name: opsbridge-web,
  port: 80}]`. One rule, because the decision "is this an API call or a
  client-side route?" is made once, in nginx; making it twice — in the
  gateway and in nginx — is how the two drift apart.

### Why same-origin proxying, and not CORS

CORS is a browser-enforced mechanism that tells a page on origin A that it may
read responses from origin B. It exists because browsers refuse to let a page
read a cross-origin response by default, and it is a *permission* granted to
the origin, not a protection of the response.

Here the proxy removes the cross-origin surface entirely: the page is served
from the same origin that serves `/api/v1/...`, so there is no second origin,
no preflight, and no CORS policy to write. The concrete consequences:

* **The CORS configuration cannot be wrong.** A permissive `Access-Control-Allow-Origin: *`
  on the API would be the classic way a browser app ends up readable from any
  site on the internet. With a proxy there is nothing to misconfigure: the
  API is never given a CORS header for the web tier at all.
* **The bearer token never crosses an origin boundary.** The token is sent to
  the same origin the page came from, so it is never attached to a request to
  a third-party origin — the class of bug where a misconfigured `allow-origin`
  hands a token to an attacker's page.
* **No preflight round trip** on `POST`/`PUT`/`DELETE` with a JSON body, so
  the UI is not paying a network round trip per write to satisfy a browser
  rule that a proxy makes unnecessary.
* **The proxy is the only thing that has to be trusted**, and it is a
  manifest: it is covered by `scripts/validate-manifests.sh` section 10, whose
  rules are themselves mutation-tested.

The cost is one more hop and one more thing to run. For a same-origin
front end, that is the cheaper trade.

### The security headers the image must set

The image — nginx, not Kubernetes — must send, on every response. The values
below are **exactly** what the image sends today, read off a running container
(`curl -sI http://127.0.0.1:18081/` against `opsbridge-web:stack-smoke`):

| Header | Value |
|---|---|
| `Content-Security-Policy` | `default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; frame-src 'none'; form-action 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; manifest-src 'self'; worker-src 'self'` |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `no-referrer` |
| `X-Frame-Options` | `DENY` (legacy fallback for `frame-ancestors 'none'`) |
| `Permissions-Policy` | `geolocation=(), camera=(), microphone=(), payment=(), usb=()` |

**This policy is STRICTER than the one this ADR used to claim.** The earlier
version of this table listed `style-src 'self' 'unsafe-inline'` and omitted
`frame-src 'none'`, `form-action 'self'`, `manifest-src 'self'` and
`worker-src 'self'`. The image never sent that: it sends `style-src 'self'`
— **no** `'unsafe-inline'` — plus those four extra directives. The
documentation was understating the shipped strictness, which is the wrong way
round for a document that is supposed to be the record of what is deployed.

**What a CSP buys.** `script-src 'self'` with **no `'unsafe-inline'` and no
`'unsafe-eval'`** means a script may only come from this origin, and inline
`<script>` blocks and `eval()` are refused by the browser. It raises the cost
of an injection: an injected `<script src="https://evil.example/x.js">` is
blocked, a stored XSS payload that depends on `eval` is blocked, and
`frame-ancestors 'none'` stops the application from being framed (clickjacking
— a real phishing primitive against an operator using a back office). It also
limits what a *compromised dependency* can exfiltrate to: `connect-src 'self'`
means a script cannot post the token to another origin.

**What a CSP does not buy.** It does **not** protect a token in
`localStorage` from an XSS that runs in this origin. A CSP constrains *where
code may come from*; it cannot stop code that is already allowed — including
code injected through a DOM sink that a `script-src` policy does not cover
(for example an `onerror` attribute or a `javascript:` URL that the application
builds). It also does not help if the application's own bundle is compromised
before the build: that code is same-origin by definition. And `'unsafe-inline'`
in `script-src` — which many Vite/React setups need and which this ADR
forbids — would reduce the policy to a speed bump.

**`style-src` does NOT keep `'unsafe-inline'`.** This ADR previously claimed
that inline styles were allowed because "inline styles are common in React
component libraries, and a stylesheet cannot execute script in a modern
browser", and that this was "a deliberate, stated trade: the script policy is
strict, the style policy is not". **That claim was false against the merged
image**: the shipped policy is `style-src 'self'`, and there is **no**
`'unsafe-inline'` anywhere in the policy the image sends. The trade-off was
never made — the image simply never allowed it.

The strict style policy works because the built bundle needs it to: Vite emits
the styles as a stylesheet, and the application uses Tailwind utility classes
rather than inline `style` attributes. If a future change ever needed an
inline style, the honest options would be to (a) move it into a CSS class, or
(b) change the policy **and this ADR and the README with it** — not to let a
document keep describing a policy the image does not send.

**`Permissions-Policy` denies five features, not four.** The image sends
`geolocation=(), camera=(), microphone=(), payment=(), usb=()` — `usb=()` was
missing from the earlier version of this table.

## The accepted risk (stated plainly)

**The web client stores its bearer token in `localStorage`. Any XSS in the
application can read it.** The security headers above raise the cost of an
injection; they do **not** remove this exposure. `localStorage` is readable by
any script running in the page's origin, and a strict `script-src` does not
make injected code stop running — it makes *injected-from-outside* code stop
running.

**Therefore: this deployment must not be described as safe for exposure to the
public internet.** It is a valid deployment for a trusted network, an internal
audience, or a pre-production environment. Before it faces untrusted traffic,
the durable fix has to be done:

* the token moves to an **HttpOnly, Secure, SameSite cookie** — HttpOnly so
  that script cannot read it, Secure so it is not sent in clear, SameSite so
  it is not attached to cross-site requests;
* **CSRF protection** follows from that move, because a cookie is sent
  automatically and a bearer header is not: the API needs an anti-CSRF token
  or an equivalent (double-submit, or an `Origin`/`Referer` check);
* **server-side authorization** is the thing that actually enforces roles, and
  the browser's role check is not it.

That work is **a separate slice**. It is not in this ADR's scope, and this ADR
does not claim it is done.

## Consequences

- The web tier has an image, a workload, a Service, a route and a deployment
  contract, and every rule of that contract is a rule the manifest gate
  enforces (`scripts/validate-manifests.sh` section 10) with a negative
  control proving it can fail
  (`scripts/test-validate-manifests.py`).
- **The image and the manifest cannot drift apart silently any more.** Two
  controls bind them: the contract test
  (`src/OpsBridge.Web/contract/image-manifest-contract.test.mjs`), which reads
  `Dockerfile.web` and fails when the Deployment's mounts are not exactly the
  writable paths the image declares, in either direction; and the validator's
  rule 10p, which fails the manifest when `API_UPSTREAM`'s port is not the
  port the API's own Service declares. The CI `opsbridge-stack-smoke` job
  rehearses the **manifest** — its security context, its three volume mounts
  and its upstream — rather than the image's own layout, so a mismatch shows
  up on the runner instead of in a cluster.
- The web set is **self-contained**: it is not referenced by
  `infrastructure/kubernetes/base/kustomization.yaml` and not by any overlay,
  because the image exists in no registry and the prod overlay pins every
  image by digest (ADR-011). The existing deployment is untouched — the
  rendered `base`, `overlays/prod` and `overlays/local` are byte-identical to
  `97473de`.
- The API needs **no CORS configuration** for the web tier, and no
  `Access-Control-Allow-Origin` header that could be misconfigured.
- A deployment that exposes the web tier without the cookie work is exposed
  to token theft by any XSS. The blast radius of an XSS in this application is
  therefore "the session of whoever was looking at the screen", not "a page
  defacement".
- The browser's role checks stay what they are: **UI gating**. Anyone reading
  the browser suite as an authorization test is reading it wrong; the
  authorization guarantee is the .NET suites'.

## Alternatives rejected

- **Serve the bundle from the API image.** The API is a .NET runtime that
  knows how to serve a static file, and it would have saved an image, a
  Deployment and a proxy. Rejected: it puts a public-facing document root
  inside the process that holds the JWT signing key and the DML database
  credential, it couples the web tier's release cadence to the API's, it makes
  the API's `readOnlyRootFilesystem` pod carry a writable static root (or
  forces the bundle into the image, where every UI change is a backend
  rebuild), and it gives the two tiers different scaling and different
  failure modes for no gain. The proxy costs one small, static, unprivileged
  container.
- **Classic Ingress instead of Gateway API `HTTPRoute`.** The repository
  already routes with `HTTPRoute` (`overlays/prod/httproute.yaml`,
  `overlays/local/httproute.yaml`) and the shared north-south gateway is a
  Gateway API gateway. A second routing mechanism would mean a second
  convention, a second set of annotations to get wrong, and a class of
  controller-specific behaviour (path rewriting, regex paths) that the
  existing tree does not have.
- **Permissive CORS (`Access-Control-Allow-Origin: *`) and keep the API on
  another origin.** Rejected: it is a *widening* of the browser's origin
  boundary to buy a deployment convenience the proxy already solves, and
  `*` cannot be combined with credentials — so the real version of this
  option is "reflect any origin and allow credentials", which is a
  well-known credential-theft primitive. The proxy removes the cross-origin
  surface instead of granting it.
- **A permissive CSP (`script-src 'self' 'unsafe-inline' 'unsafe-eval'`).**
  Rejected: with `'unsafe-inline'` an injected `<script>` block executes, and
  with `'unsafe-eval'` an injected string becomes code. The policy would
  still be present, still be quoted in a review, and still buy nothing
  against the one threat the CSP exists for — which is exactly the kind of
  control that creates false confidence. The cost of the strict policy is
  that the build must not rely on inline scripts or `eval`; that is a real
  constraint on the web tier and it is accepted.

## Known follow-up (not fixed here)

- **The token in `localStorage` stays there.** Moving it to an HttpOnly,
  Secure, SameSite cookie, with the CSRF protection that implies and with
  server-side authorization as the real gate, is a separate slice. Until that
  slice lands, this deployment is for a trusted network only — see "The
  accepted risk".
- **The login endpoint is Development/Test only.** In a deployed environment
  there is no way to log in at all, because the persona endpoint does not
  exist outside Development and Test
  (`src/OpsBridge.Api/Endpoints/AuthEndpoints.cs:20`). That is correct — a
  demo endpoint must not be reachable in production — but it means the web
  tier cannot be demonstrated to a human in a deployed environment until
  real authentication exists. A separate slice.
- **The security headers are a requirement on the image, not on the
  manifest.** Nothing in `infrastructure/kubernetes/base/opsbridge-web/`
  proves that the image sets them; they are asserted by the image's own
  configuration and by the browser suite. A future slice could assert the
  headers end-to-end (a request through the proxy, checking the response
  headers) — that is a browser-test change, not a manifest change.
- **`API_UPSTREAM` is a single upstream.** The proxy sends every `/api` call
  to one address. That is correct today (one API); if the API is ever split,
  the routing decision moves into the image's configuration and the manifest
  has to grow with it.
