# Feature — Documents & templates

## What it is

Generated documents (decision letters, certificates, notifications) and
the template engine that produces them. Renders DOCX from
placeholder-tagged templates; archives the output with hash-keyed
storage and an immutability marker; passes outbound to MSign for
qualified-signature application.

## TOR / UC mapping

- **UC11** — Descarc document.
- **UC17** — Metadate & șabloane.
- Annex 7 — 35 DOCX templates.
- TOR clauses: CF 11.*, CF 17.*, MR 013.

## Surface

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/documents/{sqid}` | `CnasUser` | Get document metadata |
| `GET /api/documents/{sqid}/content` | `CnasUser` | Download rendered DOCX/PDF |
| `GET /api/document-hash/{sqid}` | `CnasUser` | Hash + immutability status |
| `GET /api/templates` | `CnasUser` | List active templates |
| `POST /api/templates-admin` | `CnasAdmin` | Upload / edit a template |
| `GET /api/template-version-history/{sqid}` | `CnasAdmin` | Version history per template |
| `GET /api/template-language-coverage-admin` | `CnasAdmin` | RO/RU/EN coverage report |
| `GET /api/report-templates` | `CnasUser` | Report-specific templates |
| `POST /api/m-notify-templates-admin` | `CnasAdmin` | MNotify notification templates |

## Code map

- Controllers
  - [`DocumentsController.cs`](../../src/Cnas.Ps.Api/Controllers/DocumentsController.cs)
  - [`DocumentHashController.cs`](../../src/Cnas.Ps.Api/Controllers/DocumentHashController.cs)
  - [`TemplatesController.cs`](../../src/Cnas.Ps.Api/Controllers/TemplatesController.cs)
  - [`TemplatesAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/TemplatesAdminController.cs)
  - [`TemplateVersionHistoryController.cs`](../../src/Cnas.Ps.Api/Controllers/TemplateVersionHistoryController.cs)
  - [`TemplateLanguageCoverageAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/TemplateLanguageCoverageAdminController.cs)
  - [`ReportTemplatesController.cs`](../../src/Cnas.Ps.Api/Controllers/ReportTemplatesController.cs)
  - [`MNotifyTemplatesAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/MNotifyTemplatesAdminController.cs)
- Application services
  - `IDocumentService`, `IDocumentGenerationService`,
    `ITemplateAdminService`, `IUploadedTemplateRenderer`,
    `IMNotifyTemplateService`.
- Infrastructure
  - `IDocxTemplate` — 35 implementations in
    `src/Cnas.Ps.Infrastructure/Documents/Templates/`.
  - `DocxRenderHelpers.cs` — placeholder substitution.
  - `MinioFileStorage` — content-hash-keyed storage with immutability.

## Data model

| Entity | Purpose |
|---|---|
| `Document` | Generated document row + reference to MinIO object. |
| `DocumentTemplate` | Template definition row, multi-locale. |
| `MNotifyTemplate` | Notification template content (RO/RU/EN). |
| `FileImmutabilityRecord` | Marker that a stored object is immutable. |

## Business rules

- Every rendered document is hashed (SHA-256) and stored
  hash-as-object-key. Re-rendering the same template + payload yields
  the same hash — deduplication is implicit.
- Once marked immutable via `IFileImmutabilityMarker`, the object
  cannot be deleted (`IFileImmutabilityGuard` returns
  `FILESTORAGE.IMMUTABLE_OBJECT`). True bucket-level lock is a
  deployment-time MinIO toggle that complements the app-level guard.
- Template version history is append-only; rendering a Decision pinned
  to template v4 always uses v4 even if v5 is published.
- RO is the canonical locale; RU and EN are derived. The language-coverage
  admin report surfaces templates where a non-canonical locale is
  missing.

## Tests

- `tests/Cnas.Ps.Infrastructure.Tests/Documents/`
- `tests/Cnas.Ps.Application.Tests/Templates/`

## What's NOT here

- PDF/A conversion + MSign signing — externally gated on the MSign
  WSDL; see [`mgov-integration.md`](mgov-integration.md).
- Citizen-side document download UX — see [`personal-account.md`](personal-account.md).
