# Role — Administrator tehnic STISC (AT)

Infrastructure / SRE / DevOps role. Operated from STISC (the technical
service centre that runs MCloud). Owns the cluster, the database,
the integrations transport, the observability stack, and the
disaster-recovery path. Does NOT touch functional configuration —
that is AS.

## TOR identifier

- Code: **AT**.
- RBAC policy: `CnasTechAdmin` (standalone — does NOT subsume
  `CnasAdmin`; the two are deliberately disjoint).
- ABAC: institution-wide; restricted to the technical surface.
- Rate-limit partition: `Authenticated`.

## Use cases owned

- **UC20** — Proceduri automate (technical side).
- **UC23** — Jurnalizez (audit + integrity).

## Day-to-day tasks

- Deploy and upgrade the stack through the Helm chart at
  [`../../ops/k8s/cnas-ps/`](../../ops/k8s/cnas-ps/README.md).
- Operate the CI/CD pipelines in `.github/workflows/`.
- Monitor `/health/live` + `/health/ready` and respond to alerts.
- Triage the failed-job DLQ (`AdminController`) and replay or
  escalate.
- Rotate secrets in Vault / Kubernetes Secrets.
- Manage maintenance windows + peak-hour gates.
- Schedule and run database migrations (`MigrationAdminController`).
- Coordinate backup verification + DR drills.
- Tune Npgsql / PgBouncer sizing against PSR 003 (2000 concurrent
  users).
- Drive integrity-check runs and respond to findings.
- Onboard / off-board AT-level engineers.

## Features they touch

- [`../DevOps.md`](../DevOps.md) — read first.
- [`../features/background-jobs.md`](../features/background-jobs.md)
- [`../features/mgov-integration.md`](../features/mgov-integration.md) (transport)
- [`../features/audit.md`](../features/audit.md)
- [`../features/admin-console.md`](../features/admin-console.md) (failed-job replay,
  database health, maintenance windows)

## Operational references

- [`../operations.md`](../operations.md) — full runbook.
- [`../bcp-drp-backup-plan.md`](../bcp-drp-backup-plan.md) — backup &
  DR.
- [`../recovery-procedures.md`](../recovery-procedures.md) — recovery
  drill.
- [`../production-deployment.md`](../production-deployment.md) —
  go-live deployment plan.
- [`../performance-ops.md`](../performance-ops.md) — performance
  tuning.

## What they cannot do

- Edit business / functional configuration (service passports,
  workflows, templates, classifiers) — that is AS.
- Approve or examine applications — that is UCNAS / SD / SC.
- Access plaintext of encrypted user fields — by design,
  `CnasTechAdmin` does not satisfy `CnasAdmin`. The two policies are
  disjoint so an infrastructure compromise doesn't expose business
  data.

## Onboarding & offboarding

Two AT-level engineers approve a new AT account through the 4-eyes
queue. Off-boarding revokes the role + every cluster credential
(KUBECONFIG secret, kubelet client cert, Vault token, GHCR token).

The full handover checklist after contract termination:
[`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md).
The security ownership map at handover:
[`../handover/security-ownership-map.md`](../handover/security-ownership-map.md).
