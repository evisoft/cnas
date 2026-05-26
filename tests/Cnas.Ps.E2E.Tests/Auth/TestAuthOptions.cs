namespace Cnas.Ps.E2E.Tests.Auth;

/// <summary>
/// E2E-only configuration switch that gates the registration of <see cref="TestAuthHandler"/>.
/// Bound from the <c>Cnas:E2E:TestAuth</c> configuration section by the
/// <see cref="ApiHostFixture"/>; <see cref="AuthenticatedApiHostFixture"/> flips
/// <see cref="Enabled"/> to <c>true</c> so authenticated journey tests can short-circuit
/// the MPass OIDC dance with a single HTTP header.
/// </summary>
/// <remarks>
/// <para>
/// The default is <see cref="Enabled"/> = <c>false</c> so the original three journey tests
/// (health, OpenAPI, staff login placeholder) keep exercising the production authentication
/// scheme registration. No code path inside <c>Cnas.Ps.Api</c> reads this option — the
/// switch lives entirely in the E2E test host.
/// </para>
/// <para>
/// Configuration key: <c>Cnas:E2E:TestAuth:Enabled</c>. Any string parseable as a boolean
/// (<c>"true"</c> / <c>"false"</c>) is accepted; absent or unparseable values default to
/// <c>false</c>.
/// </para>
/// </remarks>
public sealed class TestAuthOptions
{
    /// <summary>Configuration section name to bind from app settings.</summary>
    public const string SectionName = "Cnas:E2E:TestAuth";

    /// <summary>
    /// When <c>true</c> the E2E host registers <see cref="TestAuthHandler"/> as an
    /// additional authentication scheme and makes it the default authenticate scheme.
    /// Defaults to <c>false</c> so the production cookie + MPass OIDC wiring stays
    /// intact for tests that intentionally exercise it.
    /// </summary>
    public bool Enabled { get; init; }
}
