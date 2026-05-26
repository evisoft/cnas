namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// Placeholder for the staff-login UI journey. Activating this journey requires three
/// pieces of upstream work that are deliberately out of scope for the current build:
/// <list type="number">
///   <item>A <c>StaffLogin.razor</c> page in <c>src/Cnas.Ps.Web/Pages/</c>. The Web
///         project currently ships only the citizen-portal pages (Index, Dashboard,
///         Inbox, Applications) — there is no staff console route yet.</item>
///   <item>An MPass mock IdP. The real MPass protocol is SAML 2.0 with X.509 client
///         certs, gated on a MEGA-issued staging cert (see
///         <c>docs/EGOV-INTEGRATION-GAP.md §"MPass — CRITICAL refactor"</c>). Until
///         the SAML middleware lands (the OIDC adapter we ship today is wire-incompatible
///         with the real MPass), there is no testable redirect flow.</item>
///   <item>A Blazor host bootstrap in <see cref="ApiHostFixture"/>. The current fixture
///         hosts the API only; it does not serve the Web project's WASM bundle. Booting
///         both is feasible but adds noticeable startup cost to every E2E run, so we
///         defer it until a journey actually needs to render UI under Kestrel.</item>
/// </list>
/// The skipped test is kept in the suite so future contributors see the planned shape
/// rather than rediscovering it.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class StaffLoginPageJourneyTests
{
    /// <summary>
    /// Planned: render the staff login page, assert the MPass redirect button is
    /// visible, and follow it through a stubbed SAML flow into the staff inbox.
    /// </summary>
    [Fact(Skip = "Blocked on three upstream items: no StaffLogin.razor page exists, no MPass SAML middleware yet (gated on MEGA cert — EGOV-INTEGRATION-GAP §MPass), and the E2E fixture does not host the Blazor Web project. Track in the project gap list.")]
    public Task StaffLoginPage_RendersAndPromptsForMPass()
    {
        return Task.CompletedTask;
    }
}
