# TOGAF 9.1 ADM artefacts — skeleton

> Anchored to TOR ID(s): R2112 (ARH 003, Phase 15). Iteration 102.
> Architecture-documentation artefact — not code. Maps existing
> programme documentation to the TOGAF 9.1 Architecture Development
> Method (ADM) phases A–H. SDD coverage cross-links to
> [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md).

## 1. Purpose / scope

TOGAF 9.1 is the framework named in ARH 003. This document is the
skeleton mapping every ADM phase to a target artefact + current
status, so the architecture-review board can see at a glance which
artefacts exist, which are stubs, and which are missing. Scope = the
ADM Preliminary phase plus phases A through H. Requirements
Management (the central activity) is treated as a cross-cutting row.

## 2. Audience / stakeholders

CNAS enterprise-architecture lead, supplier architecture board,
architecture-review board (deliverable signatories), and the M2
deliverable owner (SDD R2414, SRS R2402).

## 3. Content + procedure

### 3.1 ADM phase → artefact map

| ADM phase | Concern | Target artefact | Current status |
|---|---|---|---|
| **Preliminary** | Framework + principles tailoring | `docs/architecture/preliminary-and-principles.md` (target) | **Stub** — principles inferred from `CLAUDE.md` (Universal Playbook) + TOR ARH section; consolidate in dedicated file. |
| **Phase A — Architecture Vision** | Vision, scope, stakeholders, business goals | [`../pm/project-kickoff.md`](../pm/project-kickoff.md), [`../pm/project-management-plan.md`](../pm/project-management-plan.md) | **Present** — kickoff doc captures the vision; consolidates supplier-side stakeholder map. |
| **Phase B — Business Architecture** | Business services, processes, organisation | [`../pm/srs-structural.md`](../pm/srs-structural.md) (structural SRS, R2402) | **Present** — structural SRS enumerates business capabilities and use cases. |
| **Phase C(1) — Data Architecture** | Data entities, lifecycle, classification | [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md) §3.2 Persistence; EF migrations in `src/Cnas.Ps.Infrastructure/Migrations/` | **Present** — SDD §3.2 + EF model are the canonical data architecture; classification (PII vs non-PII) ratified by SEC 035/044. |
| **Phase C(2) — Application Architecture** | Logical components, integration, interfaces | [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md) §3 Architecture layers, [`../integration/technical-integration-specs.md`](../integration/technical-integration-specs.md), [`../EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md) | **Present** — SDD documents layers; integration spec documents the MGov MSuite touchpoints + 11 external IS facades. |
| **Phase D — Technology Architecture** | Platforms, infra, deployment | [`../ARCHITECTURE.md`](../ARCHITECTURE.md), [`../pm/tech-infra-requirements.md`](../pm/tech-infra-requirements.md), `ops/k8s/cnas-ps/` Helm chart, `Dockerfile`(s) | **Present** — Helm chart + tech-infra doc anchor platform decisions (k8s, Postgres, MinIO, Quartz, OpenTelemetry, Vault). |
| **Phase E — Opportunities + Solutions** | Implementation increments, sequencing | [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md) (iterative SDD — milestone-by-milestone), milestone gates in TODO.md §16 | **Present** — milestones M1–M7 + Phase 13 capture the increments. |
| **Phase F — Migration Planning** | Migration approach + plans | [`../migration/migration-acceptance-protocol.md`](../migration/migration-acceptance-protocol.md), `MigrationPlan` registry + `IMigrationPlanService` (iter 89) | **Present** — declarative migration registry + bilateral acceptance protocol. |
| **Phase G — Implementation Governance** | Change control, four-eyes, release management | `IChangeRequestService` (iter 81/94), `IMaintenanceWindowService` (iter 98), [`../production-deployment.md`](../production-deployment.md), [`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md) | **Present** — 4-eyes++ ChangeRequest workflow, scheduled maintenance windows, production-deployment + stabilization plans. |
| **Phase H — Architecture Change Management** | Ongoing change handling post-go-live | [`../stabilization/stabilization-plan.md`](../stabilization/stabilization-plan.md), [`../operations/monthly-error-fix-report-template.md`](../operations/monthly-error-fix-report-template.md), [`./monthly-support-report-template.md`](../operations/monthly-support-report-template.md), [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md) | **Present** — stabilization plan + monthly reports + contract-end procedures form the architecture-change cadence. |
| **Requirements Management (continuous)** | Requirements feed every phase | TOR.md + TODO.md; coverage matrix in TODO.md §16 | **Present** — TODO.md row-by-row mapping of TOR IDs to artefacts is the requirements-management register. |

### 3.2 Architecture deliverables vs ADM outputs

| ADM output (per 9.1) | Where it lives in this repo |
|---|---|
| Statement of Architecture Work | Embedded in [`../pm/project-management-plan.md`](../pm/project-management-plan.md) |
| Architecture Vision | [`../pm/project-kickoff.md`](../pm/project-kickoff.md) |
| Architecture Definition Document | [`../ARCHITECTURE.md`](../ARCHITECTURE.md) + [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md) |
| Architecture Requirements Specification | [`../pm/srs-structural.md`](../pm/srs-structural.md) + TOR.md |
| Architecture Roadmap | TODO.md §16 milestone breakdown |
| Implementation + Migration Plan | [`../migration/migration-acceptance-protocol.md`](../migration/migration-acceptance-protocol.md) + [`../go-live-strategy.md`](../go-live-strategy.md) |
| Architecture Contract | Acceptance Protocol — [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md) |
| Compliance Assessment | Layer-boundary + naming + ratchet tests in `tests/Cnas.Ps.Architecture.Tests/` |
| Change Request | `IChangeRequestService` aggregate (iter 81/94) |
| Requirements Impact Assessment | Performed inline in PR description + ChangeRequest payload |

### 3.3 Procedure

1. At each milestone gate, the architecture-review board walks this table and confirms the named artefact is present and current.
2. If an artefact is added or moved, this file is updated in the same PR.
3. If a phase is found to be a Stub, the gap is logged as a row in TODO.md and the milestone closure is conditional on resolution.

## 4. Acceptance criteria

- Every ADM phase row has a non-empty "Target artefact" cell.
- Every row marked Present has at least one working link from this file to the artefact.
- Every row marked Stub has a TODO.md tracking ID (R-id) recorded.

## 5. Implementation map

| Surface | Path |
|---|---|
| Iterative SDD (R2414) | [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md) |
| Structural SRS (R2402) | [`../pm/srs-structural.md`](../pm/srs-structural.md) |
| Architecture overview | [`../ARCHITECTURE.md`](../ARCHITECTURE.md) |
| Architecture tests | `tests/Cnas.Ps.Architecture.Tests/` |
| Helm chart (technology arch) | `ops/k8s/cnas-ps/` |

## 6. Status / open gaps

- Preliminary phase (principles + framework tailoring) — Stub; to be consolidated in `docs/architecture/preliminary-and-principles.md`. TODO R2112 follow-up.
- Architecture Vision document is currently embedded in the kickoff doc rather than a stand-alone artefact; consider promoting.
- Compliance Assessment is implicit in the test suite; consider exporting a markdown summary at each release tag.

## 7. References

- TOR §ARH 003
- TODO.md row R2112
- The Open Group — TOGAF 9.1 Standard (Part II — Architecture Development Method)
- [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md)
- [`../pm/srs-structural.md`](../pm/srs-structural.md)
- [`../ARCHITECTURE.md`](../ARCHITECTURE.md)
