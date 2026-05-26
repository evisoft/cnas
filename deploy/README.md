# CNAS PS — Kubernetes deployment scaffolding

Production-shaped Helm chart and Dockerfiles for the CNAS PS application
stack (API + Blazor WASM Web). This directory is deployment infrastructure
only; CI/CD pipelines live elsewhere.

## Layout

```
deploy/
  docker/                          # Container images
    Dockerfile.api                 # ASP.NET Core 10 API (non-root, UID 1001)
    Dockerfile.web                 # Blazor WASM SPA served by nginx-unprivileged
    nginx.web.conf                 # SPA fallback + cache headers + WASM MIME
    .dockerignore                  # Lean build context
  helm/cnas-ps/                    # Helm chart (apiVersion v2, type application)
    Chart.yaml
    values.yaml                    # Default values, fully documented
    values.dev.yaml                # Local / single-node cluster overrides
    values.staging.yaml            # Pre-production
    values.prod.yaml               # Production (HA, HPA, monitoring, NetPol)
    templates/...                  # Deployments, services, ingress, HPA, etc.
```

## Prerequisites

| Tool    | Version  | Notes                                 |
|---------|----------|---------------------------------------|
| Kubernetes | >= 1.27 | tested against 1.27 / 1.28 / 1.29 |
| Helm    | >= 3.13  | `apiVersion: v2` chart features       |
| kubectl | >= 1.27  | cluster-version-matched                |
| Docker  | >= 24    | multi-stage builds with `dotnet:10.0` |

Cluster timezone should be **UTC** (CronJobs and audit logs assume UTC).

## Building images

From the repository root (NOT from `deploy/`):

```bash
# API
docker build \
  -f deploy/docker/Dockerfile.api \
  -t ghcr.io/evisoft/cnas-ps-api:$(git rev-parse --short HEAD) \
  .

# Web
docker build \
  -f deploy/docker/Dockerfile.web \
  -t ghcr.io/evisoft/cnas-ps-web:$(git rev-parse --short HEAD) \
  .
```

Copy `deploy/docker/.dockerignore` to `./.dockerignore` (or symlink) before
building so the context stays small.

Push both images to your registry, then record the tags for the Helm
install / upgrade.

## Required Secrets

The chart never embeds secrets. Create a Kubernetes Secret out-of-band and
reference its name via `api.envFromSecret`. Recommended keys:

| Env var                              | Purpose                                        |
|--------------------------------------|------------------------------------------------|
| `ConnectionStrings__Postgres`        | EF Core Npgsql connection string               |
| `Cnas__Jwt__SigningKey`              | Symmetric key for issuing access tokens        |
| `Cnas__Jwt__Issuer`                  | Token issuer (env-specific)                    |
| `Minio__AccessKey` / `Minio__SecretKey` | Object storage credentials                 |
| `Cnas__MGov__MConnect__Bearer`       | MGov MConnect bearer token                     |
| `Cnas__MGov__MPay__Bearer`           | MGov MPay bearer token                         |
| `Cnas__MGov__MNotify__Bearer`        | MGov MNotify bearer token                      |
| `Cnas__MGov__MSign__Bearer`          | MGov MSign bearer token                        |
| `Cnas__MGov__MDocs__Bearer`          | MGov MDocs bearer token                        |
| `Cnas__MGov__MLog__Bearer`           | MGov MLog bearer token                         |
| `Cnas__MGov__MPower__Bearer`         | MGov MPower bearer token                       |

Example (dev):

```bash
kubectl create namespace cnas-dev
kubectl -n cnas-dev create secret generic cnas-ps-dev \
  --from-literal=ConnectionStrings__Postgres='Host=...;Database=...;Username=...;Password=...' \
  --from-literal=Cnas__Jwt__SigningKey='<32+ random bytes base64>' \
  --from-literal=Minio__AccessKey='...' \
  --from-literal=Minio__SecretKey='...'
```

For production, prefer `external-secrets.io` (set
`secrets.externalOperator: true` and configure `secretStoreRef`).

## Installing the chart

```bash
# Dev
helm upgrade --install cnas-ps ./deploy/helm/cnas-ps \
  --namespace cnas-dev --create-namespace \
  -f deploy/helm/cnas-ps/values.dev.yaml \
  --set api.image.tag=dev-$(git rev-parse --short HEAD) \
  --set web.image.tag=dev-$(git rev-parse --short HEAD) \
  --atomic --wait --timeout 5m

# Staging
helm upgrade --install cnas-ps ./deploy/helm/cnas-ps \
  --namespace cnas-staging --create-namespace \
  -f deploy/helm/cnas-ps/values.staging.yaml \
  --set api.image.tag=staging-<sha> \
  --set web.image.tag=staging-<sha> \
  --atomic --wait --timeout 10m

# Production
helm upgrade --install cnas-ps ./deploy/helm/cnas-ps \
  --namespace cnas-prod --create-namespace \
  -f deploy/helm/cnas-ps/values.prod.yaml \
  --set api.image.tag=<release-version> \
  --set web.image.tag=<release-version> \
  --atomic --wait --timeout 10m
```

`--atomic --wait` rolls back automatically if any resource fails to become
ready before the timeout — leaving the cluster in a known-good state.

## Upgrade flow

`helm upgrade --install` is idempotent — re-running it with new image tags
performs a rolling update. Pods are restarted automatically when the API
ConfigMap changes (a `checksum/config` annotation forces the rollout).

```bash
helm -n cnas-prod upgrade cnas-ps ./deploy/helm/cnas-ps \
  -f deploy/helm/cnas-ps/values.prod.yaml \
  --set api.image.tag=<new-tag> \
  --set web.image.tag=<new-tag> \
  --atomic --wait
```

## Rollback

```bash
# List revisions
helm -n cnas-prod history cnas-ps

# Roll back to the previous release
helm -n cnas-prod rollback cnas-ps

# Or roll back to a specific revision
helm -n cnas-prod rollback cnas-ps <revision-number>
```

## Health check endpoints (API)

* `GET /health/live`  — process is up (no dependency checks).
* `GET /health/ready` — full dependency sweep (Postgres, MinIO, MGov
  services, workflow engine).

Probes are wired automatically in `deployment-api.yaml`.

## Tearing down

```bash
helm -n cnas-dev uninstall cnas-ps
kubectl delete namespace cnas-dev          # optional, removes namespaced data
```

Secrets created out-of-band are NOT deleted by `helm uninstall` — clean
them up explicitly if needed.
