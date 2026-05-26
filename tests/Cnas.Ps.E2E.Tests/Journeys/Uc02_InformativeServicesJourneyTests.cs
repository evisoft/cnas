using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC02 — "Accesez servicii informative". End-to-end journey covering the two
/// anonymous-accessible informational endpoints exposed by <c>PublicController</c>:
/// <list type="bullet">
///   <item><c>GET /api/public/calculators/retirement-age</c> — implements CF 02.01
///         "Calculatorul vârstei de pensionare".</item>
///   <item><c>GET /api/public/calculators/application-status</c> — implements CF 02.03
///         "Statutul cererii/deciziei" lookup by public reference number.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Actor.</b> Anonymous internet user (Utilizator internet). Neither endpoint sets
/// <c>[Authorize]</c>, and both sit behind the <c>Anonymous</c> rate-limit policy. The
/// tests deliberately omit any auth header to lock the anonymous accessibility contract.
/// </para>
/// <para>
/// <b>Why one journey covers two endpoints.</b> CF 02.01 and CF 02.03 surface the same
/// "informational services" actor flow — a citizen lands on the public portal, picks the
/// calculator they need, and gets a sanitised answer. The PII-free shape promised by CF
/// 01.09 / SEC 044 is locked by asserting only the documented columns on the response
/// records (<see cref="RetirementAgeOutput"/> / <see cref="ApplicationStatusOutput"/>),
/// neither of which has a slot for personal data.
/// </para>
/// <para>
/// <b>Fixture choice.</b> Same rationale as <see cref="Uc01_PublicContentJourneyTests"/> —
/// the authenticated fixture is reused for its field-encryption + hashing keys (required
/// to seed the <see cref="Solicitant"/> + <see cref="ServiceApplication"/> rows the
/// status-lookup test resolves). The HTTP calls remain anonymous.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc02_InformativeServicesJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture supplying the running Kestrel host.</param>
    public Uc02_InformativeServicesJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// An anonymous client queries the retirement-age calculator for a male born in 1965
    /// and gets back the legal retirement date (birth + 63y) per Legea nr. 156/1998.
    /// </summary>
    [Fact]
    public async Task RetirementAgeCalculator_AnonymousMale_ReturnsBirthPlusSixtyThreeYears()
    {
        // Arrange — chosen so the asserted retirement date is unambiguous.
        var birthDate = new DateOnly(1965, 4, 18);
        const char sex = 'M';
        var expectedRetirementDate = birthDate.AddYears(63);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };

        // Act — anonymous GET; the query-string serialiser uses ISO-8601 for DateOnly,
        // which the model binder accepts.
        using var response = await client.GetAsync(
            $"/api/public/calculators/retirement-age?birthDate={birthDate:yyyy-MM-dd}&sex={sex}");

        // Assert
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var output = await response.Content.ReadFromJsonAsync<RetirementAgeOutput>();
        output.Should().NotBeNull("the endpoint must return a parseable RetirementAgeOutput");
        output!.RetirementDate.Should().Be(
            expectedRetirementDate,
            "Legea 156/1998 sets male retirement age at 63");
        output.AgeYears.Should().Be(63);
    }

    /// <summary>
    /// An anonymous client looks up a seeded application by its public reference number
    /// and receives the sanitised status payload (no PII). A second lookup with a
    /// non-existent reference returns 404.
    /// </summary>
    [Fact]
    public async Task ApplicationStatusLookup_AnonymousByReference_ReturnsSanitisedStatus()
    {
        // Arrange — seed a Solicitant + ServiceApplication with a known reference number.
        const string referenceNumber = "UC02-REF-LOOKUP-001";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        // A unique service-passport row anchors the seeded ServiceApplication (FK).
        var passport = new ServicePassport
        {
            Code = "SP-UC02-STATUS",
            NameRo = "UC02 Status Test Pasaport",
            DescriptionRo = "Pasaport seed pentru jurnalul UC02 status lookup.",
            WorkflowCode = "wf-e2e",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        // Solicitant with a valid 13-digit IDNP (mod-10 weighted-{7,3,1} checksum-conformant).
        const string idnp = "2000000000007";
        var solicitant = new Solicitant
        {
            NationalId = idnp,
            NationalIdHash = hasher.ComputeHash(idnp),
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "UC02 Status Solicitant",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);
        await db.SaveChangesAsync();

        var submittedAt = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
        db.Applications.Add(new ServiceApplication
        {
            SolicitantId = solicitant.Id,
            ServicePassportId = passport.Id,
            Status = ApplicationStatus.UnderExamination,
            FormPayloadJson = "{}",
            SnapshotJson = "{}",
            ReferenceNumber = referenceNumber,
            SubmittedAtUtc = submittedAt,
            UpdatedAtUtc = updatedAt,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };

        // Act 1 — anonymous status lookup by the seeded reference number.
        using var hitResponse = await client.GetAsync(
            $"/api/public/calculators/application-status?referenceNumber={referenceNumber}");

        // Assert — 200 OK with a sanitised status payload.
        hitResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await hitResponse.Content.ReadAsStringAsync());

        var status = await hitResponse.Content.ReadFromJsonAsync<ApplicationStatusOutput>();
        status.Should().NotBeNull();
        status!.ReferenceNumber.Should().Be(referenceNumber);
        status.Status.Should().Be(
            ApplicationStatus.UnderExamination.ToString(),
            "the public status surface must echo the lifecycle enum");
        status.LastUpdateUtc.Should().Be(
            updatedAt,
            "the public status must surface the most recent timestamp (UpdatedAtUtc) per InformationServices");

        // The DTO surface intentionally has no PII slot — verify the response body does
        // not contain the seeded IDNP. This is a belt-and-braces check for CF 01.09 /
        // SEC 044 in case a future refactor widens the DTO.
        var raw = await hitResponse.Content.ReadAsStringAsync();
        raw.Should().NotContain(idnp, "no PII may leak into the public status payload");
        raw.Should().NotContain("Solicitant", "no PII may leak into the public status payload");

        // Act 2 — anonymous status lookup with a reference that does not exist.
        using var missResponse = await client.GetAsync(
            "/api/public/calculators/application-status?referenceNumber=UC02-DOES-NOT-EXIST");

        // Assert — 404 Not Found, with no information leakage in the body.
        missResponse.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "unknown references must surface as 404, not 200 with a null payload");
    }
}
