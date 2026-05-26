using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.E2E.Tests.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cnas.Ps.E2E.Tests;

/// <summary>
/// Authenticated-journey variant of <see cref="ApiHostFixture"/>. Layers three additional
/// configuration switches on top of the base fixture so journey tests that need to act as
/// a real CNAS persona work end-to-end through the production controller + service stack:
/// <list type="bullet">
///   <item>
///     <description>
///       <c>Cnas:E2E:TestAuth:Enabled</c> = <c>true</c> — registers
///       <see cref="TestAuthHandler"/> as the default authentication scheme so requests
///       carrying <see cref="TestAuthHandler.HeaderName"/> materialise a
///       <see cref="System.Security.Claims.ClaimsPrincipal"/> with the requested persona.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>Cnas:FieldEncryption:Key</c> — a fixed test AES-256 master key (32 bytes,
///       base64-encoded). Required because the journey tests seed entities (<c>Solicitant</c>,
///       <c>UserProfile</c>) whose <c>NationalId</c> column round-trips through
///       <c>EncryptedStringConverter</c>; without a real key the encryptor sentinel
///       throws on first use.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>Cnas:FieldHashing:SaltKey</c> — a fixed test HMAC-SHA256 salt (32 bytes,
///       base64-encoded). Required because <c>EnqueueAsync</c> and other paths consult
///       the deterministic hash for equality lookups.
///     </description>
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fixed test key.</b> The constants below are checked into source intentionally —
/// they exist only inside the E2E test process and never touch a real database. Production
/// deployments source the same configuration keys from the secrets manager
/// (CLAUDE.md §1.8) and would refuse to boot with these values present in
/// <c>appsettings.json</c>. Tests prefer determinism over rotation so a re-run does not
/// produce different ciphertexts.
/// </para>
/// <para>
/// <b>Test class binding.</b> Journey tests subscribe to this fixture via
/// <c>[Collection(AuthenticatedE2ECollection.Name)]</c>; the collection definition wires
/// the Playwright + authenticated host fixtures together.
/// </para>
/// </remarks>
public sealed class AuthenticatedApiHostFixture : ApiHostFixture
{
    /// <summary>
    /// Base64-encoded AES-256 master key for field encryption. Decoded to 32 raw bytes
    /// by <c>FieldEncryptionOptions.GetKeyBytes</c>. The byte pattern <c>0xC0..0xDF</c>
    /// matches the constant in <c>NationalIdHashShadowColumnTests</c> for consistency
    /// across the E2E and integration suites — single canonical "test key" value.
    /// </summary>
    /// <remarks>
    /// Never used outside the test process. Production keys come from Vault / k8s
    /// Secret / MCloud KMS per CLAUDE.md §1.8 and TOR SEC 005 / SEC 006.
    /// </remarks>
    public const string TestFieldEncryptionKey =
        "wMHCw8TFxsfIycrLzM3Oz9DR0tPU1dbX2Nna29zd3t8=";

    /// <summary>
    /// Base64-encoded HMAC-SHA256 salt for deterministic hashing. Decoded to 32 raw
    /// bytes by <c>FieldHashingOptions.GetSaltBytes</c>. A different byte pattern from
    /// the encryption key so accidental cross-pollination cannot disguise itself as a
    /// "passing" round-trip.
    /// </summary>
    /// <remarks>
    /// Never used outside the test process. Production salts come from the secrets
    /// manager — see <c>FieldHashingOptions</c> remarks for the rotation discipline.
    /// </remarks>
    public const string TestFieldHashingSalt =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    /// <inheritdoc />
    protected override void ConfigureAdditionalSettings(IDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings[$"{TestAuthOptions.SectionName}:Enabled"] = "true";
        settings["Cnas:FieldEncryption:Key"] = TestFieldEncryptionKey;
        settings["Cnas:FieldHashing:SaltKey"] = TestFieldHashingSalt;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>UC17 phase 2A — IFileStorage swap.</b> The production composition wires
    /// either <c>MinioFileStorage</c> (when credentials are present) or the fail-loud
    /// <c>MissingMinioFileStorage</c> sentinel (when they are not). The E2E config
    /// deliberately leaves MinIO credentials empty so the sentinel is registered —
    /// but phase 2A introduced a journey that legitimately exercises the upload /
    /// download path, which would throw <c>InvalidOperationException</c> the moment
    /// the controller reaches the storage call. We scrub the sentinel and substitute
    /// the <see cref="InMemoryFileStorage"/> in this authenticated fixture only,
    /// keeping the base read-only journeys unaffected.
    /// </para>
    /// <para>
    /// <b>Registration-order note.</b> The base fixture calls
    /// <c>ConfigureAdditionalServices</c> AFTER every default registration, including
    /// the singleton <c>IFileStorage</c> from <c>AddCnasInfrastructure</c>. Simply
    /// appending <c>AddSingleton&lt;IFileStorage, InMemoryFileStorage&gt;()</c> would
    /// leave the sentinel as the resolved implementation because the first
    /// registration wins for a singleton service descriptor that DI sees as already
    /// registered. We use <see cref="ServiceCollectionDescriptorExtensions.Replace"/>
    /// — or equivalently <see cref="ServiceCollectionDescriptorExtensions.RemoveAll{T}"/>
    /// followed by <c>AddSingleton</c> — so the substitute takes effect. The
    /// alternative (<c>AddSingleton</c> alone) silently no-ops, which is the kind of
    /// failure mode that masquerades as a "works locally, breaks in CI" flake.
    /// </para>
    /// </remarks>
    protected override void ConfigureAdditionalServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IFileStorage>();
        services.AddSingleton<IFileStorage, InMemoryFileStorage>();
    }
}
