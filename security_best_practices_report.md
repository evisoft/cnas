# Security Best Practices Report

Date: 2026-05-25

## Executive Summary

This review used the requested `security-best-practices` workflow, but the skill has no C#/.NET-specific reference files. The repository is primarily .NET 10 / ASP.NET Core / Blazor WASM, so the findings below are based on general web application security review of authentication, anonymous callback surfaces, token/session handling, rate limiting, XML parsing, and deployment defaults.

The highest-risk issues are callback and SAML endpoints that rely on external gateway controls instead of in-process verification. If any of those routes are reachable without the exact expected mTLS/source-IP/signature controls, attackers can mutate payment/notification state or exercise unsigned SAML parsing. Several findings are deployment-dependent, but the code currently fails open when the deployment control is missing or misconfigured.

## Critical

### SEC-001: MPay callback endpoints trust only external gateway controls

Impact: If `/api/mpay/*` is reachable without enforced mTLS/source-IP allow-listing, an attacker can query payment-order details including beneficiary IDNP and can forge payment confirmations.

Evidence:
- [MPayCallbackController.cs:36](src/Cnas.Ps.Api/Controllers/MPayCallbackController.cs:36) documents that MPay authentication is expected at the transport/gateway layer.
- [MPayCallbackController.cs:55](src/Cnas.Ps.Api/Controllers/MPayCallbackController.cs:55) marks the controller `[AllowAnonymous]`.
- [MPayCallbackController.cs:93](src/Cnas.Ps.Api/Controllers/MPayCallbackController.cs:93) fetches an order by caller-supplied `orderId`.
- [MPayCallbackController.cs:103](src/Cnas.Ps.Api/Controllers/MPayCallbackController.cs:103) returns `BeneficiaryIdnp`.
- [MPayCallbackController.cs:126](src/Cnas.Ps.Api/Controllers/MPayCallbackController.cs:126) exposes the confirmation endpoint.
- [MPayCallbackController.cs:154](src/Cnas.Ps.Api/Controllers/MPayCallbackController.cs:154) records confirmation using only `orderId`, `PaymentRef`, and `ConfirmedAtUtc`.

Recommendation:
Add an application-level verifier for MPay callbacks before reading or mutating order state. Acceptable controls include mTLS client-certificate validation in ASP.NET Core, a signed callback payload, or a dedicated shared-secret/HMAC header with replay protection. Do not rely solely on ingress comments or manual allow-listing for the security boundary.

### SEC-002: MNotify bounce webhook mutates notification state without in-process signature validation

Impact: If `/api/webhooks/mnotify/bounce` is reachable without the assumed upstream HMAC validation, an attacker can mark notifications as failed and pollute audit/reporting state.

Evidence:
- [MNotifyTemplatesAdminController.cs:195](src/Cnas.Ps.Api/Controllers/MNotifyTemplatesAdminController.cs:195) says an HMAC header is validated upstream.
- [MNotifyTemplatesAdminController.cs:199](src/Cnas.Ps.Api/Controllers/MNotifyTemplatesAdminController.cs:199) marks the webhook `[AllowAnonymous]`.
- [MNotifyTemplatesAdminController.cs:219](src/Cnas.Ps.Api/Controllers/MNotifyTemplatesAdminController.cs:219) accepts the bounce payload.
- [MNotifyTemplatesAdminController.cs:224](src/Cnas.Ps.Api/Controllers/MNotifyTemplatesAdminController.cs:224) dispatches directly to the handler.
- [MNotifyBounceHandler.cs:73](src/Cnas.Ps.Infrastructure/Services/MNotify/MNotifyBounceHandler.cs:73) looks up a notification by caller-supplied reference.
- [MNotifyBounceHandler.cs:90](src/Cnas.Ps.Infrastructure/Services/MNotify/MNotifyBounceHandler.cs:90) sets `DeliveryStatus = Failed`.
- [MNotifyBounceHandler.cs:93](src/Cnas.Ps.Infrastructure/Services/MNotify/MNotifyBounceHandler.cs:93) persists the mutation.

Recommendation:
Validate the MNotify signature in application code before invoking `HandleBounceAsync`. Include timestamp/nonce replay protection and return a uniform 401/403 response on invalid signatures.

## High

### SEC-003: SAML assertion parser explicitly does not validate XMLDSig signatures

Evidence:
- [MPassSamlAssertionParser.cs:38](src/Cnas.Ps.Infrastructure/MGov/MPassSamlAssertionParser.cs:38) states XMLDSig is not validated.
- [MPassSamlAssertionParser.cs:57](src/Cnas.Ps.Infrastructure/MGov/MPassSamlAssertionParser.cs:57) leaves signature validation as a TODO.
- [MPassSamlAssertionParser.cs:76](src/Cnas.Ps.Infrastructure/MGov/MPassSamlAssertionParser.cs:76) parses caller-provided XML.
- [MPassSamlAssertionParser.cs:202](src/Cnas.Ps.Infrastructure/MGov/MPassSamlAssertionParser.cs:202) converts attribute values into claims.
- [MPassSamlAssertionParser.cs:211](src/Cnas.Ps.Infrastructure/MGov/MPassSamlAssertionParser.cs:211) returns an authenticated `ClaimsPrincipal`.

Current mitigating context:
- [MPassSamlController.cs:28](src/Cnas.Ps.Api/Controllers/MPassSamlController.cs:28) says the current ACS does not issue a cookie or call `SignInAsync`.

Risk:
The current endpoint is preparatory, but the parser returns an authenticated principal from unsigned XML. If future code wires this parser into sign-in before XMLDSig verification lands, this becomes authentication bypass.

Recommendation:
Make signature validation mandatory in `ISamlAssertionParser.Parse` before it can return success. If the prep endpoint must remain, gate it behind development/staging configuration or a privileged operator auth policy and keep it unavailable in production.

### SEC-004: SAML ACS reflects sensitive claim values to anonymous callers

Evidence:
- [MPassSamlController.cs:45](src/Cnas.Ps.Api/Controllers/MPassSamlController.cs:45) marks the ACS `[AllowAnonymous]`.
- [MPassSamlController.cs:116](src/Cnas.Ps.Api/Controllers/MPassSamlController.cs:116) parses the submitted SAML response.
- [MPassSamlController.cs:130](src/Cnas.Ps.Api/Controllers/MPassSamlController.cs:130) reads `subjectIdnp`.
- [MPassSamlController.cs:131](src/Cnas.Ps.Api/Controllers/MPassSamlController.cs:131) reads `principalIdnp`.
- [MPassSamlController.cs:155](src/Cnas.Ps.Api/Controllers/MPassSamlController.cs:155) includes email in the response.
- [MPassSamlController.cs:159](src/Cnas.Ps.Api/Controllers/MPassSamlController.cs:159) returns the summary body.

Risk:
Because assertions are not signed, the endpoint mostly reflects attacker-supplied values today. The larger issue is that a production-exposed anonymous endpoint normalizes and returns national identifiers and email addresses, which is the wrong default for a security-sensitive ACS.

Recommendation:
Do not return raw claims from ACS. Return only a correlation id/status, and log minimal non-PII metadata. Add request size limits for `SAMLResponse`.

### SEC-005: Auth cookie uses `SameAsRequest` while forwarded-header handling is absent

Evidence:
- [AuthenticationComposition.cs:63](src/Cnas.Ps.Api/Composition/AuthenticationComposition.cs:63) sets `CookieSecurePolicy.SameAsRequest`.
- [AuthenticationComposition.cs:109](src/Cnas.Ps.Api/Composition/AuthenticationComposition.cs:109) sets `SaveTokens = true`, increasing the sensitivity of the auth cookie payload.
- [ApiCompositionRoot.cs:305](src/Cnas.Ps.Api/Composition/ApiCompositionRoot.cs:305) uses HSTS outside development.
- [ApiCompositionRoot.cs:306](src/Cnas.Ps.Api/Composition/ApiCompositionRoot.cs:306) uses HTTPS redirection outside development.

Risk:
In TLS-terminated deployments, Kestrel may see the request as HTTP unless forwarded headers are configured. With `SameAsRequest`, the auth cookie can be emitted without the `Secure` attribute even though the public site is HTTPS. The code scan found no `UseForwardedHeaders` registration in the API composition root.

Recommendation:
Set `CookieSecurePolicy.Always` in non-development environments, or correctly configure and restrict ASP.NET Core forwarded headers before cookie/auth middleware. Also avoid saving OIDC tokens into the cookie unless the application actually needs them after sign-in.

## Medium

### SEC-006: Rate limiting trusts `X-Forwarded-For` by default

Evidence:
- [RateLimitingOptions.cs:28](src/Cnas.Ps.Api/Composition/RateLimitingOptions.cs:28) documents forwarded-header trust as the default.
- [RateLimitingOptions.cs:69](src/Cnas.Ps.Api/Composition/RateLimitingOptions.cs:69) sets `TrustForwardedHeaders = true`.
- [RateLimitingComposition.cs:293](src/Cnas.Ps.Api/Composition/RateLimitingComposition.cs:293) reads the raw `X-Forwarded-For` header.
- [RateLimitingComposition.cs:302](src/Cnas.Ps.Api/Composition/RateLimitingComposition.cs:302) extracts a token from the header.
- [RateLimitingComposition.cs:305](src/Cnas.Ps.Api/Composition/RateLimitingComposition.cs:305) uses that token as the rate-limit partition.

Risk:
If the API is ever reachable directly, or behind an ingress that appends rather than rewrites XFF, clients can influence rate-limit partitioning. This can allow throttling bypass or accidental aggregation of all users into one gateway partition, depending on proxy behavior.

Recommendation:
Default `TrustForwardedHeaders` to false and enable it only in environment-specific production config after ASP.NET Core forwarded-header middleware is configured with known proxies/networks. Add integration tests for direct and proxied request partitioning.

### SEC-007: Anonymous OpenAPI is explicitly exempt from rate limiting

Evidence:
- [ApiCompositionRoot.cs:327](src/Cnas.Ps.Api/Composition/ApiCompositionRoot.cs:327) maps OpenAPI and disables rate limiting.

Risk:
This is mainly information disclosure and availability risk. The public OpenAPI document can expose internal/admin routes and can be scraped without throttling. The project may intentionally publish the contract, but disabling rate limits removes a cheap abuse control.

Recommendation:
Either protect OpenAPI outside development/staging, or keep it public but leave at least the anonymous/global limiter enabled.

## Low / Observations

### SEC-008: Public WSDL portal exposes controller metadata

Evidence:
- [WsdlPortalController.cs:33](src/Cnas.Ps.Api/Controllers/WsdlPortalController.cs:33) marks the WSDL portal anonymous.
- [WsdlPortalController.cs:46](src/Cnas.Ps.Api/Controllers/WsdlPortalController.cs:46) lists controller WSDL surfaces.
- [WsdlPortalController.cs:64](src/Cnas.Ps.Api/Controllers/WsdlPortalController.cs:64) serves per-controller WSDL documents.

Risk:
The comments state this is intentional public metadata. It still increases endpoint discoverability, especially for admin surfaces.

Recommendation:
Confirm this is required in production. If not, gate it by environment or technical-admin policy.

## Positive Findings

- Layer boundaries are enforced by architecture tests.
- Most non-public controllers carry `[Authorize]` or role/policy attributes.
- JWT validation enables issuer, audience, lifetime, and signing-key validation.
- Refresh tokens use cryptographic randomness.
- Field encryption and HMAC shadow columns exist for sensitive identifiers.
- File upload validation includes base64, size, filename, path traversal, and magic-byte style validation paths.
- Raw SQL usage found in global search binds user input through `NpgsqlParameter`.

## Review Limitations

- This was a static review, not dynamic testing or penetration testing.
- The requested skill has no C#/.NET-specific reference guidance, so findings are based on general web/AppSec practice.
- I did not verify live ingress, mTLS, WAF, or Kubernetes network policy behavior. Several findings are critical only if the documented gateway controls are missing, bypassed, or misconfigured.
