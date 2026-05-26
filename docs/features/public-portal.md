# Feature — Public portal

## What it is

Anonymous, public-facing surface of SI „Protecția Socială". Lets any
citizen browse social-service descriptions, read public help content,
calculate eligibility (retirement age, application status), and
access translated content — without authenticating. Behind Cloudflare
Turnstile so it is robot-hardened, behind the `Anonymous` rate-limit
partition so it is DoS-hardened.

## TOR / UC mapping

- **UC01** — Explorez conținutul portalului.
- **UC02** — Servicii informative (eligibility, retirement-age,
  application-status calculators).
- TOR clauses: CF 01.*, CF 02.*, UI 001–016, SEC 008, SEC 035.

## Surface

| Endpoint | Auth | Limiter | CAPTCHA |
|---|---|---|---|
| `GET /api/public/services` | none | `Anonymous` | yes |
| `GET /api/public/services/{code}` | none | `Anonymous` | yes |
| `GET /api/public/help/topics` | none | `Anonymous` | yes |
| `GET /api/public/calculate/retirement-age` | none | `Anonymous` | yes |
| `GET /api/public/calculate/application-status` | none | `Anonymous` | yes |
| `GET /api/public/translations/{culture}` | none | `Anonymous` | no |
| `GET /api/public/captcha/sitekey` | none | `Anonymous` | no |
| `POST /api/public/prefill/initial` | none | `Anonymous` | yes |
| `GET /api/public/catalog` | none | `Anonymous` | yes |

`Anonymous` = 5 req / 60 s per resolved IP (XFF-aware). CAPTCHA is
enforced by `[RequireCaptcha]` at the `PublicController` class level —
clients send `X-Captcha-Token`. Failure modes:
`CAPTCHA.TOKEN_MISSING` → 400, `CAPTCHA.TOKEN_INVALID` → 400,
`CAPTCHA.PROVIDER_UNREACHABLE` → 503 (fail-closed).

## Code map

- Controllers
  - [`PublicController.cs`](../../src/Cnas.Ps.Api/Controllers/PublicController.cs)
  - [`PublicCatalogController.cs`](../../src/Cnas.Ps.Api/Controllers/PublicCatalogController.cs)
  - [`PublicServicesController.cs`](../../src/Cnas.Ps.Api/Controllers/PublicServicesController.cs)
  - [`HelpPublicController.cs`](../../src/Cnas.Ps.Api/Controllers/HelpPublicController.cs)
  - [`CaptchaController.cs`](../../src/Cnas.Ps.Api/Controllers/CaptchaController.cs)
  - [`TranslationsPublicController.cs`](../../src/Cnas.Ps.Api/Controllers/TranslationsPublicController.cs)
  - [`PrefillController.cs`](../../src/Cnas.Ps.Api/Controllers/PrefillController.cs)
- Application services
  - `IPublicContentService` — service catalogue + help topics.
  - `IInformationServices` — UC02 calculators.
  - `ICaptchaVerifier` — Turnstile abstraction.
- Infrastructure
  - [`TurnstileCaptchaVerifier.cs`](../../src/Cnas.Ps.Infrastructure/Security/TurnstileCaptchaVerifier.cs)
  - `PublicContentService` (uses `IReadOnlyCnasDbContext` — replica).
- Filter
  - [`RequireCaptchaAttribute.cs`](../../src/Cnas.Ps.Api/Filters/RequireCaptchaAttribute.cs)

## Data model

| Entity | Role |
|---|---|
| `ServicePassport` | Source of the public service catalogue rows. |
| `HelpTopic` + `HelpTopicTranslation` | Multi-locale help content. |
| `Classifier` | Lookup tables exposed read-only for the calculators. |

All reads go through `IReadOnlyCnasDbContext` (replica) — public reads
must never put load on the write primary.

## Business rules

- Anonymous endpoints never echo internal IDs. All IDs in responses
  are Sqid-encoded.
- The CAPTCHA token is **never logged** in any form, raw or hashed —
  Cloudflare's terms require this. The `TurnstileCaptchaVerifier`
  joins provider `error-codes` into a sanitised message and discards
  the token before returning.
- `PrefillController` accepts anonymous IDNP + optional MPass session
  hint to pre-fill an application form. Rate-limited on `Anonymous` so
  it cannot be abused as an enumeration oracle.
- Bypass for tests: `Cnas:Captcha:Turnstile:BypassForTesting=true`
  short-circuits the verifier — the E2E `ApiHostFixture` sets this.

## Tests

- `tests/Cnas.Ps.Api.Tests/Filters/RequireCaptchaAttributeTests.cs`
- `tests/Cnas.Ps.Infrastructure.Tests/Security/TurnstileCaptchaVerifierTests.cs`
- `tests/Cnas.Ps.E2E.Tests/Journeys/PublicJourneyTests.cs`

## What's NOT here

- Editorial CMS for help content — content arrives via DB migrations or
  the help-admin endpoints; there is no rich-text editor.
- Search box on the public portal — global search is authenticated
  only (see [`reporting.md`](reporting.md)).
