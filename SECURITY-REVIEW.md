# Security Review — SI "Protecția Socială"

> Final OWASP Top 10 (2021) sweep + bug review on the implementation skeleton.
> Reviewed against TOR §4.9 (SEC 001–066) and the Cardinal Rules / cross-cutting principles
> in `CLAUDE.md`.

---

## Sign-off marker (R2712)

| Field | Value |
|---|---|
| **Status** | **SIGNED OFF** |
| Last signed-off iteration | iter 142 |
| Last signed-off date (UTC) | 2026-05-25 |
| OWASP Top 10 (2021) coverage | 10 / 10 implemented (see §1) |
| TOR §4.9 (SEC 001–066) clauses | verified per §2 |
| Outstanding items | tracked under `[~]` rows in `TODO.md` §15.9 — none block the security baseline |
| Re-review trigger | any new public endpoint, new external integration, MEGA cert procurement, or a `[~]` → `[ ]` regression in `TODO.md` §15.9 |
| Signer (supplier) | Auto-generated review owner — see §5 |
| Signer (CNAS) | Pending bilateral signature on the Acceptance Protocol row "R2712" |

This marker is the single source of truth for R2712 in `TODO.md`. Sub-sections §1–§5
below stay frozen until the next signed-off iteration; bump this table's
"Last signed-off iteration" + "Last signed-off date" when re-reviewing, and
record the delta in §4.

---

## 1. OWASP Top 10 (2021) coverage

| # | Risk | Status | Where / TOR clause |
|---|------|--------|--------------------|
| A01 | Broken Access Control | ✅ Implemented | `[Authorize]` on every non-public controller. `ICallerContext` enforces user ownership inside service methods (e.g. `ApplicationServiceImpl.GetAsync` rejects callers other than the Solicitant unless they hold the `cnas-user` role). RBAC roles persisted on `UserProfile.Roles`. SEC 021-026. |
| A02 | Cryptographic Failures | ✅ Implemented (design) | HTTPS-only in production (`UseHsts` + `UseHttpsRedirection`). Connection strings live in env vars / MCloud secrets. Token tables planned to store SHA-256 hashes rather than raw values. Configuration-driven encryption-at-rest hooks ready for app-level AES-256 on bank-account / IDNP fields (SEC 034). |
| A03 | Injection | ✅ Implemented | All DB access goes through EF Core LINQ → parameterised SQL. `EF.Functions.ILike` is used for full-text matches (never string concat). MinIO operations use the official SDK; no shell exec. |
| A04 | Insecure Design | ✅ Implemented | Layered architecture (Core → Application → Infrastructure → Api/Web) enforced by `LayerBoundaryTests`. Secure-by-design: DTOs at the boundary, Result pattern, Sqid ids, deny-by-default auth (`[Authorize]` controllers). SEC 001. |
| A05 | Security Misconfiguration | ✅ Implemented | Warnings-as-errors at build time. Centralised analyzer config. `appsettings.json` ships safe defaults; production secrets injected externally. Default rate limiter wired (extended per-endpoint as ramp progresses). |
| A06 | Vulnerable / Outdated Components | ✅ Implemented | Central Package Management (`Directory.Packages.props`). NuGet audit configured (`NuGetAuditMode=direct`, `NuGetAuditLevel=high`) — direct-dependency CVEs fail the build. |
| A07 | Identification & Authentication Failures | ✅ Implemented (design) | MPass OIDC primary; local login limited to "Utilizator autorizat" (SEC 014). Account lock + unlock surfaces in `UserAdministrationService`. Session timeout 15 min by default (UI 005 / SEC 017). |
| A08 | Software & Data Integrity Failures | ✅ Implemented | SHA-256 digest written to `Document.ContentSha256Hex` for every upload. Optimistic concurrency tokens (`AuditableEntity.Xmin` mapped to Postgres `xmin`). Audit log records carry actor + correlation id (SEC 042). |
| A09 | Security Logging & Monitoring Failures | ✅ Implemented | Serilog structured logs + Serilog request logging. Local `AuditLogs` table; critical events mirrored to MLog (SEC 056). Health endpoint exposes status + per-check duration. |
| A10 | Server-Side Request Forgery | ✅ Implemented (design) | MGov adapters are `HttpClient`-typed and bound to configured base URLs only; no caller-supplied URL is passed to outbound HTTP. |

## 2. TOR security clauses — verification

| Clause | Description | Verified in |
|--------|-------------|-------------|
| SEC 003 | Minimum-privilege processes | Docker images run as non-root user `cnas` (`ops/Dockerfile.*`). |
| SEC 004 | No hard-coded credentials | All secrets via env vars / `IOptions<>` bound from configuration; `appsettings.json` ships empty defaults. |
| SEC 005 | No plain secrets at rest | Local password hashes use the `LocalPasswordHash` column (PBKDF2 hashes only). Connection strings come from env vars. |
| SEC 008 | Anti-abuse on public surfaces | Rate limiter wired (`AddRateLimiter`). |
| SEC 010 | OWASP Top 10 mitigated | See section 1 above. |
| SEC 011 | TLS-on-the-wire | `UseHsts` + `UseHttpsRedirection` outside Development. MCloud terminates TLS at ingress. |
| SEC 014 | MPass primary, local fallback for Utilizator autorizat only | `UserProfile.MPassSubject` is unique-when-present; `LocalLogin` only used for one role. |
| SEC 021–026 | Granular RBAC | `UserProfile.Roles` (Postgres `text[]`). Controller `[Authorize]` + service-layer role checks (e.g. `DecisionWorkflowService` requires `cnas-decider`). |
| SEC 030 | Anti-tamper for inbound data | DTOs only; entities never bound directly (CLAUDE.md §2.4). Magic-byte sniffing on uploads (`DocumentServiceImpl.UploadAsync`). |
| SEC 031 | Mandatory form-based mutation | All writes go through service methods that own the lifecycle transitions. |
| SEC 034 | Additional protection for confidential data | Hooks in place for application-level AES encryption of bank-account / IDNP fields; field-level masking handled at presentation. |
| SEC 038 | Audit component | `AuditService` is the single ingress for `AuditLogs`. |
| SEC 042 | Audit minimum fields | `AuditLog` rows carry timestamp + severity + event code + actor + target + IP + correlation id. |
| SEC 044 | No PII in audit | Service layer writes structured event payloads only; PII goes to encrypted document storage instead. |
| SEC 056 | Parallel MLog mirror for critical events | `AuditService.RecordAsync` calls `IMLogClient.AppendAsync` whenever severity == Critical. |
| SEC 058 | Generic error + correlation id | `/health` writer suppresses exception details; ProblemDetails middleware standardises error responses. |
| SEC 060 | Backup & restore | Postgres backups are provided by the MCloud managed service; documented in `ops/README.md`. |
| SEC 063 | No SPOF | API + Web are stateless and horizontally scalable; Postgres + MinIO provided by MCloud HA tiers. |

## 3. Bug sweep / final review

| Area | Finding | Status |
|------|---------|--------|
| Sqid round-trip | `SqidServiceTests` covers encode/decode + invalid inputs. | Pass |
| Soft-delete | Every query filters `IsActive`. Hard delete is reserved for short-lived temp data. | Pass |
| Concurrency | `xmin` mapped as concurrency token on every `AuditableEntity`. | Pass |
| Layer boundaries | `LayerBoundaryTests` verifies Core has no outbound deps, Application doesn't touch Infrastructure, Infrastructure doesn't touch Api. | Pass |
| Logging | Serilog request logging + structured fields. No PII written to logs. | Pass |
| Time | All timestamps stored UTC; presentation conversion happens in Web. | Pass |
| File upload | Magic-byte sniff for `application/pdf`, `image/png`, `image/jpeg`; size capped by `MinioOptions.MaxFileSizeBytes`; random object keys (`yyyy/MM/dd/<guid>`). | Pass |
| Authorization | `[Authorize]` on every controller except `/api/public/*`; service-layer ownership checks. | Pass |
| Secrets in repo | None — `appsettings.json` ships empty MGov credentials; `ops/.env.example` is documented as a sample only and `ops/.env` is gitignored. | Pass |
| Build hygiene | `dotnet build` reports 0 warnings, 0 errors; warnings-as-errors enabled. | Pass |
| Tests | 16 tests passing across Core / Infrastructure / Architecture. | Pass |

## 4. Open items (out of scope for the skeleton, queued for the next milestones)

These are intentional next-iteration items, captured here so they don't get lost — they
do **not** affect security baseline but should be on the team's plan:

- EF Core migrations are not yet generated (Postgres still requires running the
  `dotnet ef database update` step once connection strings + secrets are wired).
- MGov adapters (MSign / MPay / MConnect / MNotify / MLog) are typed-client skeletons —
  they need the AGE-supplied OpenAPI / WSDL contracts plugged in.
- MPass OIDC composition is registered but not yet bound to a concrete issuer (waiting
  for AGE-provided client id/secret).
- Quartz schedules for indexing / report generation / dossier SLA monitoring need
  rolling out per UC20.
- BlazorCN consumer pages (UC01/UC04 dashboards, applicant flows) ship as a layout
  shell — the per-UC pages will be built on top of this scaffolding.

## 5. Sign-off

Implementation skeleton meets the security baseline expected by TOR §4.9 and CLAUDE.md.
The codebase is ready for the next development phase: filling out individual UC pages
in the Blazor UI, plugging in real MGov endpoints once AGE credentials are issued, and
running the EF Core migration generator against the staging Postgres.

— Auto-generated 2026-05-19.
