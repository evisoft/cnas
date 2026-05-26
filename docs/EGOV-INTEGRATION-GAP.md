# EGov MGov Integration — Architectural Gap Analysis

> Source of truth: <https://github.com/EvisoftSRL/tender/blob/main/docs/EGov%20and%20other%20extrernal%20systems%20integration%20Guide.md>
> Captured: 2026-05-19. Owner: SI „Protecția Socială" (CNAS) integration team.

## TL;DR

The MGov client implementations currently in `src/Cnas.Ps.Infrastructure/MGov/*` use plausible-but-invented REST/JSON endpoints with `Bearer <token>` auth. The official MEGA integration spec mandates substantially different protocols, transports, and authentication for every service. Five of the seven services we shipped need protocol-level refactor before they will speak to real MGov endpoints; two are conceptually wrong (MPower is not a separate HTTP service, and MConnect Events is a CloudEvents/WebSocket service we have not implemented at all).

The Decision Engine, application lifecycle, registries, UI, background jobs, and architecture tests are **NOT affected** — they consume MGov via the `IM*Client` abstractions in `src/Cnas.Ps.Application/Abstractions/MGovClients.cs`, so the refactor is contained to the Infrastructure adapter layer + the auth wiring in `src/Cnas.Ps.Api/Composition/AuthenticationComposition.cs`.

## Service-by-service gap

### MPass — CRITICAL refactor

| Field | Today (wrong) | Real spec |
|---|---|---|
| Protocol | OIDC (cookie + OpenIdConnect challenge) | **SAML 2.0** (HTTP POST binding) |
| Auth | Bearer client_id/client_secret | **X.509 client certificate**, signed `AuthnRequest` |
| Endpoints | OIDC issuer URL | `/login/saml`, `/logout/saml`, `/meta/saml` |
| Staging URL | configured `MPassIssuer` | `https://mpass.staging.egov.md` |
| Production URL | configured `MPassIssuer` | `https://mpass.gov.md` |
| NuGet package | `Microsoft.AspNetCore.Authentication.OpenIdConnect` | **`Egov.Integrations.MPass.Saml`** |
| Returned attributes | `sub`, `role`, `groups` claims | SAML assertion with custom attributes (IDNP, name, organisation, role list, eID method) |
| Logout | OIDC end_session | Single Logout (SLO) flow |

**Refactor plan (next session):**
1. Replace `AddOpenIdConnect` in `AuthenticationComposition.cs` with the SAML middleware from the `Egov.Integrations.MPass.Saml` package.
2. Wire X.509 cert (from `semnatura.md/order/system-certificate`) — separate cert for staging vs prod.
3. Translate SAML assertion attributes → `ClaimTypes.Role` via the existing claim-map in `AuthenticationComposition.cs` (the map is reusable; the source changes).
4. Keep `IUserDirectoryService.UpsertOnSignInAsync` as-is — it takes a generic `ClaimsPrincipal` and works the same way regardless of cookie source.

### MSign — CRITICAL refactor

| Field | Today | Real spec |
|---|---|---|
| Protocol | REST/JSON `POST /api/v1/sign` | **SOAP/WS-I Basic Profile 1.1** |
| Auth | Bearer | **X.509 client certificate** |
| Flow | One synchronous HTTP call returning signature | **Two-phase**: `PostSignRequest` (SOAP) → returns `requestID` → redirect user browser to `https://msign.gov.md/{requestID}?ReturnUrl=...` → MSign POSTs back → call `GetSignResponse(requestID)` to retrieve signature |
| Content options | bytes only | Hash mode (SHA-1 of doc) OR PDF mode (server signs entire PDF) |
| URL | configured `MSignBaseUrl` | `https://msign.gov.md/MSign.svc?singleWsdl` |
| NuGet | none | **`Egov.Integrations.MSign.Soap`** |

**Implication:** the synchronous `IMSignClient.SignAsync` contract is wrong. Need to split into:
- `PostSignRequestAsync(...)` → returns `requestId`
- A new browser-redirect endpoint on the API: `GET /api/msign/return/{requestId}` that MSign calls back
- `GetSignResponseAsync(requestId)` → returns the signature bytes

The existing `IMSignClient` abstraction needs to evolve. Callers (`DocumentGenerationService` is the main one once signing-on-decision is wired) need to be updated.

### MPay — CRITICAL refactor

| Field | Today | Real spec |
|---|---|---|
| Protocol | REST `POST /api/v1/payments` | **SOAP/WS-Security** for outbound + **REST callback** for inbound + browser redirect to `/service/pay` |
| Auth | Bearer | **Mutual TLS (client cert) + X.509 XML Signature in SOAP message** |
| Flow | Single outbound send | **Callback pattern**: we expose `GetOrderDetails` and `ConfirmOrderPayment` REST endpoints, MPay calls us; for outbound payments the flow is similar but in the other direction |
| Staging URL | configured | `https://testmpay.gov.md` |
| Production URL | configured | `https://mpay.gov.md` |
| REST port | 443 | **8443** |
| WSDL | n/a | request from `suport.mpay@gov.md` (gated) |

**Implication:** need to:
1. Add `GetOrderDetails` + `ConfirmOrderPayment` controllers on `Cnas.Ps.Api` (idempotent — current code is not).
2. Replace `MPayClient.SendAsync` REST body with SOAP envelope + XML-DSig signature.
3. Add WS-Security infrastructure (BouncyCastle + System.Security.Cryptography.Xml).

### MNotify — MEDIUM refactor (right protocol, wrong endpoints)

| Field | Today | Real spec |
|---|---|---|
| Protocol | REST/JSON ✓ | REST/JSON ✓ |
| Auth | Bearer | **X.509 client cert** (serial number) |
| Endpoint | `POST /api/v1/dispatch` | `POST /api/Notification` (singular) |
| URL | configured `MNotifyBaseUrl` | `https://mnotify.gov.md:8443/api/` |
| Body shape | `{ recipientIdnp, channel, templateCode, parameters }` | Multi-language `subject` + `body` + `bodyShort` dicts + `recipients[].type` ∈ {`email`, `IDNP`, `msisdn`} + optional `attachments[]` with base64 content |
| Templates | external responsibility | Has full `/api/Template/*` CRUD |
| Swagger | n/a | `https://mnotify.staging.egov.md:8443/api/swagger/index.html` |

**Refactor:**
1. Switch HttpClientHandler to mTLS via `X509Certificate2`.
2. Rewrite `MNotifyClient.SendAsync` body shape per the canonical `NotificationRequest`/`Recipient`/`Attachment` records.
3. Optionally add template CRUD (separate `IMNotifyTemplateClient` or extend `IMNotifyClient`).

### MLog — MEDIUM refactor (right protocol, much richer event shape)

| Field | Today | Real spec |
|---|---|---|
| Protocol | REST/JSON ✓ | REST/JSON ✓ |
| Auth | Bearer | **X.509 cert (fingerprint)** |
| Endpoint | `POST /api/v1/journal/append` | `POST /register` |
| Query | n/a | `GET /query`, `GET /query/{uid}` (event lookup) |
| URL | configured | `https://mlog.gov.md:8443/` |
| Event shape | `{ EventCode, ActorId, TargetEntity, TargetEntityId, DetailsJson }` | **Much richer**: `event_time`, `event_type` (`System.X.Y`), `event_id`, `event_correlation`, `event_level`, `event_source`, `event_message`, `event_details`, `legal_entity`, `legal_basis`, `legal_reason`, `user`, `user_session`, `user_address`, `subject`, `object`. Optional **JOSE/JWS signing** of events. |

**Refactor:**
1. mTLS via cert fingerprint.
2. Replace the `MLogEntry` record with the canonical 16-field shape.
3. Add optional JOSE/JWS signing for events that require legal-grade tamper proof.

### MConnect — UNKNOWN (NDA-protected)

| Field | Today | Real spec |
|---|---|---|
| Protocol | REST/JSON | **SOAP/REST hybrid, XML signature** |
| Auth | Bearer | **Signature-based** (X.509) |
| Endpoints | `POST /api/v1/services/{code}/call` | NDA — comes with each individual MConnect contract for each external system (RSP, RSUD, SFS, ...) |

**Action:** The 11 typed facades the current session added (`IRspClient`, `IRsudClient`, `ISfsClient`, etc.) keep their domain shapes but the underlying transport in `MConnectClient.CallAsync` will need to be rewritten once the per-system contracts are obtained from MEGA. The interface boundary is correct; the implementation needs to be replaced.

### MConnect Events — NEW SERVICE TO IMPLEMENT

We have NOT implemented this at all. The official spec uses:
- **CloudEvents v1.0** over HTTP (producer) + **WebSocket** (recommended consumer) or long-polling (fallback).
- TLS 1.2+ with X.509 client certificate.
- NuGet: `Age.Integrations.MConnect.Events`.
- URLs: `https://mconnect-events.staging.egov.md:8443/` / `https://mconnect-events.gov.md:8443/`.
- Producer endpoints: `POST /ce/produce/raw`, `POST /ce/produce/event`, `POST /ce/produce/events` (batch, recommended).
- Consumer endpoints (WSS): `WSS /ce/consume/ws` with sub-protocol `cloudevents.json`.
- Long-polling fallback: `POST /ce/consumers`, `GET /{bridge}/ce/consumers/{group}/instances/{instance}/events`, etc.

**Action:** introduce `IMConnectEventsProducer` + `IMConnectEventsConsumer` abstractions, hosted-service implementation for the consumer, and the `Age.Integrations.MConnect.Events` NuGet package.

### MDocs — NEW SERVICE TO IMPLEMENT

We have NOT implemented this. Real spec:
- REST/JSON.
- Auth = **Client certificate + MPass token** (so it pulls a token from MPass and forwards).
- Use case: managed document storage, signing co-ordination, version history.

**Action:** introduce `IMDocsClient` if/when document workflows need it (current `IFileStorage` → MinIO might be sufficient for v1).

### MPower — WRONG MENTAL MODEL

Today: we implemented `IMPowerClient` as a standalone REST service with its own bearer + base URL.

Real spec: **MPower is NOT a separate HTTP endpoint.** It is consumed indirectly via **MPass claims** — when a user authenticates with MPass while acting on someone's behalf, the SAML assertion carries a `MPower:Delegation` attribute (or similar) describing the delegation.

**Refactor:**
1. Delete `IMPowerClient` / `MPowerClient` / `MGovOptions.MPowerBaseUrl` and `MPowerBearer`.
2. Add `principalIdnp` + `delegationPowerId` claims to the user's principal at sign-in (in the MPass SAML handler, once MPass-SAML is in place).
3. In `ApplicationServiceImpl.SubmitAsync`, instead of calling `_mpower.VerifyAsync(...)`, read the delegation claim from `ICallerContext`. If `OnBehalfOfPrincipalIdnp` was supplied but the principal's claim does not authorise it, fail with `MPowerNotAuthorized`.
4. The 11 MPower-related tests need to be rewritten as claim-based assertions instead of HTTP mocks.

## Auth model: universal — needs central refactor

Across MSign / MPay / MNotify / MLog / MConnect / MConnect-Events, the universal pattern is **X.509 client certificate authentication** (mutual TLS) — typically with a different cert per environment (staging vs production).

Today we wire `Authorization: Bearer <token>` headers. After refactor:

1. Add an `ICertificateStore` abstraction in Application layer.
2. In Infrastructure, register a delegating handler that loads the appropriate cert per service and attaches it to the underlying `SocketsHttpHandler.SslOptions.ClientCertificates`.
3. Move cert paths into `MGovOptions` (per-service `CertPath` + `CertPassword`).
4. Remove all the `*Bearer` options.

The `MGovHttp.cs` shared helper added during MGov-client work is a good seam — that's where the auth swap happens.

## Connection prerequisite (real-world)

Per the integration guide, before any of this is testable end-to-end:
1. Submit MEGA integration form (URL in guide).
2. Sign contract/annex with MEGA.
3. Order a system certificate from <https://semnatura.md/order/system-certificate> (separate cert for staging + production).
4. Send `.cer` public key to `servicii@egov.md`.
5. MEGA configures staging within 7 working days.

This is a procurement/legal task, not a code task; the codebase only needs to be ready to accept the configured certs.

## Tech stack alignment notes

The integration guide describes a recommended target stack:
- .NET 10 LTS ✓ (we match)
- Blazor Server for internal UI (we use Blazor WASM — both are valid; revisit before production)
- PostgreSQL 16+ ✓
- Redis 7+ (not yet wired; SI PS does not require caching yet)
- Apache Kafka 3.x (not in scope — we use Quartz scheduled jobs)
- Elasticsearch 8.x (planned but not wired)
- MinIO ✓
- **Workflow: Operaton (BPMN 2.0)** — we have `WorkflowCode` placeholder; full Operaton integration is a separate epic
- YARP API gateway (not in scope)
- OpenTelemetry + Grafana — Serilog wired; OTel exporter exists in `Directory.Packages.props` but not exporting yet
- HashiCorp Vault 1.x — not wired; current dev config uses appsettings + env vars

## Effort estimate (next session)

| Refactor | Estimate |
|---|---|
| MPass OIDC → SAML | 0.5–1 day |
| MSign REST → SOAP two-phase | 1 day |
| MPay REST → SOAP + callback endpoints | 1–2 days |
| MNotify REST refactor + mTLS | 0.5 day |
| MLog endpoint + event-shape refactor + mTLS | 0.5 day |
| MConnect transport rewrite (waits for NDA contract) | 1–2 days |
| MConnect Events (new producer + consumer) | 1–2 days |
| MPower remove + claim-based delegation | 0.5 day |
| Universal mTLS + ICertificateStore | 1 day |
| **Total** | **~7–10 working days** |

## What this session deliberately delivered anyway

Even though the protocol details are wrong, the **architecture, abstractions, and consumer code are right**. The Decision Engine, application lifecycle, registries, audit, scheduler, UI, and 433 Application.Tests are all unaffected by the refactor — they program against `IM*Client` interfaces, and only the implementations behind those interfaces change.

The 22 + 11 + 6 = 39 tests that mock or hit the (wrong) HTTP shapes will need updating, but no business-logic test needs to change.

---

*Captured 2026-05-19 in response to the EvisoftSRL/tender EGov integration guide being shared by the user.*
