# cnas-ps Helm chart

Helm v3 chart bundling the full CNAS „Protecția Socială" runtime in a single,
self-contained release. Mandated by **TOR R0038**: API Deployment + HPA,
Postgres StatefulSet (Patroni), MinIO StatefulSet, Ingress with TLS, and a
default-deny NetworkPolicy surface.

No external chart dependencies. Some Moldovan public-sector procurement
envelopes prohibit pulling charts from third-party registries (bitnami,
community); every resource here is inlined.

## Prerequisites

| Component | Version / Note |
|-----------|----------------|
| Kubernetes | ≥ 1.27 (autoscaling/v2 + networking.k8s.io/v1 GA) |
| Helm CLI | 3.14+ |
| Ingress controller | nginx-ingress (default) or Traefik / Istio (override `ingress.className`) |
| cert-manager | Required if `ingress.tls.certManager=true`; else upload the TLS secret yourself |
| External Secrets Operator | Required if `vault.enabled=true`; install [external-secrets.io](https://external-secrets.io) and the matching `(Cluster)SecretStore` first |
| StorageClass | Required for production (`postgres.storageClass`, `minio.storageClass`). Dev may leave them empty to use the cluster default |

## Install

### Staging

```bash
helm upgrade --install cnas-ps ops/k8s/cnas-ps \
  --namespace cnas-ps-staging --create-namespace \
  --values ops/k8s/cnas-ps/values.staging.yaml \
  --set image.tag=<git-sha>
```

### Production

```bash
helm upgrade --install cnas-ps ops/k8s/cnas-ps \
  --namespace cnas-ps-prod --create-namespace \
  --values ops/k8s/cnas-ps/values.production.yaml \
  --set image.tag=<git-sha>
```

The `image.tag` flag is mandatory; the chart fails at template time
if it is empty (see `_helpers.tpl::validateRequired`).

## Upgrade

Helm overlays the values files left-to-right. Last value wins.

```bash
helm upgrade cnas-ps ops/k8s/cnas-ps \
  --namespace cnas-ps-prod \
  --values ops/k8s/cnas-ps/values.production.yaml \
  --set image.tag=<new-sha>
```

The chart respects rolling-update semantics: `maxSurge: 25%` /
`maxUnavailable: 0`. With three replicas this means one extra pod at a
time and zero downtime.

## Lint and Render Locally

```bash
# Static lint — must finish with 0 errors AND 0 warnings.
helm lint ops/k8s/cnas-ps
helm lint ops/k8s/cnas-ps -f ops/k8s/cnas-ps/values.staging.yaml
helm lint ops/k8s/cnas-ps -f ops/k8s/cnas-ps/values.production.yaml

# Full render — pipe to kubectl --dry-run for an extra layer of validation.
helm template release-test ops/k8s/cnas-ps \
  --set image.tag=test \
  --set ingress.host=test.example.gov.md \
  | kubectl apply --dry-run=client -f -

# Staging / production overlay render.
helm template release-test ops/k8s/cnas-ps \
  -f ops/k8s/cnas-ps/values.staging.yaml \
  --set image.tag=test

helm template release-test ops/k8s/cnas-ps \
  -f ops/k8s/cnas-ps/values.production.yaml \
  --set image.tag=test

# Landmark-string assertion harness (POSIX shell).
bash ops/k8s/cnas-ps/test/render.sh
```

## Configuration reference

### Top-level

| Key | Default | Meaning |
|-----|---------|---------|
| `nameOverride` | `""` | Short chart name override. Empty = derive from `Chart.Name`. |
| `fullnameOverride` | `""` | Full release-name override. Empty = `{release}-{chart}`. |
| `commonLabels` | `{}` | Labels appended to every resource. |
| `commonAnnotations` | `{}` | Annotations appended to every resource. |
| `imagePullSecrets` | `[]` | Private-registry pull secrets. |
| `image.repository` | `ghcr.io/evisoft/cnas-ps-api` | API image registry. |
| `image.tag` | `""` (REQUIRED) | Immutable image tag. Empty = install fails. |
| `image.pullPolicy` | `IfNotPresent` | kubelet pull policy. |
| `replicaCount` | `3` | Base replica count (HPA overrides at runtime). |
| `serviceAccount.create` | `true` | Materialize a dedicated SA for the API pod. |
| `serviceAccount.name` | `""` | Override SA name. Empty = chart fullname. |
| `serviceAccount.annotations` | `{}` | Extra SA annotations (e.g. `iam.gke.io/...`). |
| `podAnnotations` | `{}` | Annotations on the API pod template (Vault Agent etc.). |
| `securityContext` | UID 10001 + seccomp RuntimeDefault | Pod security context. |
| `containerSecurityContext` | readOnlyRootFs + drop ALL | Container security context. |
| `resources.requests.cpu` | `250m` | API CPU request. |
| `resources.requests.memory` | `512Mi` | API memory request. |
| `resources.limits.cpu` | `1` | API CPU limit. |
| `resources.limits.memory` | `1Gi` | API memory limit. |
| `nodeSelector` | `{}` | Node selector for the API pod. |
| `tolerations` | `[]` | Tolerations for the API pod. |
| `affinity` | preferred anti-affinity | Soft host spread by default. |

### Service

| Key | Default | Meaning |
|-----|---------|---------|
| `service.type` | `ClusterIP` | Service type (LB / NodePort are NOT supported). |
| `service.port` | `80` | Cluster port. |
| `service.targetPort` | `8080` | Container port. |
| `service.annotations` | `{}` | Service annotations. |

### Ingress

| Key | Default | Meaning |
|-----|---------|---------|
| `ingress.enabled` | `true` | Render the Ingress object. |
| `ingress.className` | `nginx` | Ingress class. |
| `ingress.host` | `""` (REQUIRED when enabled) | External hostname. |
| `ingress.tls.enabled` | `true` | Render the TLS stanza. |
| `ingress.tls.secretName` | `cnas-ps-tls` | TLS secret name. |
| `ingress.tls.certManager` | `false` | Annotate for cert-manager. |
| `ingress.tls.clusterIssuer` | `letsencrypt-prod` | cert-manager ClusterIssuer. |
| `ingress.annotations.proxy-body-size` | `30m` | Max body — Minio max is 25 MiB + 5 MiB headroom. |
| `ingress.annotations.proxy-read-timeout` | `60` | Read timeout (seconds). |

### HPA

| Key | Default | Meaning |
|-----|---------|---------|
| `hpa.enabled` | `true` | Render HPA. |
| `hpa.minReplicas` | `3` | Floor. |
| `hpa.maxReplicas` | `10` | Ceiling (sized for TOR PSR 003: 2000 concurrent sessions). |
| `hpa.targetCPUUtilizationPercentage` | `70` | CPU scale threshold. |
| `hpa.targetMemoryUtilizationPercentage` | `80` | Memory scale threshold. |
| `hpa.behavior` | aggressive up / conservative down | autoscaling/v2 behavior block. |

### PDB

| Key | Default | Meaning |
|-----|---------|---------|
| `pdb.enabled` | `true` | Render PDB. |
| `pdb.minAvailable` | `2` | Survives single-node drain when replicaCount=3. |

### Config (ConfigMap-bound)

| Key | Default | Meaning |
|-----|---------|---------|
| `config.aspNetCoreEnvironment` | `Production` | `ASPNETCORE_ENVIRONMENT`. |
| `config.observability.otlpEndpoint` | `""` | OTLP/gRPC collector. Empty = disabled. |
| `config.observability.serviceName` | `cnas-ps-api` | `service.name` resource attr. |
| `config.observability.environment` | `production` | `deployment.environment` resource attr. |
| `config.rateLimiting.enabled` | `true` | Master switch. |
| `config.rateLimiting.trustForwardedHeaders` | `true` | Only safe behind an XFF-rewriting Ingress. |
| `config.rateLimiting.anonymousPermitLimit` | `5` | SEC 008 — 5 req/min anonymous. |
| `config.rateLimiting.authenticatedPermitLimit` | `200` | 200 req/min authenticated. |
| `config.rateLimiting.callbackPermitLimit` | `60` | MGov callbacks. |
| `config.rateLimiting.uploadPermitLimit` | `10` | Upload throttle. |
| `config.rateLimiting.globalConcurrencyLimit` | `500` | Hard ceiling. |
| `config.mgovResilience.enabled` | `true` | Polly pipeline kill switch. |
| `config.mgovResilience.msign.*` | 3 retries / 60s pipeline | Per-client overrides (msign shown; mpay/mconnect/mnotify/mlog/mdocs/mconnect-events/mcabinet follow the same shape). |
| `config.mgov.msignBaseUrl` | `""` | MSign base URL. Empty = disabled. |
| `config.mgov.mpayBaseUrl` | `""` | MPay base URL. |
| `config.mgov.mconnectBaseUrl` | `""` | MConnect base URL. |
| `config.mgov.mnotifyBaseUrl` | `""` | MNotify base URL. |
| `config.mgov.mlogBaseUrl` | `""` | MLog base URL. |
| `config.mgov.mdocsBaseUrl` | `""` | MDocs base URL. |
| `config.mgov.mconnectEventsBaseUrl` | `""` | MConnect Events base URL. |
| `config.mcabinet.baseUrl` | `""` | MCabinet base URL. |
| `config.mcabinet.systemCode` | `CNAS-PS` | Stable system code (NEVER change). |
| `config.minio.endpoint` | auto | `<release>-minio:9000` when minio.enabled. |
| `config.minio.useSsl` | `false` | TLS to MinIO. |
| `config.minio.citizenUploadsBucket` | `cnas-citizen-uploads` | Bucket for citizen uploads. |
| `config.minio.generatedDocumentsBucket` | `cnas-generated` | Bucket for generated documents. |
| `config.minio.maxFileSizeBytes` | `26214400` | 25 MiB (SEC 010). |

### Secret (Secret-bound)

| Key | Source | Notes |
|-----|--------|-------|
| `secret.inline.connectionStringsPostgres` | values | Inlined for staging only. |
| `secret.inline.fieldEncryptionKey` | values | base64 AES-256 key (32 bytes). |
| `secret.inline.fieldHashingSaltKey` | values | base64 HMAC-SHA256 key (≥ 32 bytes). |
| `secret.inline.minioAccessKey` | values | MinIO access key. |
| `secret.inline.minioSecretKey` | values | MinIO secret key. |
| `secret.inline.mtlsMsignPassword` | values | PFX decrypt password (msign). Same shape repeats for mpay, mconnect, mnotify, mlog, mdocs, mconnect-events, mcabinet. |
| `secret.inline.mpassClientSecret` | values | OIDC client secret. |

### Vault / ExternalSecret

| Key | Default | Meaning |
|-----|---------|---------|
| `vault.enabled` | `false` | Toggle ExternalSecret rendering. MUST be true in production. |
| `vault.refreshInterval` | `1h` | ExternalSecret refresh interval. |
| `vault.secretStoreRef.name` | `cnas-secret-store` | (Cluster)SecretStore name. |
| `vault.secretStoreRef.kind` | `ClusterSecretStore` | Either `SecretStore` or `ClusterSecretStore`. |
| `vault.remoteKeys` | see values.yaml | k8s key → remote path mapping. |

### Postgres

| Key | Default | Meaning |
|-----|---------|---------|
| `postgres.enabled` | `true` | Render Patroni StatefulSet + services. |
| `postgres.image.repository` | `ghcr.io/zalando/spilo-16` | Spilo image. |
| `postgres.image.tag` | `3.3-p2` | Pin in values.production.yaml. |
| `postgres.replicas` | `3` | 1 leader + 2 standbys (TOR PSR 006). |
| `postgres.storageClass` | `""` | Empty = cluster default. Production MUST override. |
| `postgres.storageSize` | `100Gi` | Per-pod PVC size. |
| `postgres.podAntiAffinity.type` | `preferred` | `required` for prod (≥ 3 nodes). |
| `postgres.applicationUsername` | `cnas` | Application DB user. |
| `postgres.replicationUsername` | `replicator` | Streaming replication user. |
| `postgres.superuserUsername` | `postgres` | Superuser (Spilo bootstrap). |

### MinIO

| Key | Default | Meaning |
|-----|---------|---------|
| `minio.enabled` | `true` | Render MinIO workload. |
| `minio.distributed` | `true` | StatefulSet (4 pods + erasure coding) vs single-pod Deployment. |
| `minio.image.repository` | `quay.io/minio/minio` | MinIO image. |
| `minio.image.tag` | `RELEASE.2024-12-13T22-19-12Z` | Pin in values.production.yaml. |
| `minio.replicas` | `4` | Distributed-mode minimum. |
| `minio.storageClass` | `""` | Empty = cluster default. |
| `minio.storageSize` | `50Gi` | Per-pod PVC. |
| `minio.apiPort` | `9000` | S3 API port. |
| `minio.consolePort` | `9001` | Web console (never exposed via Ingress). |

### NetworkPolicies

| Key | Default | Meaning |
|-----|---------|---------|
| `networkPolicies.enabled` | `true` | Render default-deny + targeted allow rules. |
| `networkPolicies.ingressControllerNamespace` | `ingress-nginx` | Where the Ingress controller pods live. |
| `networkPolicies.ingressControllerSelector` | `{app.kubernetes.io/name: ingress-nginx}` | Pod labels on the Ingress controller. |
| `networkPolicies.mgovEgressCidrs` | `[10.0.0.0/8]` | CIDRs the API may reach on port 443 (MGov VRF). |
| `networkPolicies.dnsCidrs` | `[10.96.0.10/32]` | kube-dns. |
| `networkPolicies.otlpCollectorCidrs` | `[]` | OTLP collector — empty disables. |

## Secrets matrix

| .NET binding path | k8s key | Source (inline) | Source (vault) | Rotation seam |
|-------------------|---------|-----------------|----------------|---------------|
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | `secret.inline.connectionStringsPostgres` | `vault.remoteKeys.ConnectionStrings__Postgres` | Patroni superuser password rotation |
| `Cnas:FieldEncryption:Key` | `Cnas__FieldEncryption__Key` | `secret.inline.fieldEncryptionKey` | `vault.remoteKeys.Cnas__FieldEncryption__Key` | Coordinated re-encryption pass (see FieldEncryptionOptions XML doc) |
| `Cnas:FieldHashing:SaltKey` | `Cnas__FieldHashing__SaltKey` | `secret.inline.fieldHashingSaltKey` | `vault.remoteKeys.Cnas__FieldHashing__SaltKey` | Recompute all shadow columns (see FieldHashingOptions XML doc) |
| `Cnas:Storage:Minio:AccessKey` | `Cnas__Storage__Minio__AccessKey` | `secret.inline.minioAccessKey` | `vault.remoteKeys.Cnas__Storage__Minio__AccessKey` | MinIO root key rotation |
| `Cnas:Storage:Minio:SecretKey` | `Cnas__Storage__Minio__SecretKey` | `secret.inline.minioSecretKey` | `vault.remoteKeys.Cnas__Storage__Minio__SecretKey` | MinIO root key rotation |
| `MGov:MPassClientSecret` | `MGov__MPassClientSecret` | `secret.inline.mpassClientSecret` | `vault.remoteKeys.MGov__MPassClientSecret` | AGE / MPass portal rotation |
| `Cnas:MGov:Mtls:Certificates:<svc>:Password` | `Cnas__MGov__Mtls__Certificates__<svc>__Password` | `secret.inline.mtls<Svc>Password` | `vault.remoteKeys.Cnas__MGov__Mtls__Certificates__<svc>__Password` | PFX rotation per service |

## Troubleshooting

### API pods stuck in CrashLoopBackOff with "Cnas:FieldEncryption:Key is required"

The `Secret` is empty or missing the key. With `vault.enabled=false`, inspect:

```bash
kubectl -n <ns> get secret <release>-cnas-ps-api-secret -o yaml
```

With `vault.enabled=true`, inspect the ExternalSecret reconcile status:

```bash
kubectl -n <ns> describe externalsecret <release>-cnas-ps-api-secret
```

The most common cause is a typo in `vault.remoteKeys.<key>` not matching
the path in the backing store.

### `kubectl drain` blocked by PDB

Expected behaviour when `replicaCount == pdb.minAvailable + 1`. Set
`pdb.enabled: false` in dev to bypass; in production wait for the rolling
update to complete or scale `replicaCount` up to drain one node.

### Probe failures right after install ("/health/ready 503")

A dependency probe is failing — Postgres or MinIO is not up yet. Tail
the dependency pods:

```bash
kubectl -n <ns> logs -l app.kubernetes.io/component=postgres -f
kubectl -n <ns> logs -l app.kubernetes.io/component=minio -f
```

Watch `/health/ready` against an individual API pod via port-forward:

```bash
kubectl -n <ns> port-forward <api-pod> 8080:8080
curl -sS http://localhost:8080/health/ready | jq .
```

The response body lists each dependency probe and the failure reason.

### Patroni leader election stuck (no `role: master` pod)

This usually means the Patroni SA does not have permissions on
endpoints. Verify:

```bash
kubectl -n <ns> auth can-i patch endpoints \
  --as=system:serviceaccount:<ns>:<release>-cnas-ps-postgres
```

Re-apply the chart if the RoleBinding is missing.

### NetworkPolicy too restrictive

Sympton: API logs "Connection refused" when reaching a NEW external
service that was added to the configuration but not to the allow-list.
Update `networkPolicies.mgovEgressCidrs` (or add a new CIDR list) in
values and `helm upgrade`. Verify with:

```bash
kubectl -n <ns> describe networkpolicy
```

### `helm` not available in CI environment

The chart is plain YAML — every template can be rendered with the
[Helm v3 binary](https://helm.sh/docs/intro/install/) installed locally
or in the CI runner. CI must run `helm lint` and `helm template` for
both staging and production values files; the assertion harness
`test/render.sh` complements the structural checks with landmark-string
greps.
