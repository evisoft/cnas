namespace Cnas.Ps.E2E.Tests;

/// <summary>
/// xUnit collection definition that binds the Playwright + ApiHost fixtures so a
/// single browser instance and a single in-process API host are shared by every
/// journey test. Sharing the host keeps total runtime within reason — booting the
/// composition root per-test would dwarf the actual journey work.
/// </summary>
[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<PlaywrightFixture>, ICollectionFixture<ApiHostFixture>
{
    /// <summary>The collection name referenced by <c>[Collection]</c> attributes on tests.</summary>
    public const string Name = "E2E";
}

/// <summary>
/// xUnit collection definition that pairs the shared Playwright browser with the
/// <see cref="AuthenticatedApiHostFixture"/> variant. Journey tests subscribed to this
/// collection can drive authenticated API endpoints by sending the
/// <see cref="Auth.TestAuthHandler.HeaderName"/> header; the host registers a header-driven
/// authentication scheme and provisions the field-encryption / hashing keys required by
/// any seeding that touches encrypted columns.
/// </summary>
/// <remarks>
/// Kept distinct from <see cref="E2ECollection"/> so the original three journey tests
/// (which deliberately exercise the production cookie + OIDC composition) are not
/// affected by the test-auth opt-in. Tests subscribing to this collection should
/// <i>not</i> share state with tests in <see cref="E2ECollection"/> — xUnit will boot a
/// separate host for each collection.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AuthenticatedE2ECollection
    : ICollectionFixture<PlaywrightFixture>, ICollectionFixture<AuthenticatedApiHostFixture>
{
    /// <summary>The collection name referenced by <c>[Collection]</c> attributes on tests.</summary>
    public const string Name = "AuthenticatedE2E";
}
