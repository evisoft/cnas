# Feature documentation index

> One file per feature area, mapped to TOR functional modules (M1–M8) and
> use cases UC01–UC23. Each doc points at code paths, REST endpoints, and
> the TOR clause that owns the requirement. Companion to
> [`../ARCHITECTURE.md`](../ARCHITECTURE.md) (cross-cutting layout)
> and [`../DevOps.md`](../DevOps.md) (deployment + ops).

## TOR modules → feature docs

| TOR module | Doc | UCs |
|---|---|---|
| **Public portal** (M0 surface) | [`public-portal.md`](public-portal.md) | UC01, UC02 |
| **M1 Plătitori contribuții** | [`contributors.md`](contributors.md) | UC03 |
| **M2 Persoane asigurate** | [`insured-persons.md`](insured-persons.md) | UC03, UC12 |
| **M3 Contribuții sociale** | [`contributions.md`](contributions.md) | UC07 |
| **M4 Cerere / Decizie / Dosar** | [`applications.md`](applications.md), [`examination.md`](examination.md), [`decisions.md`](decisions.md), [`workflows.md`](workflows.md) | UC04–UC08, UC10, UC11, UC16, UC21 |
| **M5 Plăți prestații** | [`payments.md`](payments.md) | UC21 |
| **M6 Securitate** | [`identity-access.md`](identity-access.md), [`audit.md`](audit.md) | UC18, UC23 |
| **M7 Administrare și control** | [`service-passport.md`](service-passport.md), [`document-templates.md`](document-templates.md), [`admin-console.md`](admin-console.md), [`helpdesk.md`](helpdesk.md) | UC15, UC17, UC18 |
| **M8 Raportare statistică** | [`reporting.md`](reporting.md) | UC09, UC19 |
| **Cross-cutting** | [`mgov-integration.md`](mgov-integration.md), [`notifications.md`](notifications.md), [`background-jobs.md`](background-jobs.md), [`personal-account.md`](personal-account.md) | UC13, UC14, UC20, UC22 |

## How to read a feature doc

Each doc follows the same shape so a new contributor can jump in fast:

1. **What it is** — one paragraph, plain language.
2. **TOR / UC mapping** — clauses + use cases this implements.
3. **Surface** — REST endpoints + authorization policy + rate-limit
   bucket.
4. **Code map** — controllers, application services, infrastructure
   services, domain entities. Direct links.
5. **Data model** — primary tables / entities + relationships.
6. **Workflows / state machines** — diagrams when relevant.
7. **Business rules** — invariants, edge cases, gotchas.
8. **Tests** — pointers to the test projects that cover the feature.
9. **Open questions / what's NOT here** — honest gaps.
