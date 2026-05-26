# Architecture overview

> Companion to [`README.md`](../README.md) and [`CLAUDE.md`](../CLAUDE.md).
> This document is the architectural index — it points at the source-of-truth
> code and config, it does not duplicate them.

## Layer map

Code flows in one direction. Inner layers know nothing about outer layers. The
hard boundaries are enforced at build time by
[`tests/Cnas.Ps.Architecture.Tests/`](../tests/Cnas.Ps.Architecture.Tests/) —
in particular `LayerBoundaryTests.cs`, `BoundaryRulesTests.cs`, and
`NamingConventionTests.cs`.

```
                      +-----------------------------+
                      |   Cnas.Ps.Api   (Web SDK)   |
                      |   Cnas.Ps.Web   (Blazor)    |
                      +--------------+--------------+
                                     |
                                     v
                      +-----------------------------+
                      |    Cnas.Ps.Infrastructure   |
                      |   EF Core / MinIO / MGov    |
                      |   Quartz / Security / OTel  |
                      +--------------+--------------+
                                     |
                                     v
                      +-----------------------------+
                      |     Cnas.Ps.Application     |
                      |   UC services, validators,  |
                      |  IM*Client abstractions,    |
                      |  ICnasDbContext, DTOs       |
                      +--------------+--------------+
                                     |
                                     v
                      +-----------------------------+
                      |        Cnas.Ps.Core         |
                      |   Entities / Enums /        |
                      |   Value Objects / Result    |
                      |   (zero external deps)      |
                      +-----------------------------+

                      +-----------------------------+
                      |     Cnas.Ps.Contracts       |
                      |   WASM-safe wire DTOs       |
                      |   (referenced by Web, Api,  |
                      |    Application)             |
                      +-----------------------------+
```

`Cnas.Ps.Contracts` exists as a separate sliver so `Cnas.Ps.Web` (which targets
the Blazor WebAssembly SDK and cannot pull in EF Core, server abstractions, or
the Infrastructure layer) can still share the request / response record
shapes with the API. `Contracts` has zero outbound dependencies; the
architecture tests assert this.

## Cross-cutting concerns

### Sqid encoding (CLAUDE.md RULE 3)

Every external identifier on the wire is a Sqid-encoded `string`. Internal
code uses raw `long` / `int` and encodes only at the API boundary. The
centralised service is `ISqidService` (implementation in
[`src/Cnas.Ps.Infrastructure/Common/SqidService.cs`](../src/Cnas.Ps.Infrastructure/Common/SqidService.cs)),
configured from the `Sqids:Alphabet` and `Sqids:MinLength` settings. The
alphabet and salt are part of the immutable runtime config — changing them
after launch breaks every previously issued external reference.

### Field encryption (CLAUDE.md §5.7 / TOR SEC 035)

Sensitive personal identifiers are encrypted with AES-256-GCM at rest and
shadowed by an HMAC-SHA256 column so equality lookups remain index-backed.
The encryptor is `AesFieldEncryptor` (envelope prefix `v1:` — the prefix
exists as a rotation seam for a future `v2`, but only `v1` is implemented
today). Hashing is `Hmac256Hasher` keyed by `Cnas:FieldHashing:SaltKey`.

Entities with encrypted columns and their hash shadow:

| Entity | Plaintext (encrypted at rest) | Hash shadow column | Unique index |
|---|---|---|---|
| `Solicitant` | `NationalId` | `NationalIdHash` | yes |
| `Contributor` | `Idno` | `IdnoHash` | yes |
| `InsuredPerson` | `Idnp` | `IdnpHash` | yes |
| `UserProfile` | `NationalId` | `NationalIdHash` | no (non-unique index) |

`Solicitant.BankIban`, `Solicitant.PhoneE164`, and `UserProfile.PhoneE164` are
encrypted at rest but currently have no hash-shadow column — equality lookups
against those are not supported by an index today. [verify before relying on
this in new code.]

### Authentication

Production sign-in for CNAS staff is MPass SAML 2.0 (claim-based). The current
codebase carries both wirings during the cut-over to the official MEGA
middleware:

- The composition root in
  [`src/Cnas.Ps.Api/Composition/AuthenticationComposition.cs`](../src/Cnas.Ps.Api/Composition/AuthenticationComposition.cs)
  wires a cookie session and an `AddOpenIdConnect` MPass scheme. This is a
  **placeholder** until the SAML middleware lands (see *What's NOT here*).
- [`src/Cnas.Ps.Infrastructure/MGov/MPassSamlOptions.cs`](../src/Cnas.Ps.Infrastructure/MGov/MPassSamlOptions.cs)
  and `MPassSamlAssertionParser.cs` capture the SAML configuration shape and
  attribute map (IDNP, full name, email, role, MPower delegation claims).
- `MPower` is **not** a standalone HTTP service. The user's principal-IDNP and
  delegation-id arrive as SAML attributes inside the MPass assertion; the
  parser maps them to `mpower:principal_idnp` and `mpower:delegation_id`
  claims. This is a common misconception worth heading off when reading
  existing TOR text.

### Authorization

Tiered RBAC, defined as constants in
[`src/Cnas.Ps.Api/Composition/AuthorizationComposition.cs`](../src/Cnas.Ps.Api/Composition/AuthorizationComposition.cs):

```
CnasUser  <-  CnasDecider  <-  CnasAdmin
CnasTechAdmin   (standalone — infrastructure / system jobs)
```

Higher policies satisfy lower ones (`CnasAdmin` users transparently pass the
`CnasDecider` and `CnasUser` checks). Controllers reference the policy names
through the constants, never as bare role strings. SEC clauses 021–026.

### Rate limiting

`Cnas:RateLimiting` defines four named partition policies plus a process-wide
ceiling, all wired in
[`src/Cnas.Ps.Api/Composition/RateLimitingComposition.cs`](../src/Cnas.Ps.Api/Composition/RateLimitingComposition.cs):

| Policy | Default | Partition key |
|---|---|---|
| `Anonymous` | 5 req / 60 s (SEC 008) | resolved IP (XFF-aware) |
| `Callback` | 60 req / 60 s | resolved IP |
| `Upload` | 10 req / 60 s, queue 2 | authenticated principal id |
| `Authenticated` | 200 req / 60 s, queue 10 | authenticated principal id |
| Global | 500 concurrent + 1000 queued | process-wide |

Authentication runs **before** the limiter (see `UseCnasApiPipeline`) so
user-partitioned policies see the populated principal. The XFF-trust chain
is documented on `RateLimitingOptions`; production must sit behind a proxy
that strips client-supplied `X-Forwarded-For`.

### Resilience

Every MGov-facing `HttpClient` is wrapped in a Polly v8 pipeline (retry with
exponential backoff + circuit breaker) configured by
[`MGovResilienceOptions`](../src/Cnas.Ps.Infrastructure/MGov/MGovResilienceOptions.cs)
under `Cnas:MGov:Resilience`. Each client (`MSign`, `MPay`, `MConnect`,
`MNotify`, `MLog`, `MDocs`, `MConnectEvents`, `MCabinet`) has its own
sub-section so retries can be tuned per upstream without touching code. The
master switch `Cnas:MGov:Resilience:Enabled` exists for emergency disable.

### Observability

OpenTelemetry pipeline registered by `ApiCompositionRoot.AddCnasObservability`:
traces (ASP.NET Core, HttpClient, EF Core), metrics (ASP.NET Core, HttpClient,
runtime), and an optional OTLP/gRPC exporter. EF Core spans deliberately set
`SetDbStatementForText = false` so PII bound in SQL literals never leaves the
process. Serilog handles structured logs with `FromLogContext`, environment
name, and machine name enrichers; request logging via
`UseSerilogRequestLogging`.

### Background jobs

Quartz.NET registered in
[`src/Cnas.Ps.Infrastructure/Jobs/QuartzComposition.cs`](../src/Cnas.Ps.Infrastructure/Jobs/QuartzComposition.cs):

- `DossierSlaMonitorJob` — every 15 minutes; flags overdue `WorkflowTask`
  rows and notifies the assignee.
- `MPayDispatcherJob` — every 5 minutes; drains the approved-but-not-yet-paid
  queue.
- `MConnectSyncJob` — daily at 03:00 UTC; refreshes stale `InsuredPerson` rows
  from RSP.

Every scheduler firing runs through `FailedJobListener`, which persists a row
in the `FailedJobs` dead-letter queue (`Cnas.Ps.Core.Domain.FailedJob`) on
job failure. Operators query and replay the DLQ via
`AdminController.ListFailedJobsAsync` / `ReplayFailedJobAsync` (see
[`AdminController.cs`](../src/Cnas.Ps.Api/Controllers/AdminController.cs)).

## MGov integration surface

All clients are concrete HTTP adapters registered in
`InfrastructureServiceCollectionExtensions.cs`. The interfaces live in the
Application layer (`Cnas.Ps.Application.External` and the
`Cnas.Ps.Application.Abstractions.MGovClients` family) so domain code is
decoupled from transport choice.

| Client | What it does | TOR clause |
|---|---|---|
| **MPass** (SAML, claim-based) | Citizen / staff sign-in. **Not** an HTTP service — consumed via the SAML assertion. | SEC 014 |
| **MSign** | Digital signature on outbound documents (decisions, certificates). | UC11 / MR |
| **MPay** | Outbound payment dispatch + inbound payment confirmation callback. | UC21 / MR |
| **MConnect** | Pull-style data exchange with the SIA RGP / RSP / RAMV registries. | UC14 |
| **MConnectEvents** | Push-style CloudEvents stream from registry change events. | UC14 |
| **MNotify** | Citizen / staff notifications (email, SMS, push). | UC22 |
| **MLog** | Centralised audit log mirroring (SEC 056). | UC23 / SEC 038-043 |
| **MCabinet** | Publish application status to the citizen e-cabinet. | UC06 / UC13 |
| **MDocs** | Document repository for citizen-facing artefacts. | UC11 |

External-registry clients (not part of the MGov platform proper but called by
the same Infrastructure layer):
`RspClient`, `FmsClient`, `SfsClient`, `EcmndClient`, `SiveClient`,
`SiddcmClient`, `SiaIssClient`, `SiaasClient`, `PccmClient`, `EessiClient`,
`RsudClient` — see
[`src/Cnas.Ps.Infrastructure/MGov/External/`](../src/Cnas.Ps.Infrastructure/MGov/External/).

## Data flow examples

### 1. Citizen submits an application (UC06)

```
Browser (Blazor WASM)
  │   POST /api/applications  +  SubmitApplicationInput (JSON, Sqid IDs)
  ▼
ApplicationsController.SubmitAsync          [Cnas.Ps.Api]
  │   maps DTO → service request, surfaces 201 CreatedAtAction
  ▼
IApplicationService.SubmitAsync             [Cnas.Ps.Application]
  │   FluentValidation → business rules → returns Result<ApplicationOutput>
  ▼
ApplicationServiceImpl                      [Cnas.Ps.Infrastructure]
  │   DbContext.Applications.Add + SaveChanges
  │   IAuditService → AuditLog row (immutable snapshot)
  │   INotificationService → MNotify dispatch
  │   IMCabinetPublisher → push status to MCabinet
  ▼
PostgreSQL  +  MinIO (any attachments)  +  MNotify  +  MCabinet
```

### 2. Examiner records a verdict (UC08)

```
Browser (Blazor WASM)
  │   POST /api/examination/documents/{sqid}/verdict  +  VerdictRequest
  ▼
ExaminationController.RecordVerdictAsync    [Cnas.Ps.Api]
  │   @Authorize(Policy = CnasUser), @EnableRateLimiting(Authenticated)
  │   parses verdict enum, maps Sqid → long via ISqidService
  ▼
IDocumentExaminationService.RecordVerdictAsync   [Cnas.Ps.Application]
  │   loads dossier + document, applies workflow transition rule,
  │   returns Result with ErrorCode on failure
  ▼
DocumentExaminationServiceImpl              [Cnas.Ps.Infrastructure]
  │   DbContext.Documents.Update + SaveChanges
  │   IAuditService → AuditLog row
  │   OpenTelemetry counter increments examination.verdicts.recorded
  ▼
PostgreSQL  +  OTLP collector (when configured)
```

## What's NOT here

Honest gap list. New contributors hit these often; better signal than
aspirational prose.

- **MPass SAML middleware is a placeholder.** The composition root still
  wires `AddOpenIdConnect` because we are gated on the MEGA-issued X.509
  certificate for the SAML SP. The `MPassSamlOptions` and assertion parser
  are ready; the middleware swap is the next session. See
  [`docs/EGOV-INTEGRATION-GAP.md`](EGOV-INTEGRATION-GAP.md) "MPass".
- **X.509 XML-DSig signing on outbound SOAP is a placeholder.** `MSignClient`
  and `MPayClient` use REST-shaped HTTP bodies today; the real MEGA spec
  requires SOAP / WS-Security with XML signature. TODO comments in those
  files mark the rewrite scope. Real WSDLs are gated on the
  `suport.mpay@gov.md` request.
- **MConnectEvents is a producer only.** The CloudEvents / WebSocket consumer
  side is not implemented — see `MConnectEventsConsumer.cs`.
- **UC15 / UC16 / UC17 have no HTTP controller yet.** Service-passport admin,
  workflow-configuration admin, and template admin live in the Application
  layer (`IServicePassportService`, `IWorkflowConfigurationService`,
  `ITemplateAdminService` [verify last name]) but are not exposed by a
  controller. The corresponding E2E journey tests are marked `[Fact(Skip = ...)]`
  to keep the suite green without lying about coverage.
- **Local username/password authentication.** SEC 014 reserves a `Local`
  scheme for the single `Utilizator autorizat` account. The scheme name is
  documented in `AuthenticationComposition.cs` but the cookie wiring isn't
  registered yet.
- **`BankIban`, `PhoneE164` fields are encrypted but not hash-shadowed.** If
  you need indexed equality lookup against either, you must add the shadow
  column first; do not introduce a non-indexed scan in a request path.
