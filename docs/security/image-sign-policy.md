# Image-sign policy — OpsBridge

## Scope

Applies to all first-party images promoted to `infrastructure/kubernetes/overlays/prod/`.

## Requirements

### 1. Digest-pinned (mandatory)

Every image reference in the prod overlay must be digest-pinned:

```yaml
images:
  - name: opsbridge-api
    newName: <acr-login-server>/opsbridge-api
    newTag: sha256:<64-hex-digest>
```

Tags (`:sha`, `:latest`, `:v1`) are mutable and forbidden in `overlays/prod`.
The `scripts/gitops-pin-overlay.py` script enforces this: it rewrites `newName`
and `newTag` for every first-party image and exits non-zero if any pin is
missing or if the tag is not a full 40-char commit SHA.

### 2. Signed (mandatory)

Before an image is referenced in `overlays/prod`, it must be:

1. Built from a verified commit on `main`.
2. Scanned with Trivy (HIGH+ CRITICAL vulnerabilities block).
3. Signed with `cosign` (or equivalent) using the OIDC-federated GitHub Actions
   identity. The signature is attached to the image digest in ACR.
4. Verified: `cosign verify` must succeed against the ACR login server and the
   image digest before the GitOps pin job runs.

### 3. Enforcement

The `manifest-validation` CI job renders the prod overlay and validates it
against the Kubernetes schema. The `gitops-update` job pins the overlay only
after `release-push` has pushed digest-addressed images and `manifest-validation`
has passed.

No image reaches `overlays/prod` without a digest pin and a valid signature.
