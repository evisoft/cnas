using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ProfileService"/> — UC13 self-service profile management.
/// Exercises the GetMineAsync / UpdateMineAsync round-trip with focus on the
/// Phone-persistence contract added in the encrypted-PhoneE164 batch:
/// <list type="bullet">
///   <item>valid E.164 phones are persisted and round-trip through GetMineAsync,</item>
///   <item>malformed phones fail validation rather than being silently dropped,</item>
///   <item>nullable semantics — clearing a previously-set phone with <c>null</c> works.</item>
/// </list>
/// </summary>
/// <remarks>
/// Tests use EF Core's InMemory provider and the encryption-aware
/// <see cref="CnasDbContext"/> constructor when exercising encrypted-column behaviour;
/// the converter is wired at the property layer so the round-trip is faithful even on
/// InMemory (see <c>EncryptedStringConverterTests</c> for the rationale). NSubstitute is
/// used for the small collaborators (<see cref="ICallerContext"/>,
/// <see cref="ICnasTimeProvider"/>). Per CLAUDE.md RULE 1 these tests are written BEFORE
/// the production code that maps PhoneE164 ↔ ProfileOutput.Phone.
/// </remarks>
public class ProfileServiceTests
{
    /// <summary>Deterministic clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UpdateMineAsync_ValidPhone_PersistsAndRoundTrips()
    {
        // Arrange — seed a profile with no phone.
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-phone-valid",
            DisplayName = "Phone Round-Trip",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var input = new ProfileUpdateInput(
            Email: "round-trip@example.md",
            Phone: "+37369123456",
            PreferredLanguage: "ro");

        // Act — push the update, then read the profile back.
        var update = await harness.Service.UpdateMineAsync(input);
        var read = await harness.Service.GetMineAsync();

        // Assert — the update succeeded, GetMineAsync surfaces the persisted phone.
        update.IsSuccess.Should().BeTrue();
        read.IsSuccess.Should().BeTrue();
        read.Value!.Phone.Should().Be("+37369123456",
            "the service must project the persisted PhoneE164 onto ProfileOutput.Phone.");
    }

    [Fact]
    public async Task UpdateMineAsync_PhoneWithFormattingChars_NormalisesToCanonicalE164()
    {
        // Arrange — Phones with whitespace, dashes, parentheses must normalise per the
        // PhoneE164 value object (a common UX affordance — users paste "+373 22 255-555").
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-phone-normalised",
            DisplayName = "Phone Normalisation",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var input = new ProfileUpdateInput(
            Email: null,
            Phone: "+373 22 255-555",
            PreferredLanguage: "ro");

        // Act
        var update = await harness.Service.UpdateMineAsync(input);
        var read = await harness.Service.GetMineAsync();

        // Assert — the canonical form is "+37322255555" (no spaces, dashes, parens).
        update.IsSuccess.Should().BeTrue();
        read.Value!.Phone.Should().Be("+37322255555");
    }

    [Fact]
    public async Task UpdateMineAsync_InvalidPhoneFormat_ReturnsValidationFailure()
    {
        // Arrange — a US-formatted phone "123-456-7890" lacks the leading '+' country
        // code and must be rejected. The service must NOT silently accept-and-drop it
        // (the historical bug this batch closes).
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-phone-bad",
            DisplayName = "Phone Bad Format",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var input = new ProfileUpdateInput(
            Email: null,
            Phone: "123-456-7890", // missing leading '+'
            PreferredLanguage: "ro");

        // Act
        var update = await harness.Service.UpdateMineAsync(input);

        // Assert — validation failure surfaces with the standard InvalidPhone code.
        update.IsFailure.Should().BeTrue();
        update.ErrorCode.Should().Be(ErrorCodes.InvalidPhone);

        // And the row is untouched on disk — no silent partial update.
        var reloaded = await harness.Db.UserProfiles.AsNoTracking()
            .SingleAsync(u => u.Id == profile.Id);
        // The field doesn't exist before this batch; once it does, must remain null.
        reloaded.PhoneE164.Should().BeNull(
            "validation failure must not partially apply the update.");
    }

    [Fact]
    public async Task UpdateMineAsync_NullPhone_ClearsPreviouslySetPhone()
    {
        // Arrange — seed a profile that ALREADY has a phone set, then clear it via null.
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-phone-clear",
            DisplayName = "Phone Clear",
            PhoneE164 = "+37322000000",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var input = new ProfileUpdateInput(
            Email: null,
            Phone: null,
            PreferredLanguage: "ro");

        // Act
        var update = await harness.Service.UpdateMineAsync(input);
        var read = await harness.Service.GetMineAsync();

        // Assert — success, GetMineAsync returns null, raw column is null.
        update.IsSuccess.Should().BeTrue();
        read.Value!.Phone.Should().BeNull(
            "passing Phone=null must clear the previously-persisted value.");
    }

    [Fact]
    public async Task GetMineAsync_ProfileWithPhone_ReturnsPhoneInDto()
    {
        // Arrange — directly seed a profile with PhoneE164 set; the GET path is the
        // single most important contract to lock since it carried the historical bug
        // (the service hard-coded null on the way out).
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-phone-get",
            DisplayName = "Phone Get",
            PhoneE164 = "+37369999999",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        // Act
        var read = await harness.Service.GetMineAsync();

        // Assert — the field is no longer hard-coded null.
        read.IsSuccess.Should().BeTrue();
        read.Value!.Phone.Should().Be("+37369999999");
    }

    // ─────────────────────── R0361 — UpdateMyContactAsync ───────────────────────

    /// <summary>
    /// R0361 happy path — the contact-only PUT updates DisplayName + Email + Phone
    /// without disturbing PreferredLanguage. The MyProfile.razor page calls this
    /// method for the contact form so the rename + e-mail change ride in one
    /// round-trip; the language toggle has its own dedicated PUT (R0211).
    /// </summary>
    [Fact]
    public async Task UpdateMyContactAsync_ValidPayload_UpdatesContactFieldsPreservesLanguage()
    {
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-contact-happy",
            DisplayName = "Old Name",
            Email = "old@example.md",
            PhoneE164 = "+37322000000",
            PreferredLanguage = "ru",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var input = new ProfileContactInput(
            DisplayName: "New Name",
            Email: "new@example.md",
            Phone: "+37369123456");

        var update = await harness.Service.UpdateMyContactAsync(input);
        var read = await harness.Service.GetMineAsync();

        update.IsSuccess.Should().BeTrue();
        read.IsSuccess.Should().BeTrue();
        read.Value!.DisplayName.Should().Be("New Name");
        read.Value.Email.Should().Be("new@example.md");
        read.Value.Phone.Should().Be("+37369123456");
        read.Value.PreferredLanguage.Should().Be("ru",
            "the contact PUT must not touch the language preference — that has its own endpoint.");
    }

    /// <summary>
    /// R0361 — malformed phone surfaces with the standard InvalidPhone code and
    /// the row is left untouched, mirroring the historical safety guarantee of
    /// <see cref="ProfileService.UpdateMineAsync"/>. No silent partial update.
    /// </summary>
    [Fact]
    public async Task UpdateMyContactAsync_InvalidPhone_ReturnsValidationFailureAndDoesNotPersist()
    {
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-contact-badphone",
            DisplayName = "Keep Me",
            Email = "keep@example.md",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var input = new ProfileContactInput(
            DisplayName: "Renamed",
            Email: "renamed@example.md",
            Phone: "123-456-7890");

        var update = await harness.Service.UpdateMyContactAsync(input);

        update.IsFailure.Should().BeTrue();
        update.ErrorCode.Should().Be(ErrorCodes.InvalidPhone);

        var reloaded = await harness.Db.UserProfiles.AsNoTracking()
            .SingleAsync(u => u.Id == profile.Id);
        reloaded.DisplayName.Should().Be("Keep Me",
            "phone validation failure must not partially apply the display-name rename.");
        reloaded.Email.Should().Be("keep@example.md",
            "phone validation failure must not partially apply the e-mail change.");
    }

    /// <summary>
    /// R0361 — when the caller is anonymous (UserId is null) the service refuses
    /// with <see cref="ErrorCodes.Unauthorized"/> as defense in depth, even if
    /// the controller-level <c>[Authorize]</c> attribute is somehow bypassed.
    /// </summary>
    [Fact]
    public async Task UpdateMyContactAsync_Anonymous_ReturnsUnauthorized()
    {
        var harness = Harness.Create();
        // Caller is created with UserId returning null — leave it that way.

        var update = await harness.Service.UpdateMyContactAsync(new ProfileContactInput(
            DisplayName: "Anyone",
            Email: null,
            Phone: null));

        update.IsFailure.Should().BeTrue();
        update.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    // ─────────────────────── R0621 — IssuedDocuments aggregate ───────────────────────

    /// <summary>
    /// R0621 / TOR CF 13.02 — when the caller has no linked Solicitant
    /// (no NationalIdHash on the profile, or the hash matches no Solicitant
    /// row) the aggregate carries an empty issued-documents list. Empty is
    /// the safe default — never <c>null</c>.
    /// </summary>
    [Fact]
    public async Task GetMineAsync_NoLinkedSolicitant_ReturnsEmptyIssuedDocuments()
    {
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-issued-none",
            DisplayName = "No Linkage",
            // NationalIdHash intentionally left null/empty — no Solicitant link possible.
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var read = await harness.Service.GetMineAsync();

        read.IsSuccess.Should().BeTrue();
        read.Value!.IssuedDocuments.Should().NotBeNull(
            "the aggregate must always expose a non-null IssuedDocuments collection.");
        read.Value.IssuedDocuments.Should().BeEmpty(
            "with no linked Solicitant there can be no issued documents.");
    }

    /// <summary>
    /// R0621 / TOR CF 13.02 — single CNAS-issued document on an open dossier
    /// belonging to the caller's Solicitant surfaces on the aggregate with
    /// the Sqid + DocumentTypeCode + Channel populated.
    /// </summary>
    [Fact]
    public async Task GetMineAsync_SingleIssuedDocument_IncludesSummaryRow()
    {
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-issued-one",
            DisplayName = "Has One Doc",
            NationalIdHash = "HASH-ONE",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-2),
            IsActive = true,
        };
        var solicitant = new Solicitant
        {
            NationalId = "0000000000001",
            NationalIdHash = "HASH-ONE",
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Has One Doc",
            CreatedAtUtc = ClockNow.AddDays(-2),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        harness.Db.Solicitants.Add(solicitant);
        await harness.Db.SaveChangesAsync();

        var dossier = new Dossier
        {
            ApplicationId = 0,
            DossierNumber = "D-2026-1",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.Dossiers.Add(dossier);
        await harness.Db.SaveChangesAsync();

        var application = new ServiceApplication
        {
            SolicitantId = solicitant.Id,
            ServicePassportId = 1,
            DossierId = dossier.Id,
            Status = ApplicationStatus.UnderExamination,
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.Applications.Add(application);

        var doc = new Document
        {
            DossierId = dossier.Id,
            Kind = DocumentKind.Decision,
            Title = "Decision 2026-1",
            MimeType = "application/pdf",
            SizeBytes = 1024,
            StorageObjectKey = "obj-key",
            StorageBucket = "bucket",
            ContentSha256Hex = "deadbeef",
            IsSigned = true, // electronic
            CreatedAtUtc = ClockNow,
            IsActive = true,
        };
        harness.Db.Documents.Add(doc);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var read = await harness.Service.GetMineAsync();

        read.IsSuccess.Should().BeTrue();
        read.Value!.IssuedDocuments.Should().HaveCount(1);
        var summary = read.Value.IssuedDocuments[0];
        summary.Sqid.Should().NotBeNullOrEmpty(
            "the document id must be Sqid-encoded at the boundary (CLAUDE.md RULE 3).");
        summary.DocumentTypeCode.Should().Be("Decision");
        summary.Title.Should().Be("Decision 2026-1");
        summary.IssuedAtUtc.Should().Be(ClockNow);
        summary.Channel.Should().Be(IssuedDocumentChannel.Electronic,
            "a signed document was issued via the electronic channel.");
        summary.Status.Should().Be("Active");
        summary.DownloadUrl.Should().Be($"/api/documents/{summary.Sqid}/download");
    }

    /// <summary>
    /// R0621 / TOR CF 13.02 — multiple issued documents come back newest-first
    /// (ordered by <c>CreatedAtUtc DESC</c>) and citizen-supplied attachments
    /// are filtered OUT — only Decision / Certificate / Extract / Information
    /// count as "issued by CNAS".
    /// </summary>
    [Fact]
    public async Task GetMineAsync_MultipleIssuedDocuments_OrderedNewestFirst_ExcludesAttachments()
    {
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "uc13-issued-many",
            DisplayName = "Has Many Docs",
            NationalIdHash = "HASH-MANY",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-5),
            IsActive = true,
        };
        var solicitant = new Solicitant
        {
            NationalId = "0000000000002",
            NationalIdHash = "HASH-MANY",
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Has Many Docs",
            CreatedAtUtc = ClockNow.AddDays(-5),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        harness.Db.Solicitants.Add(solicitant);
        await harness.Db.SaveChangesAsync();

        var dossier = new Dossier
        {
            ApplicationId = 0,
            DossierNumber = "D-2026-2",
            CreatedAtUtc = ClockNow.AddDays(-3),
            IsActive = true,
        };
        harness.Db.Dossiers.Add(dossier);
        await harness.Db.SaveChangesAsync();

        harness.Db.Applications.Add(new ServiceApplication
        {
            SolicitantId = solicitant.Id,
            ServicePassportId = 1,
            DossierId = dossier.Id,
            Status = ApplicationStatus.UnderExamination,
            CreatedAtUtc = ClockNow.AddDays(-3),
            IsActive = true,
        });

        // Two issued documents (different timestamps) + one citizen attachment.
        var oldDoc = new Document
        {
            DossierId = dossier.Id,
            Kind = DocumentKind.Certificate,
            Title = "Certificate (older)",
            MimeType = "application/pdf",
            SizeBytes = 100,
            StorageObjectKey = "k1",
            StorageBucket = "b",
            ContentSha256Hex = "h1",
            IsSigned = false, // paper
            CreatedAtUtc = ClockNow.AddDays(-2),
            IsActive = true,
        };
        var newDoc = new Document
        {
            DossierId = dossier.Id,
            Kind = DocumentKind.Extract,
            Title = "Extract (newer)",
            MimeType = "application/pdf",
            SizeBytes = 200,
            StorageObjectKey = "k2",
            StorageBucket = "b",
            ContentSha256Hex = "h2",
            IsSigned = true,
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        var citizenAttachment = new Document
        {
            DossierId = dossier.Id,
            Kind = DocumentKind.Attachment,
            Title = "Citizen Upload",
            MimeType = "application/pdf",
            SizeBytes = 300,
            StorageObjectKey = "k3",
            StorageBucket = "b",
            ContentSha256Hex = "h3",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        };
        harness.Db.Documents.AddRange(oldDoc, newDoc, citizenAttachment);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var read = await harness.Service.GetMineAsync();

        read.IsSuccess.Should().BeTrue();
        read.Value!.IssuedDocuments.Should().HaveCount(2,
            "the citizen-supplied Attachment kind must be filtered out of the aggregate.");
        read.Value.IssuedDocuments[0].Title.Should().Be("Extract (newer)",
            "newest-first ordering — Extract (created -1d) must come before Certificate (created -2d).");
        read.Value.IssuedDocuments[1].Title.Should().Be("Certificate (older)");
        read.Value.IssuedDocuments[1].Channel.Should().Be(IssuedDocumentChannel.Paper,
            "an unsigned document is treated as a paper issuance.");
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    /// <returns>A converter-less context — encrypted columns persist as plaintext.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-profile-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning a fixed instant for deterministic tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ProfileService Service { get; init; }
        public required ICallerContext Caller { get; init; }

        /// <summary>
        /// Re-points the caller-context substitute at the supplied user id so the
        /// service "knows who the caller is" without requiring an HTTP pipeline.
        /// </summary>
        /// <param name="userId">The internal user primary key.</param>
        public void AsCaller(long userId)
        {
            Caller.UserId.Returns(userId);
            Caller.UserSqid.Returns($"sqid-{userId}");
        }

        public static Harness Create()
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);
            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns((long?)null);
            caller.Roles.Returns(Array.Empty<string>());

            // CnasDbContext implements BOTH ICnasDbContext + IReadOnlyCnasDbContext so
            // the same in-memory store backs reads and writes — read-your-own-writes
            // is therefore deterministic in tests (in production the streaming
            // replica may lag by tens of ms).
            IReadOnlyCnasDbContext readDb = db;
            var service = new ProfileService(db, readDb, sqids, clock, caller);
            return new Harness
            {
                Db = db,
                Service = service,
                Caller = caller,
            };
        }
    }
}
