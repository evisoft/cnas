# Source code and repository access handover

> Anchored to TOR ID(s): R2445 (UTD 014, Milestone M5). Companion to
> R2417 (Deliverable 2.4 — full source code + git access). Iteration 100.

## 1. Purpose / scope

Defines the artefact list, sequence and signatories required to hand
over the source code, repository access, CI/CD secrets, and operational
ownership of SI „Protecția Socială" from the supplier to CNAS during
the M5 milestone. Reused on contract end (see R2507 /
[`contract-end-procedures.md`](contract-end-procedures.md)).

## 2. Audience / stakeholders

Supplier engineering lead, CNAS engineering lead, supplier DevOps,
CNAS DevOps, supplier security officer, CNAS security officer, and
the joint acceptance committee.

## 3. Procedure

### 3.1 Artefact list

| # | Artefact | Form |
|---|---|---|
| 1 | Source code repository (full history) | Git remote URL — `<placeholder://git.cnas.md/protectia-sociala>` |
| 2 | Solution entry point | `Cnas.Ps.slnx` |
| 3 | Branching guide | This document, §3.2 |
| 4 | CI/CD workflow | `.github/workflows/ci.yml` |
| 5 | Secrets inventory | CNAS secrets manager (handover §3.4) |
| 6 | Helm chart + overlays | `deploy/helm/` (staging + production) |
| 7 | Docker compose (dev) | `docker-compose.yml` |
| 8 | EF migrations | `src/Cnas.Ps.Infrastructure/Migrations/` |
| 9 | Architecture + SDD | [`../ARCHITECTURE.md`](../ARCHITECTURE.md), [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md) |
| 10 | Operational guides index | [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md) |

### 3.2 Branching strategy

- `main` — protected. Direct push disallowed. PRs require two reviewers
  and green CI.
- Short-lived feature branches off `main`. Squash-merge.
- Release tags: `vX.Y.Z` aligned with the cadence in R2503 (monthly
  updates, version every 3 years).
- Hotfix branches off the release tag, merged back to `main` with the
  fix-forward PR.

### 3.3 Repository access transfer

1. CNAS creates organisation + team accounts on the target git host.
2. Supplier mirrors `<placeholder://supplier-git>` to
   `<placeholder://git.cnas.md/protectia-sociala>` with full history.
3. Supplier rotates and revokes its deploy keys; CNAS issues new keys.
4. Supplier removes itself from write groups; CNAS becomes
   `CODEOWNERS` for every path.
5. Branch protection rules and merge policies re-applied on the CNAS
   remote.

### 3.4 CI/CD secrets handover

1. Joint inventory session: list every secret currently in the
   supplier CI vault (DB credentials, MGov client cert thumbprints,
   storage tokens, etc.).
2. CNAS issues replacement secrets in its own secrets manager.
3. Supplier updates `.github/workflows/ci.yml` references to point at
   CNAS-controlled secret names.
4. Supplier rotates all secrets the CNAS instance is not yet using,
   then revokes its own copies once the next green CI run confirms
   the swap.
5. The handover protocol logs the swap with timestamps; never store
   secret values in the protocol itself.

### 3.5 Ownership transfer

1. CODEOWNERS updated to reference CNAS teams.
2. Issue tracker, status reports, and on-call rota transferred.
3. Monitoring / alerting routes rewired to CNAS pager.
4. Final repository administrator handover — at least one CNAS member
   becomes organisation owner before any supplier owner is removed.

## 4. Acceptance criteria / sign-off

- CNAS clones the repo and reproduces a green build:
  `dotnet build Cnas.Ps.slnx -p:TreatWarningsAsErrors=true`.
- CNAS reproduces a green test run.
- CNAS triggers the CI pipeline successfully on a no-op commit.
- All secrets above are issued from CNAS-controlled stores.
- Supplier no longer holds production write access (verified via
  audit log).
- Both parties sign the handover protocol.

## 5. Implementation map

| Surface | Path |
|---|---|
| Build entry point | `Cnas.Ps.slnx` |
| CI pipeline | `.github/workflows/ci.yml` |
| Architecture | [`../ARCHITECTURE.md`](../ARCHITECTURE.md) |
| SDD | [`../pm/sdd-iterative.md`](../pm/sdd-iterative.md) |
| SRS | [`../pm/srs-structural.md`](../pm/srs-structural.md) |

## 6. Status / open gaps

- Target git host URL — PLACEHOLDER pending CNAS infrastructure choice.
- Helm chart path assumed `deploy/helm/` — confirm during handover.
- CODEOWNERS authored at `.github/CODEOWNERS`; replace placeholder CNAS team
  slugs with the concrete organisation/team handles during target git-host
  provisioning.
- Contract-end-specific handover artefacts referenced in
  [`contract-end-procedures.md`](contract-end-procedures.md).

## 7. References

- TOR §UTD 014
- TODO.md rows R2417, R2445, R2507
- [`contract-end-procedures.md`](contract-end-procedures.md)
- [`../operations/operational-guides-index.md`](../operations/operational-guides-index.md)
