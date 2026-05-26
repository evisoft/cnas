# DEL 002 — Technical artefacts (index)

> Anchored to TOR ID(s): R2601 (TOR §7.1). Index doc; content lives
> in the linked files. Iteration 103.

## 1. Purpose

Single navigation surface for the technical deliverables required by
TOR §7.1 DEL 002: architecture doc, SRS, SDD, infra requirements,
integration docs, API docs, source code, migration plan + scripts,
deployment plan + scripts, git access.

## 2. Audience

CNAS engineering lead, supplier engineering lead, security officer,
operators (SREs / DBAs), audit reviewers, acceptance committee.

## 3. Bundle contents

| Artefact | File | Status |
|---|---|---|
| Architecture doc | [`../ARCHITECTURE.md`](../ARCHITECTURE.md) | Repo root |
| TOGAF ADM artefacts | [`../architecture/togaf-adm-artefacts.md`](../architecture/togaf-adm-artefacts.md) | Earlier iter |
| SRS (structural) | [`../pm/srs-structural.md`](../pm/srs-structural.md) | Iter 99 |
| SDD (iterative) | [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md) | Iter 99 |
| Tech / infra requirements | [`../pm/tech-infra-requirements.md`](../pm/tech-infra-requirements.md) | Iter 99 |
| Integration specs (per touchpoint) | [`../integration/technical-integration-specs.md`](../integration/technical-integration-specs.md) | Iter 100 |
| Integration acceptance protocol | [`../integration/interop-acceptance-protocol.md`](../integration/interop-acceptance-protocol.md) | Iter 100 |
| Source code (full tree) | Repo root `src/` + `tests/` + `perf/` | Working tree |
| Source-code handover plan | [`../handover/source-code-handover.md`](../handover/source-code-handover.md) | Iter 100 |
| API surface (OpenAPI XML) | `src/Cnas.Ps.Api/bin/Debug/net10.0/Cnas.Ps.Api.xml` (generated) | Build output |
| Migration plan / methodology | TODO R2430 anchors: `IMigrationPlanService` (iter 89) | Code |
| Migration scripts scaffold | Iter 89 scaffold: `IMigrationSource`, `MigrationImporter`, `MigrationDryRunJob` | Code |
| Migration acceptance protocol | [`../migration/migration-acceptance-protocol.md`](../migration/migration-acceptance-protocol.md) | Iter 100 |
| Deployment plan | [`../production-deployment.md`](../production-deployment.md) | Iter 98 |
| Go-live strategy | [`../go-live-strategy.md`](../go-live-strategy.md) | Iter 98 |
| Deployment scripts | Helm charts + CI workflows (`.github/workflows/`) | Working tree |
| Git access transfer | Procedure in [`../handover/source-code-handover.md`](../handover/source-code-handover.md) §3 | Iter 100 |

## 4. Acceptance criteria

- `dotnet build Cnas.Ps.slnx -p:TreatWarningsAsErrors=true` green on
  CNAS clone (R2700).
- All linked artefacts resolve and are version-tagged with the
  delivered build.
- CNAS engineering lead signs DEL 002 row in the Acceptance Protocol.

## 5. Status / open gaps

- Migration execution against real CNAS data (R2432): pending — must
  happen on CNAS infrastructure only (MIG 009).
- API XML docs: generated artefact; the public-facing OpenAPI bundle
  is produced on release tag.
- Deployment scripts for production environment: depend on CNAS
  Kubernetes cluster handover.

## 6. References

- TOR §7.1 DEL 002
- TODO.md R2601 (this row), R2410-R2434, R2454-R2455, R2445
- [`../pm/`](../pm/), [`../integration/`](../integration/),
  [`../migration/`](../migration/), [`../handover/`](../handover/)
