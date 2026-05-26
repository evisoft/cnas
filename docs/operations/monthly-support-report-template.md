# Monthly support report — template

> Anchored to TOR ID(s): R2461 (Deliverable 7.1, Milestone M7). Data
> source: `IMonthlySupportReportService` (iter 96). Iteration 102.
> Companion to
> [`monthly-error-fix-report-template.md`](monthly-error-fix-report-template.md).

## 1. Purpose / scope

Standard template that operators fill in each month to document
support-ticket performance: volumes, SLA breach rates, severity and
category mix, and the supplier service-delivery manager's narrative
covering breaches and escalations. The numeric inputs come from the
admin reporting surface; the narrative is added manually and signed by
CNAS.

## 2. Audience / stakeholders

Supplier service-delivery manager, supplier support lead, CNAS
operations lead, CNAS quality officer.

## 3. Data source

GET `/api/admin/reporting/support-monthly` — backed by
`IMonthlySupportReportService` (see
`src/Cnas.Ps.Application/Reporting/IMonthlySupportReportService.cs`
and `src/Cnas.Ps.Infrastructure/Services/Reporting/MonthlySupportReportService.cs`).
Bucketing rules (from the interface XML doc):

- Tickets are included when `SubmittedAt` falls inside the requested month [UTC).
- Breach rates use `SupportTicketSlaEvent` rows whose `DetectedAt` falls in the same month and whose parent ticket was submitted in the month.
- Average resolution minutes are computed only across tickets with a non-null `ResolvedAt` in the month.
- Optional `CategoryCodes` filter — case-sensitive match against `SupportTicketCategory.Code`.

The endpoint is read-only (no audit events emitted).

## 4. Template — fill each section every month

```markdown
# Monthly support report — <!-- placeholder: YYYY-MM -->

**Reporting period:** <!-- placeholder: first day to last day, UTC -->
**Report owner (supplier):** <!-- placeholder: name + role -->
**Report owner (CNAS):** <!-- placeholder: name + role -->
**Source query:** GET /api/admin/reporting/support-monthly?periodStart=<!-- placeholder -->

## A. Headline metrics

| Metric | Value |
|---|---|
| Tickets submitted | <!-- placeholder: TotalSubmitted --> |
| Tickets resolved | <!-- placeholder: TotalResolved --> |
| Tickets closed | <!-- placeholder: TotalClosed --> |
| Tickets escalated | <!-- placeholder: TotalEscalated --> |
| Tickets cancelled | <!-- placeholder: TotalCancelled --> |

## B. SLA performance

| Metric | Value |
|---|---|
| Average first-response time (min) | <!-- placeholder: AvgFirstResponseMinutes --> |
| Average resolution time (min) | <!-- placeholder: AvgResolutionMinutes --> |
| First-response breach rate (4 decimals) | <!-- placeholder: FirstResponseBreachRate --> |
| Resolution breach rate (4 decimals) | <!-- placeholder: ResolutionBreachRate --> |

### SLA breach narrative

<!-- placeholder: For each non-zero breach rate, explain root cause,
remediation taken, and whether the underlying defect is captured by a
SupportTicket or ChangeRequest. Cite ticket / CR ids. -->

## C. Severity breakdown

| Severity | Count |
|---|---|
| Critical | <!-- placeholder --> |
| High | <!-- placeholder --> |
| Medium | <!-- placeholder --> |
| Low | <!-- placeholder --> |

## D. Category breakdown

| Category code | Count |
|---|---|
| <!-- placeholder --> | <!-- placeholder --> |

## E. Escalations

| Ticket id | Severity | Category | Escalated to | Reason | Outcome |
|---|---|---|---|---|---|
| <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> | <!-- placeholder --> |

**Total escalations in period:** <!-- placeholder: count -->

## F. Notable themes

<!-- placeholder: 2-3 sentence narrative describing the dominant theme
of the month — e.g. surge after a benefit-rule change, a regional
training shortfall, a partner-system outage that drove a category
spike. -->

## G. Open items carried into next period

<!-- placeholder: bullet list of unresolved tickets or trends to
watch. -->

## H. Sign-off

| Role | Name | Date |
|---|---|---|
| Supplier service-delivery manager | <!-- placeholder --> | <!-- placeholder --> |
| CNAS operations lead | <!-- placeholder --> | <!-- placeholder --> |
```

## 5. Acceptance criteria

- Report produced within the first 5 business days of the following month.
- All placeholders are filled (no `<!-- placeholder -->` marker left in the final artefact).
- Numeric figures match the API response payload.
- Section B includes a narrative for every non-zero breach rate.
- Section E lists every escalation, with `TotalEscalated` matching the row count.
- Both signatories sign the report.

## 6. Implementation map

| Surface | Path |
|---|---|
| Service interface | `src/Cnas.Ps.Application/Reporting/IMonthlySupportReportService.cs` |
| Service implementation | `src/Cnas.Ps.Infrastructure/Services/Reporting/MonthlySupportReportService.cs` |
| Admin endpoint | `src/Cnas.Ps.Api/Controllers/ReportingAdminController.cs` |
| Input DTO | `src/Cnas.Ps.Contracts/Reporting/MonthlySupportReportInputDto.cs` |
| Output DTO | `src/Cnas.Ps.Contracts/Reporting/MonthlySupportReportDto.cs` |

## 7. Status / open gaps

- Archival location for filled reports — pending decision between SharePoint and the platform's document store (same gap as R2462).
- Automatic PDF rendering of the filled template — not implemented.
- Trend-over-time charts across 12 months — not implemented; would require a separate longitudinal endpoint.

## 8. References

- TOR §Deliverable 7.1
- TODO.md row R2461
- [`monthly-error-fix-report-template.md`](monthly-error-fix-report-template.md) (R2462)
- [`operational-guides-index.md`](operational-guides-index.md)
