# SI „Protecția Socială" — CNAS

> .NET 10 reference implementation of the Moldovan National Social Insurance Office back-office —
> 2026–2028 procurement.

## Disclosure

This code is for **pure demonstration only** — a worked example of how
an Agent-AI workflow can produce a complicated, production-shaped
system. It is **not intended for use by any third party**. All
contents are **© Evisoft SRL**, all rights reserved. No license,
warranty, or fitness for any purpose is granted or implied.

## What this is

`cnas` is the production codebase for **SI „Protecția Socială"**, the information system that
the Casa Națională de Asigurări Sociale (CNAS) of the Republic of Moldova will operate from
2026 onwards. The system runs the full social-insurance back office: contribution accounting
for *Plătitori*, the *Persoane asigurate* registry, the life-event service catalogue (81
services across pensions, allowances, disability, death, and adjacent benefits), the decision
workflow (*Cerere → Examinare → Decizie → Plată*), and the citizen-facing portal that drives
all of it.

The procurement reference is *Caietul de sarcini 17.04.2026* (consolidated in
[`tor/TOR.md`](tor/TOR.md), ~15 000 lines of Romanian). The contract spans 2026–2028
implementation plus a 12-month post-implementation support window; the in-scope deliverables
are documented in [`TODO.md`](TODO.md) §16.

The stack at one breath: **.NET 10** / **ASP.NET Core 10** REST API + **Blazor WebAssembly**
Standalone front-end / **EF Core 10** on **PostgreSQL 16** behind **PgBouncer** /
**MinIO** for object storage / **Quartz.NET** scheduler / **OpenTelemetry** traces and metrics
with OTLP export / **Serilog** structured logs / MGov platform integration (MPass, MSign,
MPay, MConnect, MConnectEvents, MNotify, MLog, MCabinet, MDocs). The production target is
**MCloud / Kubernetes** via the Helm chart in [`ops/k8s/cnas-ps/`](ops/k8s/cnas-ps/README.md).

## Current build state

| Metric | Value |
|---|---|
| Build | 0 warnings / 0 errors, `TreatWarningsAsErrors=true` globally |
| Tests | 2301 passed / 1 skipped / 0 failed |
| Architecture tests | 22/22 passing (`tests/Cnas.Ps.Architecture.Tests/`) |
| EF Core migrations | 14 in `src/Cnas.Ps.Infrastructure/Persistence/Migrations/` |
| REST controllers | 23 in `src/Cnas.Ps.Api/Controllers/` |
| E2E journey tests | 27 in `tests/Cnas.Ps.E2E.Tests/Journeys/` (24 UC-mapped + 3 platform) |
| DOCX templates | 35 Annex 7 templates implementing `IDocxTemplate` |
| Background jobs | 3 Quartz `IJob` types + 1 sweeper + 1 listener |
| MGov client interfaces | 8 (`IMSignClient`, `IMPayClient`, `IMConnectClient`, `IMNotifyClient`, `IMLogClient`, `IMConnectEventsProducer`, `IMDocsClient`, `IMCabinetPublisher`) |

MPass is intentionally **not** in the client-interfaces count — it is consumed via SAML
assertion claims at sign-in (see [`docs/EGOV-INTEGRATION-GAP.md`](docs/EGOV-INTEGRATION-GAP.md)
§MPass and §MPower for the common misconception correction).

The single skipped test is `StaffLoginPageJourneyTests.StaffLoginPage_RendersAndPromptsForMPass`
(`tests/Cnas.Ps.E2E.Tests/Journeys/StaffLoginPageJourneyTests.cs`). It is gated on three
upstream items:

1. The MPass SAML middleware is not yet wired (cookie + OIDC placeholder today; refactor
   gated on the MEGA-issued X.509 certificate).
2. No `StaffLogin.razor` page exists on the Blazor side.
3. The E2E `ApiHostFixture` does not yet host the Blazor Web project.

The skip message captures all three reasons verbatim so the gap is visible at test-run time.

## Repository layout

The layer split and dependency direction are enforced at build time by
[`tests/Cnas.Ps.Architecture.Tests/`](tests/Cnas.Ps.Architecture.Tests/) — in particular
`LayerBoundaryTests.cs`, `BoundaryRulesTests.cs`, and `NamingConventionTests.cs`. Don't fight
the boundaries; add new code where the dependency rule still holds.

- **`src/Cnas.Ps.Core/`** — Domain entities, enums, value objects (`Idnp`, `Idno`, `Money`,
  `DateRangeUtc`, `PercentRate`, `PhoneE164`, `IbanMd`), `Result<T>`, `ErrorCodes`,
  `ICnasTimeProvider`, `IExternalId` marker on the 13 boundary-crossing entities. Zero
  external dependencies.
- **`src/Cnas.Ps.Contracts/`** — WASM-safe wire DTOs shared between the API and the browser
  client. Sqid-encoded `string` IDs only — never raw `long`. The Blazor WASM SDK cannot pull
  in EF Core, so this sliver carries the request / response shapes.
- **`src/Cnas.Ps.Application/`** — Use-case interfaces (`UC01`–`UC23`), validators,
  server-side abstractions (`ICnasDbContext` / `IReadOnlyCnasDbContext`, `IFileStorage`, the
  MGov client interfaces, `ICallerContext`, `IJwtTokenIssuer`, `IRefreshTokenService`,
  `IPendingAdminActionService`, `IUserAccountStateService`).
- **`src/Cnas.Ps.Infrastructure/`** — EF Core (PostgreSQL + Npgsql) with PgBouncer-aware
  pooling, MinIO storage, MGov HTTP/SOAP/SAML adapters, Quartz jobs, Sqid encoder, the
  AES-256-GCM `AesFieldEncryptor`, the `Hmac256Hasher` shadow-column HMAC, the
  `Argon2idPasswordHasher` (PHC-formatted, OWASP 2024 parameters), the `JwtTokenIssuer` +
  `RefreshTokenService`, and the `TurnstileCaptchaVerifier`.
- **`src/Cnas.Ps.Api/`** — REST surface, OpenAPI document, Serilog request logging,
  OpenTelemetry pipeline, rate limiter, health checks, MPass composition, CORS for the WASM
  origin, the `UnhandledExceptionMiddleware` that maps every uncaught exception to a sanitised
  ProblemDetails 500 without leaking stack traces.
- **`src/Cnas.Ps.Web/`** — Blazor WebAssembly Standalone front-end on top of BlazorCN.
  Browser-only; talks to `Cnas.Ps.Api` strictly over HTTP/REST. Localised in `ro` / `en` / `ru`
  via `Resources/*.resx`, WCAG 2.1 AA baseline, *Modelul Unitar de Design* compliant.
- **`tests/`** — xUnit + FluentAssertions + NSubstitute + NetArchTest. Per-layer test
  projects plus `Cnas.Ps.Architecture.Tests/` that asserts the layer-boundary rules and
  `Cnas.Ps.E2E.Tests/` with the per-UC `WebApplicationFactory`-driven journeys.
- **`ops/`** — `Dockerfile.api` and `Dockerfile.web` (non-root, multi-stage), local
  `docker-compose.yml` (Postgres + PgBouncer + MinIO + api + web + mailhog), and the
  production Helm chart at `ops/k8s/cnas-ps/`.
- **`tor/TOR.md`** — Full Moldovan technical requirements (Romanian, ~15 k lines). The
  functional source of truth.

## How it works

### Citizen flow

A citizen browses the public site at `/`, served by the Blazor WASM bundle. Anonymous
endpoints under `/api/public/*` are rate-limited by the `Anonymous` partition (5 req / 60 s,
IP-partitioned, X-Forwarded-For-aware) and gated by a Cloudflare Turnstile CAPTCHA via
`[RequireCaptcha]` on `PublicController`. Once the citizen signs in through MPass, the
session becomes cookie-authenticated and traffic flows through `/api/applications/*` etc.
into use-case services in the Application layer, which call EF Core through `ICnasDbContext`
(read/write primary) or `IReadOnlyCnasDbContext` (streaming-replication replica, used by
`ReportingService` and `DataSearchService` today — more services flip over time, each one
requiring a read-your-own-writes audit first). Every Postgres connection goes through
**PgBouncer in transaction-pooling mode**; the per-pod Npgsql cap is `MaxPoolSize=2000`
(TOR PSR 003).

### Staff flow

Examiner / supervisor browsers authenticate against MPass and present a cookie-backed
session. Controllers (`ExaminationController`, `ApplicationsController`,
`PendingAdminActionsController`, `UsersController`, …) call workflow + audit + document
services in the Infrastructure layer. The unhandled-exception middleware sits first in the
pipeline (`ApiCompositionRoot.UseCnasApiPipeline`) so any uncaught exception becomes a
`500 application/problem+json` with `errorCode=INTERNAL_ERROR` + correlation id — stack
traces are logged server-side only, never on the wire (SEC 057). Note: the current sign-in
wiring still uses `AddOpenIdConnect` as a placeholder — the SAML middleware swap is gated on
the MEGA cert procurement track (see [`docs/EGOV-INTEGRATION-GAP.md`](docs/EGOV-INTEGRATION-GAP.md)
§MPass).

### Async flow

Quartz jobs run cross-cutting work that does not belong in the request lane:

- `MPayDispatcherJob` — drains the approved-but-not-yet-paid queue every 5 minutes.
- `MConnectSyncJob` — refreshes stale `InsuredPerson` rows from RSP daily at 03:00 UTC.
- `DossierSlaMonitorJob` — flags overdue `WorkflowTask` rows every 15 minutes.
- `MakerCheckerExpirySweeper` — flips Pending → Expired on stale 4-eyes actions.
- `FailedJobListener` — every job firing runs through this; failures are persisted as
  `FailedJob` rows in the dead-letter queue, queryable + replayable via `AdminController`.

### Cross-cutting concerns

Every external identifier on the wire is **Sqid-encoded**; internal code uses raw `long`
(CLAUDE.local.md RULE 3). Sensitive personal identifiers (`Solicitant.NationalId`,
`Contributor.Idno`, `InsuredPerson.Idnp`, `UserProfile.NationalId`,
`Solicitant.BankIban`, `Solicitant.PhoneE164`, `UserProfile.PhoneE164`) are encrypted
**AES-256-GCM** at rest; for the three identifier columns a **HMAC-SHA256** hash shadow
backs an indexed equality lookup. Structured logs carry a correlation id on every request.
Rate limiting partitions traffic into `Anonymous` (per-IP, 5/60 s), `Callback` (per-IP,
60/60 s), `Upload` (per-user, 10/60 s queue 2), `Authenticated` (per-user, 200/60 s queue
10), plus a process-wide 500-concurrent + 1000-queued ceiling.

## What's done

| Area | Status | Evidence |
|---|---|---|
| Layered architecture + arch tests | ✓ | 15/15 in `tests/Cnas.Ps.Architecture.Tests/` |
| Result pattern + ErrorCodes | ✓ | `src/Cnas.Ps.Core/Common/` |
| Sqids at the boundary | ✓ | `ISqidService`, `IExternalId` on 13 entities, `ExternalIdContractTests` |
| `ICnasTimeProvider` everywhere | ✓ | `TimeProviderUsageTests` forbids raw `DateTime.UtcNow` outside the abstraction |
| Value objects (Idnp/Idno/Money/DateRangeUtc/PercentRate/PhoneE164/IbanMd) | ✓ | `src/Cnas.Ps.Core/ValueObjects/`, 109 unit tests |
| AES-256-GCM field encryption + HMAC-SHA256 hash shadows | ✓ | `AesFieldEncryptor`, `Hmac256Hasher`, applied to IDNP / IDNO / NationalId with unique indexes |
| MinIO file storage with magic-byte validation | ✓ | `IFileStorage`, SEC 010 magic-byte validation |
| Rate limiting (Anonymous / Authenticated / Callback / Upload + global ceiling) | ✓ | `RateLimitingComposition` + `RateLimitingPolicies` |
| Cloudflare Turnstile CAPTCHA on anonymous surface | ✓ | R0035 — `TurnstileCaptchaVerifier`, `[RequireCaptcha]` on `PublicController` |
| Argon2id password hashing (PHC format) + policy validator | ✓ | R0052 — `Argon2idPasswordHasher`, OWASP 2024 params, `PasswordPolicyValidator` |
| JWT access + opaque refresh tokens (rotation + family reuse detection) | ✓ | R0053 — `IJwtTokenIssuer`, `RefreshTokenService`, `RefreshToken` entity |
| 4-eyes maker-checker for sensitive admin actions | ✓ | R0058 — `PendingAdminAction`, `IPendingAdminActionService`, `MakerCheckerExpirySweeper` |
| Account state machine (Active / Suspended / Disabled / Locked) | ✓ | R0059 — `UserAccountState` enum + transition matrix + audit per transition |
| Tiered RBAC (`CnasUser` < `CnasDecider` < `CnasAdmin`; `CnasTechAdmin` standalone) | ✓ | `AuthorizationComposition` |
| EF Core read-replica routing | ✓ | R0026 — `IReadOnlyCnasDbContext`, `CnasReadOnlyDbContext`, `ReportingService` + `DataSearchService` flipped |
| PgBouncer-aware connection pooling (`MaxPoolSize=2000`) | ✓ | R0025 — `PostgresPoolOptions`, see [`docs/operations.md`](docs/operations.md) §"Database connection pooling" |
| Unhandled-exception middleware (never leaks stack traces) | ✓ | R0033 — `UnhandledExceptionMiddleware`, SEC 057 |
| Quartz background jobs + FailedJob DLQ + admin replay | ✓ | `MPayDispatcherJob`, `MConnectSyncJob`, `DossierSlaMonitorJob`, `MakerCheckerExpirySweeper`, `FailedJobListener` |
| MPay order persistence + idempotent confirm callback | ✓ | `MPayOrder` entity + `IMPayOrderStore`, idempotent `/api/mpay/confirm` |
| Notification delivery tracking | ✓ | `NotificationDeliveryStatus` enum drives Annex 6g report |
| Polly v8 retry + circuit breaker per MGov client | ✓ | `MGovResilienceOptions`, master switch `Cnas:MGov:Resilience:Enabled` |
| 23 UC HTTP surfaces (UC01–UC23, partial UC15/16/17) | ✓ | 23 controllers in `src/Cnas.Ps.Api/Controllers/` |
| 35 Annex 7 DOCX templates | ✓ | `IDocxTemplate` implementations in `src/Cnas.Ps.Infrastructure/Documents/Templates/` |
| OpenTelemetry tracing + metrics + Serilog with correlation ids | ✓ | `ApiCompositionRoot.AddCnasObservability`, OTLP exporter wired |
| Helm chart (API + HPA + Patroni Postgres + MinIO + Ingress + NetworkPolicy) | ✓ | [`ops/k8s/cnas-ps/`](ops/k8s/cnas-ps/README.md) |
| CI pipeline (restore → format → build → test+coverage → coverage gate → SAST → helm-lint) | ✓ | `.github/workflows/ci.yml` |
| Local pre-commit 3-gate (Husky.Net): format → build → fast tests | ✓ | `.husky/pre-commit` + `.config/dotnet-tools.json` |
| i18n (RO / EN / RU) | ✓ | `src/Cnas.Ps.Web/Resources/*.resx` |

## What's pending

The remaining work falls into four buckets. Honest, no euphemism.

### Externally gated on MEGA artefacts (cert + WSDLs + NDAs)

Per [`docs/EGOV-INTEGRATION-GAP.md`](docs/EGOV-INTEGRATION-GAP.md) the code-side effort once
the procurement track unblocks is ~7–10 working days total.

- 🔒 **MPass SAML middleware** — wire SAML 2.0 + X.509 cert; replace `AddOpenIdConnect`
  placeholder. The `MPassSamlOptions` + `MPassSamlAssertionParser` are ready;
  `Egov.Integrations.MPass.Saml` NuGet pulls in once the cert lands. ~0.5–1 day.
- 🔒 **MSign SOAP two-phase** — `PostSignRequest` → browser redirect → `GetSignResponse`,
  replacing the current REST-shaped client. ~1 day.
- 🔒 **MPay SOAP + XML-DSig outbound + REST callback at port 8443** — adds WS-Security
  envelope signing on outbound, exposes `GetOrderDetails` / `ConfirmOrderPayment` REST
  endpoints inbound. ~1–2 days.
- 🔒 **MNotify mTLS + canonical body shape** — switch handler to `X509Certificate2`,
  rewrite request body to the multi-language `subject/body/bodyShort` + typed `recipients[]`
  shape. ~0.5 day.
- 🔒 **MLog `POST /register` + 16-field canonical event shape + optional JOSE/JWS signing** —
  ~0.5 day.
- 🔒 **MConnect SOAP transport per external system (RSP / RSUD / SFS / SIDDCM / PCCM /
  eCMND / SIAÎSȘ / SIVE / SIAAS / FMS / EESSI)** — gated on per-system NDA contracts.
  ~1–2 days per system.
- 🔒 **MConnect Events** — new producer + WSS consumer via `Age.Integrations.MConnect.Events`
  NuGet. The producer side (`IMConnectEventsProducer`) is wired; consumer / hosted service is
  not. ~1–2 days.

**Procurement track** (pure paperwork, no code): submit MEGA integration form → sign contract /
annex → order system certificate at <https://semnatura.md/order/system-certificate> (one for
staging, one for production) → send `.cer` public key to `servicii@egov.md` → MEGA configures
staging within 7 working days.

### Pending design / decision

- 🟡 **Local username/password login wiring (R0051)** — Argon2id hasher exists and is
  PHC-formatted; the login endpoint that calls into it is deferred. Today `AuthController`
  surfaces password-grant as `501 Not Implemented`.
- 🟡 **Redis-backed session store + concurrent-session limits (R0054)** — cookie idle
  timeout is in place (15 min); the Redis store + per-user concurrent cap and explicit
  `/lock` endpoint are not.
- 🟡 **ABAC expression engine + Groups (R0056)** — RBAC is done (4 policies); attribute /
  expression-based geography/subdivision/document-category rules and the `UserGroup` entity
  are not.
- 🟡 **Delegation lifecycle UI (R0057)** — `ICallerContext.DelegationPowerId` surfaces the
  claim today; the entity, lifecycle, and admin UI to grant / revoke delegations are not
  built.
- 🟡 **`PendingAdminAction` retrofit through real destructive actions (R0058 retrofit)** —
  `NoOpDemoExecutor` is the only executor wired today (`DEMO.NOOP`). The first real
  destructive admin action that routes through the 4-eyes queue is deferred.

### Mechanical / pure-code work

- ⬜ **NameRo/Ru/En sweep across all user-facing entities (R0027)** —
  `ServicePassport`, `Classifier`, `DocumentTemplate` already have it; the remaining
  user-facing entities are pending.
- ⬜ **Flip more services to `IReadOnlyCnasDbContext`** — `PublicContentService`,
  `DashboardService`, the listing branches of `ContributorService` / `InsuredPersonService`,
  and `AuditService` log queries. Each flip requires a read-your-own-writes audit because
  the InMemory test fixture is synchronous and would not catch a production replica-lag
  regression.
- ⬜ **Staging / prod CD pipeline (R0006)** — CI builds and publishes the artefact; the
  staging auto-deploy + post-deploy E2E gate + prod manual-approval stages are not in the
  workflow yet.
- ⬜ **Decision-engine DSL v2 (date-older-than-N-days + nested OR composition)** — needed
  for the 30-day missing-document timer and the pension → alocație-socială cascade.
  Documented as a coherent v2 in `TODO.md` §17 header; do **not** extend
  `JsonRulesDecisionEngine` ad-hoc.

### Configuration-only (GitHub settings, not code)

- ⬜ **Branch protection on `main` + signed-commit enforcement (R0008)** — every CI gate is
  shipped; what remains is marking the jobs as required checks, requiring ≥1 review, and
  enabling signed-commit enforcement on the repository settings page. The full checklist
  lives in [`docs/operations.md`](docs/operations.md) §"GitHub repository settings (R0008)".

For the full reconciled checklist with per-phase counts of `[ ]` / `[~]` / `[x]` items, see
the **Reconciliation summary** block at the bottom of [`TODO.md`](TODO.md).

## Running locally

```bash
# Prerequisites: .NET 10 SDK on PATH.
dotnet --version            # must be 10.x

dotnet restore Cnas.Ps.slnx
dotnet build   Cnas.Ps.slnx -p:TreatWarningsAsErrors=true
dotnet test    Cnas.Ps.slnx
```

Bring the API up directly:

```bash
dotnet run --project src/Cnas.Ps.Api
```

The API applies EF Core migrations to PostgreSQL at startup. To boot without a database
(unit-test parity, sandbox runs), set `Cnas:SkipMigrations=true` — see
[`src/Cnas.Ps.Api/Program.cs`](src/Cnas.Ps.Api/Program.cs).

For the full integrated stack (Postgres + PgBouncer + MinIO + API + WASM front-end) use the
compose file:

```bash
cd ops && docker compose up --build
```

For local development the Cloudflare Turnstile gate is bypassed by setting
`Cnas:Captcha:Turnstile:BypassForTesting=true` — the same flag the E2E `ApiHostFixture`
uses. Production and staging set it to `false` and load the `SecretKey` from Vault or a
Kubernetes Secret. See [`docs/operations.md`](docs/operations.md) §"Configuration reference"
for the full options matrix.

## CI / tests / coverage

The pipeline lives in [`.github/workflows/ci.yml`](.github/workflows/ci.yml): restore →
format check → build with `TreatWarningsAsErrors` → test → coverage gate → SAST → helm-lint
→ publish artefact. The coverage collector is configured by
[`coverlet.runsettings`](coverlet.runsettings) which excludes EF Core migrations,
`*.Designer.cs`, source-generator output, and `GlobalUsings.g.cs` so the threshold tracks
code we authored. The threshold is currently **80 %** (`COVERAGE_THRESHOLD` in the workflow);
the ratchet target for UAT 005 is 90 %.

Current snapshot: **1863 tests pass, 1 skipped, 0 failed**, across 7 test projects (`Core`,
`Application`, `Infrastructure`, `Api`, `Web`, `Architecture`, `E2E`). The single skipped
test is `StaffLoginPageJourneyTests.StaffLoginPage_RendersAndPromptsForMPass`; the skip
message explains the three upstream gates (MPass SAML middleware, the missing
`StaffLogin.razor` page, and the E2E fixture not hosting Blazor). It is intentional rather
than aspirational coverage — when the SAML cert lands and the page exists, the skip flips
to a real assertion in a single commit.

## Where to learn more

- [`CLAUDE.local.md`](CLAUDE.local.md) — engineering rules: TDD first, full XML documentation, Sqids
  at every boundary, Result pattern, UTC everywhere. Cardinal rules and the red-flag
  checklist. *Local, gitignored — see `.gitignore`.*
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — layer map, cross-cutting concerns, MGov
  integration surface, worked data-flow examples, the "What's NOT here" section.
- [`docs/DevOps.md`](docs/DevOps.md) — top-level DevOps orientation: build, CI/CD,
  environments, secrets, observability, backups, DR. Single index that points at
  every operational document.
- [`docs/operations.md`](docs/operations.md) — full operations runbook: deployment, health
  endpoints, configuration reference, secrets matrix, runbook, the PgBouncer + read-replica
  sections, the GitHub repository-settings checklist.
- [`docs/features/`](docs/features/README.md) — **one .md per feature area** (public portal,
  applications, examination, decisions, payments, workflows, reporting, MGov integration,
  notifications, audit, identity-access, etc.). Start here when you're working on a
  single business module.
- [`docs/roles/`](docs/roles/README.md) — **one .md per TOR-defined work role** (UI / UA /
  SOL / UCNAS / SD / SC / AS / AT). Start here to understand who is allowed to do
  what.
- [`docs/EGOV-INTEGRATION-GAP.md`](docs/EGOV-INTEGRATION-GAP.md) — canonical gap analysis
  between the MGov adapters as shipped and the official MEGA spec (5 critical refactors + 2
  new services + procurement track).
- [`ops/k8s/cnas-ps/README.md`](ops/k8s/cnas-ps/README.md) — Helm chart reference (values,
  secrets, upgrade flow, troubleshooting matrix).
- [`TODO.md`](TODO.md) — R-coded requirement checklist mapped to TOR clauses. The
  **Reconciliation summary** block at the bottom is the most useful single page — it gives
  per-phase counts of `[ ]` / `[~]` / `[x]` items and an explicit "externally gated" list
  pointing at the EGov gap document.
- [`tor/TOR.md`](tor/TOR.md) — official functional requirements (Romanian). Use-case table
  at lines ~100–135; non-functional codes at ~152–172.

### Where to start, by what you're doing

| You are… | Start with |
|---|---|
| New to the codebase | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) → pick a feature in [`docs/features/`](docs/features/README.md) |
| Building a feature | the feature's doc in [`docs/features/`](docs/features/README.md) + `CLAUDE.local.md` cardinal rules |
| Understanding permissions | [`docs/roles/`](docs/roles/README.md) |
| Deploying / operating | [`docs/DevOps.md`](docs/DevOps.md) → [`docs/operations.md`](docs/operations.md) → [`ops/k8s/cnas-ps/README.md`](ops/k8s/cnas-ps/README.md) |
| Tracking outstanding work | [`TODO.md`](TODO.md) §"Reconciliation summary" |
| Working on MGov integration | [`docs/EGOV-INTEGRATION-GAP.md`](docs/EGOV-INTEGRATION-GAP.md) + [`docs/features/mgov-integration.md`](docs/features/mgov-integration.md) |

## Contributing

Every change must satisfy the cardinal rules in [`CLAUDE.local.md`](CLAUDE.local.md). The short version,
for the impatient:

1. **TDD first.** Failing test, then implementation, then refactor. No exceptions
   (CLAUDE.local.md RULE 1).
2. **Full XML documentation** on every public type, method, and property —
   `<GenerateDocumentationFile>true</GenerateDocumentationFile>` is set in
   [`Directory.Build.props`](Directory.Build.props) and CS1591 is enforced
   (CLAUDE.local.md RULE 2).
3. **Sqid-encoded IDs at every system boundary**; raw `int` / `long` only inside the
   process (CLAUDE.local.md RULE 3).
4. **0 warnings, 0 errors**: `TreatWarningsAsErrors=true` is set globally; the build will
   reject your PR if you suppress without justification.
5. **All tests green** locally — `dotnet test Cnas.Ps.slnx` — before opening the PR; the
   CI gate is non-negotiable.

For the full rule set including layer-boundary enforcement, secrets handling, and the
Day-1 checklist, see [`CLAUDE.local.md`](CLAUDE.local.md).

### Pre-commit hooks

The local pre-commit chain is auto-installed on first restore by the `HuskyInstall`
target in [`Directory.Build.props`](Directory.Build.props): a plain
`dotnet restore` (or any `dotnet build`, which restores transitively) runs
`dotnet tool restore` followed by `dotnet husky install` for you. You only need to
intervene manually if the auto-install was skipped (e.g. you set `HUSKY=0`):

```bash
dotnet tool restore
dotnet husky install
```

The gates themselves live in [`.husky/task-runner.json`](.husky/task-runner.json) —
single source of truth, grouped under `pre-commit`. The wrapper at
[`.husky/pre-commit`](.husky/pre-commit) delegates to
`dotnet husky run --group pre-commit`, which executes:

1. **`format-staged-cs`** — `dotnet format --verify-no-changes` (read-only style check).
2. **`build-warnings-as-errors`** — `dotnet build -p:TreatWarningsAsErrors=true`
   (compiler + analyzers).
3. **`run-tests`** — fast unit tests for the `Core`, `Application`, and
   `Architecture` projects.

Husky's task runner aborts on the first failed gate. Slow test projects
(`Infrastructure` with Testcontainers, `Api` with `WebApplicationFactory`, `E2E` with the
full host, `Web` with bUnit) run only in CI. To bypass the chain in a pinch:
`HUSKY=0 git commit -m "…"` — CI will catch anything bypassed locally.

The same gates are enforced in CI by `.github/workflows/ci.yml`
(`format-check`, `build`, `test` jobs); the local chain exists so contributors
catch failures before pushing, not as a substitute for CI.
