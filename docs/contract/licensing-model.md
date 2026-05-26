# Comparative licensing model

> Anchored to TOR ID(s): R2105 (LIPR 008, Phase 15). Iteration 102.
> Bid artefact — not code. Companion to [`ip-transfer.md`](ip-transfer.md).

## 1. Purpose / scope

Lays out the licensing models considered for SI „Protecția Socială",
compares them, and records the supplier's recommendation. Required by
LIPR 008 as part of the bid response. Scope = the bespoke artefacts of
the system and the third-party dependencies it depends on.

## 2. Audience / stakeholders

CNAS contracting authority, CNAS legal, CNAS architecture board,
supplier proposal manager, supplier engineering lead.

## 3. Content + procedure

### 3.1 Models considered

| Model | Definition | Cost shape | Source-code rights to CNAS | Vendor lock-in |
|---|---|---|---|---|
| **Perpetual licence** | One-off purchase of a packaged product with optional annual maintenance. | High up-front + maintenance % per year. | Usually none (binary only). | High — depends on vendor lifecycle. |
| **Subscription / SaaS** | Recurring per-user or per-tenant fee for hosted service. | Annual recurring; multi-year escalators common. | None (typically). | Very high — data + code held by vendor. |
| **Custom development with full IP transfer** | Supplier builds bespoke artefacts and assigns IP to the customer. | Project cost + post-implementation support. | Full — see [`ip-transfer.md`](ip-transfer.md). | None for the bespoke layer; FOSS-only third-party dependencies cap lock-in. |
| **Open-source platform with paid support** | Customer adopts an OSS product and contracts a vendor for support and customisation. | Subscription for support + customisation cost. | Full source available under OSI licence. | Low — community fallback exists. |

### 3.2 Comparison

| Criterion | Perpetual | Subscription | Custom dev + IP transfer | OSS + support |
|---|---|---|---|---|
| Alignment with LIPR 005-006 (full IP) | No | No | **Yes** | Partial (FOSS, not bespoke) |
| Alignment with LIPR 002 (no extra licence cost) | Often no (CALs) | No (per-seat) | **Yes** (FOSS stack) | **Yes** |
| Alignment with LIPR 003-004 (unlimited users / API) | Often no (CALs) | No (per-seat) | **Yes** | **Yes** |
| Alignment with LIPR 007 (data ownership + residency in RM) | Possible | Often violated (vendor cloud) | **Yes** | **Yes** |
| Adaptability to RM social-security law changes | Slow (vendor roadmap) | Slow (vendor roadmap) | **Fast** (controlled by CNAS) | Fast |
| Risk of vendor lock-in | High | Very high | **Low** | Low |
| Up-front cost | High | Low | Moderate | Low |
| Long-run total cost of ownership | Variable | High (recurring) | Moderate | Low-to-moderate |

### 3.3 Recommendation

**Custom development with full IP transfer** is the recommended primary
model for SI „Protecția Socială":

- Satisfies LIPR 005-006 (full IP), LIPR 002 (no extra licence cost), LIPR 003-004 (unlimited users), LIPR 007 (data ownership) by construction.
- Allows the system to track RM social-security law changes on CNAS's schedule, not a vendor roadmap.
- Eliminates per-user or per-API licence economics that would scale poorly with the citizen population.

For third-party dependencies the rule is **OSI-compatible FOSS by default**:

- All runtime packages in `Directory.Packages.props` must carry an OSI-approved licence (Apache-2.0, MIT, BSD, MPL-2.0, EPL-2.0, or equivalent).
- Copyleft licences (GPL, AGPL) are accepted only for tools that do not link into the runtime (CLI utilities, dev-only); ban applies to libraries linked into the deployed app.
- Each new package added in a PR is checked against the policy at code-review time. A licence dossier is generated for each release tag.

### 3.4 Procurement consequences

| Consequence | Effect |
|---|---|
| No CAL / per-seat fees | CNAS can grow the user base without recurring licence cost. |
| No vendor-locked telemetry | Operational data stays in CNAS infrastructure. |
| Support continuity | At contract end, code + docs + container images are with CNAS (per [`ip-transfer.md`](ip-transfer.md)). |
| Independent supplier swap | Any qualified supplier can take over the codebase using the public docs in `docs/`. |

## 4. Acceptance criteria

- The bid includes this comparative document.
- The bid asserts the chosen model is "custom development with full IP transfer", aligning with LIPR 005-008.
- The bid includes a third-party dependency licence dossier (placeholder — generated per release tag).
- No third-party runtime dependency carries a non-OSI licence.

## 5. Implementation map

| Surface | Path |
|---|---|
| Third-party package inventory | `Directory.Packages.props` |
| IP transfer clause | [`ip-transfer.md`](ip-transfer.md) |
| Data ownership / NDA / DPA | [`data-ownership-nda-dpa.md`](data-ownership-nda-dpa.md) |
| Source-code handover | [`../handover/source-code-handover.md`](../handover/source-code-handover.md) |

## 6. Status / open gaps

- Third-party licence dossier (generated artefact per release) — pending; tracked under R2101.
- Final bid template containing this section — owned by supplier proposal manager.
- Per-package licence check in CI (`dotnet list package --vulnerable` + a licence check tool) — not yet wired into CI.

## 7. References

- TOR §LIPR 002-008
- TODO.md rows R2100-R2105
- OSI Approved Licences — [https://opensource.org/licenses](https://opensource.org/licenses)
- [`ip-transfer.md`](ip-transfer.md)
- [`data-ownership-nda-dpa.md`](data-ownership-nda-dpa.md)
