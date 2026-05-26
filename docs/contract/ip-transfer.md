# Full IP transfer to CNAS

> Anchored to TOR ID(s): R2103 (LIPR 005-006, Phase 15). Iteration 102.
> Contract artefact — not code. Companion to
> [`data-ownership-nda-dpa.md`](data-ownership-nda-dpa.md) and
> [`licensing-model.md`](licensing-model.md).

## 1. Purpose / scope

Defines the supplier's obligation to transfer all intellectual property
rights over the bespoke artefacts of SI „Protecția Socială" to CNAS.
Scope = every artefact authored by the supplier or its sub-contractors
under the contract. Out of scope = third-party FOSS dependencies, which
are governed by their upstream OSI-compatible licences (see
[`licensing-model.md`](licensing-model.md) §3).

## 2. Audience / stakeholders

CNAS contracting authority, CNAS legal, supplier legal, supplier
engineering lead, and any successor supplier inheriting the codebase
post-contract (cross-reference
[`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md)).

## 3. Content + procedure

### 3.1 Artefacts transferred (LIPR 005)

| # | Artefact class | Concrete surface |
|---|---|---|
| 1 | Source code | All projects in `src/`, `tests/`, `perf/`, `ops/` |
| 2 | Build configuration | `Cnas.Ps.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig` |
| 3 | EF migrations | `src/Cnas.Ps.Infrastructure/Migrations/` (all applied + unapplied) |
| 4 | Helm charts + k8s manifests | `ops/k8s/cnas-ps/`, `values.*.yaml` overlays |
| 5 | CI/CD pipelines | `.github/workflows/`, any GitLab CI / Azure DevOps definitions |
| 6 | Architectural + design docs | `docs/ARCHITECTURE.md`, `docs/pm/sdd-iterative.md`, `docs/pm/srs-structural.md` |
| 7 | Operational guides | `docs/operations/operational-guides-index.md` and all linked guides |
| 8 | Training materials | `docs/training/training-plan.md` + course assets |
| 9 | Test artefacts | All eight `tests/Cnas.Ps.*.Tests` projects + fixtures |
| 10 | Performance baselines | `perf/cnas-baseline.js` (k6) + SLO definitions |
| 11 | Container images | OCI manifests of every deployed image, tagged `vX.Y.Z` |
| 12 | Brand assets created for CNAS | Logos, icons, design-system tokens authored under the contract |

### 3.2 When the transfer occurs (LIPR 006)

| Trigger | Effect |
|---|---|
| Delivery of each milestone | IP over the milestone artefacts vests in CNAS upon Acceptance Protocol sign-off ([`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md)). |
| End of contract | Final consolidating snapshot tagged `vX.Y.Z-contract-end` plus the Contract-end procedures package ([`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md)). |
| Termination for cause | Immediate transfer of the working tree as-of the termination notice date. |

### 3.3 Form of transfer

Irrevocable, worldwide, royalty-free, perpetual assignment of all
economic rights in the bespoke artefacts to CNAS. The supplier retains
moral rights only as required by RM Law 139/2010 on copyright; CNAS
gains exclusive rights to reproduce, modify, distribute, sub-license,
and commercialise the artefacts.

### 3.4 Exclusions

- **Third-party FOSS dependencies**: governed by upstream licences (Apache-2.0, MIT, BSD, MPL-2.0). Catalogued in `Directory.Packages.props`. CNAS receives a perpetual right of use under those licences.
- **Supplier-owned tooling not delivered under this contract**: internal scripts that never enter the repository are excluded.
- **Personnel know-how**: tacit knowledge of supplier staff is not transferred; documentation captured in `docs/` is the contractual substitute.

### 3.5 Warranty + indemnity

Supplier warrants that the transferred artefacts do not infringe
third-party IP and indemnifies CNAS against claims arising from the
bespoke artefacts. Standard exclusions apply for FOSS dependencies and
for modifications made by CNAS post-handover.

### 3.6 Operational evidence of transfer

| Evidence | Surface |
|---|---|
| Source tree present in CNAS-controlled git remote | Per [`../handover/source-code-handover.md`](../handover/source-code-handover.md) §3.3 |
| Build verifiable from clean clone | `dotnet build Cnas.Ps.slnx -p:TreatWarningsAsErrors=true` returns green on CNAS clone |
| Container images present in CNAS registry | OCI references in `values.production.yaml` resolve from CNAS-controlled registry |
| Signed Acceptance Protocol referencing this artefact | One row per milestone in [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md) |

## 4. Acceptance criteria

- Signed IP-transfer clause present in the executed contract referencing this artefact.
- For each milestone, a row in the Acceptance Protocol confirms IP transfer over the milestone artefacts.
- At contract end, the `vX.Y.Z-contract-end` tag exists in the CNAS-controlled repository and points to a building tree.
- The third-party FOSS inventory matches `Directory.Packages.props` at the contract-end tag.

## 5. Implementation map

| Surface | Path |
|---|---|
| Source tree | `src/`, `tests/`, `perf/`, `ops/` |
| Package inventory | `Directory.Packages.props` |
| Acceptance protocol template | [`../acceptance/acceptance-protocol-template.md`](../acceptance/acceptance-protocol-template.md) |
| Source-code handover | [`../handover/source-code-handover.md`](../handover/source-code-handover.md) |
| Contract-end procedures | [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md) |

## 6. Status / open gaps

- Final contract template containing the assignment clause is owned by CNAS legal — TODO R2103 (clause text).
- Third-party-dependency licence dossier (catalogue with licence text per package) — pending; tracked under R2101 (LIPR 002).
- Brand-asset inventory (logos, design-system tokens) — pending; placeholder until UI design-system is finalised.

## 7. References

- TOR §LIPR 005-006
- TODO.md row R2103
- RM Law 139/2010 on copyright and related rights
- [`licensing-model.md`](licensing-model.md)
- [`data-ownership-nda-dpa.md`](data-ownership-nda-dpa.md)
- [`../handover/source-code-handover.md`](../handover/source-code-handover.md)
- [`../handover/contract-end-procedures.md`](../handover/contract-end-procedures.md)
