# MPass smoke E2E runbook

> Anchored to TOR ID(s): R2707 (verification gate). Externally gated:
> requires the MEGA-issued MPass client certificate + SAML middleware
> per `docs/EGOV-INTEGRATION-GAP.md §"MPass — CRITICAL refactor"`.
> Iteration 104.

## 1. Purpose / scope

Document the smoke E2E that proves the citizen happy-path
**MPass login → submit Cerere → MSign → MNotify** end-to-end.
The actual run is operator-triggered against the MPass staging IdP
once the prerequisites in §3 are satisfied. The runbook codifies the
sequence so anyone can re-execute the smoke without rediscovering it.

In scope: the citizen-portal happy-path. Out of scope: staff console
journeys (no `StaffLogin.razor` page yet) and adversarial paths
(captured by the security review + pen-test, R2257 / R2706).

## 2. Audience / stakeholders

- Supplier QA engineer running the smoke.
- Supplier integrations lead (MPass / MSign / MNotify wiring).
- CNAS Service Owner (sign-off for R2707).
- MEGA contact (cert + IdP availability).
- Acceptance committee for the verification-gates section.

## 3. Prerequisites (gating)

This run **cannot start** until all of:

1. **MEGA-issued X.509 client cert** is loaded in the supplier secrets
   store (Vault) and surfaced to `ICertificateStore`. Per
   `docs/EGOV-INTEGRATION-GAP.md §"MPass — CRITICAL refactor"`.
2. **SAML 2.0 middleware** is wired into the Web project (the OIDC
   adapter shipped today is wire-incompatible with the real MPass).
3. **`StaffLogin.razor`** (or the citizen-portal MPass-redirect page)
   exists and is reachable in the Web project. The citizen portal in
   `src/Cnas.Ps.Web/Pages/` currently ships only the citizen surface
   (Index, Dashboard, Inbox, Applications), so the MPass redirect
   landing-page slot must be confirmed.
4. **E2E fixture hosts the Web project.** `ApiHostFixture` today hosts
   the API only; the smoke needs Blazor WASM bootstrap under Kestrel
   so Playwright can drive the page. See
   `tests/Cnas.Ps.E2E.Tests/Journeys/StaffLoginPageJourneyTests.cs`
   for the canonical list of upstream gates.
5. **MPass staging IdP** is reachable and the supplier client is
   registered for the redirect URI.
6. **MSign staging** is reachable and `IMSignClient` is configured
   against the staging endpoint.
7. **MNotify staging** is reachable; the channel template for
   `Beneficiar` confirmations is published.

Until all of (1)–(7) are signed off, the canonical placeholder
`StaffLoginPageJourneyTests.StaffLoginPage_RendersAndPromptsForMPass`
remains `[Fact(Skip = …)]` — the green build at iteration 104 still
reports `1 skipped` for that test, which is the intended state.

## 4. Execution sequence

1. **Drive MPass redirect.** Navigate the headless browser to the Web
   project's login route. Assert the MPass redirect button is visible
   and click it.
2. **Authenticate.** Complete the MPass staging IdP flow with the
   pre-provisioned test citizen identity.
3. **Land in inbox.** Verify the post-login redirect resolves to the
   citizen Inbox page; assert the audit row `AUTH.LOGIN.SUCCESS` was
   emitted (server-side check via `IAuditService`).
4. **Open application form.** Navigate to a public service
   (e.g. a basic Cerere). Fill the minimal form fields.
5. **Submit Cerere.** Click submit. Assert a `Cerere` row created via
   `IApplicationService` and the workflow advanced to the first
   `MSignRequired` step.
6. **Sign via MSign.** Drive the MSign staging flow until the signature
   callback hits `MSignCallbackController` and the application advances
   past the MSign step.
7. **Confirm MNotify dispatch.** Assert that the application's
   completion event triggered `IMNotifyClient.SendAsync`. In staging,
   the dispatch lands in the MNotify dev-mailbox.
8. **Verify audit trail.** Assert the audit chain contains rows in
   order: `AUTH.LOGIN.SUCCESS`, `APPLICATION.SUBMITTED`,
   `APPLICATION.SIGNED`, `NOTIFICATION.SENT`. Re-verify with
   `IAuditChainVerifier.VerifyAsync` → `IsValid=true`.

## 5. Expected results

- All four MGov touchpoints (MPass / MSign / MNotify; MCloud
  implicit) responded with the expected outcomes.
- One `Cerere` row exists in the target environment with status
  reflecting the workflow's terminal step for the smoke service.
- Four-step audit chain present and unbroken.
- Test run records duration end-to-end; the smoke is a gate, not a
  benchmark — the only latency assertion is "no step times out under
  the staging-default 30 s budget".

## 6. Fallback if MPass is unavailable

If MPass staging is unreachable on the day of the run:

1. Mark the smoke `Inconclusive — external gate down`. Do NOT mark it
   `Failed` (R2707 is externally gated; an external outage is not a
   supplier defect).
2. Open a `SupportTicket` against the integration with severity
   `Ordinary` and notify the MEGA contact + CNAS Service Owner.
3. Re-attempt within 2 business days; if still down, escalate via the
   bilateral integration channel.
4. Until MPass returns, the `IMPassClient` health-probe stays RED in
   the operations dashboard and the acceptance committee may proceed
   with conditional sign-off referencing this fallback clause.

## 7. Sign-off

- The smoke is recorded as Passed when steps §4.1–§4.8 all succeed.
- Acceptance Protocol row "R2707" is countersigned by Supplier QA +
  CNAS Service Owner with the run timestamp + commit hash attached.
- Until the prerequisites in §3 are all signed off, R2707 stays
  `[ ]` in TODO.md, and the `StaffLoginPageJourneyTests` placeholder
  stays `[Fact(Skip = …)]` in the E2E suite.

## 8. References

- TOR §R2707 (verification gate).
- `docs/EGOV-INTEGRATION-GAP.md §"MPass — CRITICAL refactor"`.
- `tests/Cnas.Ps.E2E.Tests/Journeys/StaffLoginPageJourneyTests.cs`.
- `src/Cnas.Ps.Api/Controllers/MPassSamlController.cs`,
  `MSignCallbackController.cs`.
- `src/Cnas.Ps.Application/External/IMPassClient.cs`,
  `IMSignClient.cs`, `IMNotifyClient.cs`.
- `docs/integration/technical-integration-specs.md` (per-touchpoint
  spec; iter 100).
- `docs/integration/interop-acceptance-protocol.md` (bilateral
  template; iter 102).
