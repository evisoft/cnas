# Feature — MGov & external-registry integration

## What it is

The integration layer to the Moldovan government platform (MGov /
AGE) and to the per-domain external registries. All HTTP / SOAP / SAML
adapters live in `Cnas.Ps.Infrastructure/MGov/`; the application layer
sees only `IM*Client` interfaces. Polly v8 retry + circuit breaker
per client, master switch on `Cnas:MGov:Resilience:Enabled`.

## TOR / UC mapping

- **UC14** — Schimb date externe.
- Annex 4 (web services EXPOSED) + Annex 5 (web services CONSUMED).
- TOR clauses: CF 14.*, INT 001–005, MR 014.

## Surface

| Endpoint | Direction | Auth | Limiter |
|---|---|---|---|
| `POST /api/mpay/confirm` | Inbound MPay callback | signed | `Callback` |
| `POST /api/msign/callback` | Inbound MSign callback | signed | `Callback` |
| `GET /api/mpass-saml/login` | Inbound SAML start | none | `Anonymous` |
| `POST /api/mpass-saml/acs` | Inbound SAML assertion | none | `Anonymous` |
| `POST /api/external-source-ingestion-admin/run` | Trigger pull from RSP / RSUD / SFS | `CnasAdmin` | `Authenticated` |
| `GET /api/wsdl-portal` | Outbound — list exposed WSDLs | `CnasAdmin` | `Authenticated` |
| `POST /api/xsd-export` | Outbound — XSD bundle export | `CnasAdmin` | `Authenticated` |
| `POST /api/interop` | Generic interop trigger | `CnasAdmin` | `Authenticated` |
| `POST /api/interop/offline-batch` | Offline batch ingestion | `CnasAdmin` | `Upload` |

## Code map

- Controllers
  - [`MPassSamlController.cs`](../../src/Cnas.Ps.Api/Controllers/MPassSamlController.cs)
  - [`MPayCallbackController.cs`](../../src/Cnas.Ps.Api/Controllers/MPayCallbackController.cs)
  - [`MSignCallbackController.cs`](../../src/Cnas.Ps.Api/Controllers/MSignCallbackController.cs)
  - [`ExternalSourceIngestionAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/ExternalSourceIngestionAdminController.cs)
  - [`WsdlPortalController.cs`](../../src/Cnas.Ps.Api/Controllers/WsdlPortalController.cs)
  - [`XsdExportController.cs`](../../src/Cnas.Ps.Api/Controllers/XsdExportController.cs)
  - [`Interop/InteropController.cs`](../../src/Cnas.Ps.Api/Controllers/Interop/InteropController.cs)
  - [`Interop/OfflineBatchController.cs`](../../src/Cnas.Ps.Api/Controllers/Interop/OfflineBatchController.cs)
  - [`Interop/OfflineBatchAdminController.cs`](../../src/Cnas.Ps.Api/Controllers/Interop/OfflineBatchAdminController.cs)
- Application abstractions — in `Cnas.Ps.Application.External` and
  `Cnas.Ps.Application.Abstractions.MGovClients`.
- Infrastructure clients (`src/Cnas.Ps.Infrastructure/MGov/`)
  - `MSignClient`, `MPayClient`, `MConnectClient`, `MNotifyClient`,
    `MLogClient`, `MConnectEventsProducer`, `MDocsClient`,
    `MCabinetPublisher`.
  - External registries (`MGov/External/`):
    `RspClient`, `RsudClient`, `SfsClient`, `EcmndClient`, `SiveClient`,
    `SiddcmClient`, `SiaIssClient`, `SiaasClient`, `PccmClient`,
    `EessiClient`, `FmsClient`.

## Clients at a glance

| Client | What it does | TOR clause |
|---|---|---|
| **MPass** (SAML, claim-based) | Citizen / staff sign-in. **Not** an HTTP service. | SEC 014 |
| **MSign** | Qualified digital signature on outbound documents. | UC11 / MR |
| **MPay** | Outbound payment dispatch + inbound confirmation callback. | UC21 / MR |
| **MConnect** | Pull-style data exchange with RSP / RSUD / SFS / SIDDCM / PCCM / eCMND / SIAÎSȘ / SIVE / SIAAS / FMS / EESSI. | UC14 |
| **MConnectEvents** | Push CloudEvents from registry changes. | UC14 |
| **MNotify** | Citizen + staff notifications (email, SMS, push). | UC22 |
| **MLog** | Centralised audit-log mirroring. | SEC 038–043 |
| **MCabinet** | Publish application status to citizen e-cabinet. | UC06 / UC13 |
| **MDocs** | Document repository for citizen artefacts. | UC11 |

## Business rules

- MPass is a SAML SP — user's IDNP, full name, role, and MPower
  delegation are SAML attributes. MPower is **not** a separate HTTP
  service (common misconception).
- Every callback endpoint is **idempotent** by external transaction id
  (CLAUDE.md "Idempotent callbacks").
- All outbound clients are wrapped in Polly retry + circuit breaker.
  Failures land in `FailedJob` for replay.
- mTLS required for MNotify outbound (`Cnas:MGov:Mtls:MNotify`).
- All outbound IPs are allow-listed at the egress gateway in
  production — registry traffic never reaches the public internet.

## Externally-gated work

Per [`../EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md), code-side
effort once procurement unblocks is ~7–10 working days:

1. MPass SAML middleware (replace `AddOpenIdConnect` placeholder).
2. MSign SOAP two-phase (`PostSignRequest` → redirect → `GetSignResponse`).
3. MPay SOAP + XML-DSig outbound + REST callback at port 8443.
4. MNotify mTLS + canonical body shape.
5. MLog `POST /register` + 16-field canonical event + optional JOSE/JWS.
6. MConnect SOAP per external system (gated on per-system NDAs).
7. MConnectEvents WSS consumer.

## Tests

- `tests/Cnas.Ps.Infrastructure.Tests/MGov/`
- `tests/Cnas.Ps.E2E.Tests/Journeys/PlatformIntegrationTests.cs`

## What's NOT here

- The procurement track (system certificate ordering, MEGA contract
  paperwork) — see [`../EGOV-INTEGRATION-GAP.md`](../EGOV-INTEGRATION-GAP.md).
