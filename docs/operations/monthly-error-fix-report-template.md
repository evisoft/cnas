# Monthly error-fix and documentation-update report — template

> Anchored to TOR ID(s): R2462 (Deliverable 7.2, Milestone M7). Data
> source: `IMonthlyErrorFixReportService` (iter 96). Iteration 100.

## 1. Purpose / scope

Standard template that operators fill in each month to document
error fixes, change-request deployments and rollbacks, integrity
findings, and template updates over the reporting period. The numeric
inputs come from the admin reporting surface; the narrative is added
by the supplier service-delivery manager and signed by CNAS.

## 2. Audience / stakeholders

Supplier service-delivery manager, supplier QA, CNAS operations lead,
CNAS quality officer.

## 3. Data source

GET `/api/admin/reporting/error-fix-monthly` — backed by
`IMonthlyErrorFixReportService` (see
`src/Cnas.Ps.Application/Reporting/IMonthlyErrorFixReportService.cs`
and `src/Cnas.Ps.Infrastructure/Services/Reporting/MonthlyErrorFixReportService.cs`).
Bucketing rules:

- Integrity findings count when `FirstDetectedAt` falls in the month.
- Change requests "deployed" count when `DeployedAt` falls in the month.
- Change requests "rolled back" count when `RolledBackAt` falls in the
  month.
- Template variant updates count when `UpdatedAtUtc` falls in the
  month (falls back to `CreatedAtUtc`).

## 4. Template — fill each section every month

```markdown
# Monthly error-fix and documentation-update report — <!-- placeholder: YYYY-MM -->

**Reporting period:** <!-- placeholder: first day to last day, UTC -->
**Report owner (supplier):** <!-- placeholder: name + role -->
**Report owner (CNAS):** <!-- placeholder: name + role -->
**Source query:** GET /api/admin/reporting/error-fix-monthly?periodStart=<!-- placeholder -->

## A. Summary

<!-- placeholder: 2-3 sentence narrative covering the dominant theme of the month -->

## B. Integrity findings by severity

| Severity | Count | Aggregates affected |
|---|---|---|
| Critical | <!-- placeholder --> | <!-- placeholder --> |
| High | <!-- placeholder --> | <!-- placeholder --> |
| Medium | <!-- placeholder --> | <!-- placeholder --> |
| Low | <!-- placeholder --> | <!-- placeholder --> |

## C. Change requests

| Outcome | Count |
|---|---|
| Deployed in period | <!-- placeholder --> |
| Rolled back in period | <!-- placeholder --> |

### Notable change requests

<!-- placeholder: list any change request that warranted a narrative -->

## D. Documentation / template updates

| Surface | Updated count |
|---|---|
| TemplateVariant rows updated | <!-- placeholder --> |
| Docs updated (markdown files) | <!-- placeholder --> |

### Highlights

<!-- placeholder: list noteworthy template / doc updates -->

## E. Aggregate × severity breakdown

| Aggregate | Severity | Count |
|---|---|---|
| <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |

## F. Open items carried into next period

<!-- placeholder: bullet list -->

## G. Sign-off

| Role | Name | Date |
|---|---|---|
| Supplier service-delivery manager | <!-- placeholder --> | <!-- placeholder --> |
| CNAS operations lead | <!-- placeholder --> | <!-- placeholder --> |
```

## 5. Acceptance criteria

- Report is produced within the first 5 business days of the following
  month.
- All placeholders above are filled (no `<!-- placeholder -->` marker
  left in the final artefact).
- The numeric figures match the API response payload.
- Both signatories sign the report.

## 6. Implementation map

| Surface | Path |
|---|---|
| Service interface | `src/Cnas.Ps.Application/Reporting/IMonthlyErrorFixReportService.cs` |
| Service implementation | `src/Cnas.Ps.Infrastructure/Services/Reporting/MonthlyErrorFixReportService.cs` |
| Admin endpoint | `src/Cnas.Ps.Api/Controllers/ReportingAdminController.cs` |

## 7. Status / open gaps

- Document archival location (where filled reports live) — pending
  decision between SharePoint and the platform's document store.
- Automatic PDF rendering of the filled template — not implemented.

## 8. References

- TOR §Deliverable 7.2
- TODO.md row R2462
- [`operational-guides-index.md`](operational-guides-index.md)
