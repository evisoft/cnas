using System;
using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.AccessScope;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Qbe;
using Cnas.Ps.Infrastructure.QueryBudget;
using Cnas.Ps.Infrastructure.Search;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Services.Solicitants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0623 / TOR CF 13.04 — integration tests for
/// <see cref="SolicitantReferenceGuard"/> + the wiring of
/// <see cref="SolicitantService.DeactivateAsync"/> into the guard.
/// </summary>
/// <remarks>
/// Uses the in-memory EF Core provider (the same pattern the rest of the
/// Infrastructure test suite uses). The reference-blocking contract is
/// pure-read so neither connection topology nor migrations are exercised.
/// </remarks>
public sealed class SolicitantReferenceGuardTests
{
    /// <summary>Deterministic UTC clock used so audit fields stay stable.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc);

    // ─────────────────────────── Guard direct tests ───────────────────────────

    /// <summary>
    /// R0623 — A Solicitant with no foreign references reports zero open rows
    /// across every per-table counter.
    /// </summary>
    [Fact]
    public async Task ScanAsync_NoReferences_ReturnsZero()
    {
        var harness = await Harness.CreateWithSolicitantAsync();

        var result = await harness.Guard.ScanAsync(harness.SolicitantSqid);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;
        dto.SolicitantSqid.Should().Be(harness.SolicitantSqid);
        dto.ApplicationsOpen.Should().Be(0);
        dto.DossiersOpen.Should().Be(0);
        dto.DocumentsOpen.Should().Be(0);
        dto.PaymentsOpen.Should().Be(0);
        dto.NotificationsOpen.Should().Be(0);
        dto.TotalOpen.Should().Be(0);
    }

    /// <summary>
    /// R0623 — One open <see cref="ServiceApplication"/> increments
    /// <see cref="SolicitantReferenceScanDto.ApplicationsOpen"/>.
    /// </summary>
    [Fact]
    public async Task ScanAsync_SingleOpenApplication_CountsOne()
    {
        var harness = await Harness.CreateWithSolicitantAsync();
        await harness.SeedApplicationAsync(ApplicationStatus.Submitted);

        var result = await harness.Guard.ScanAsync(harness.SolicitantSqid);

        result.IsSuccess.Should().BeTrue();
        result.Value.ApplicationsOpen.Should().Be(1);
        result.Value.TotalOpen.Should().Be(1);
    }

    /// <summary>
    /// R0623 — Multi-table OPEN references all accrue independently into the
    /// per-table counters and into the total.
    /// </summary>
    [Fact]
    public async Task ScanAsync_MultiTableOpenReferences_AllCounted()
    {
        var harness = await Harness.CreateWithSolicitantAsync();
        var appId = await harness.SeedApplicationAsync(ApplicationStatus.UnderExamination);
        var dossierId = await harness.SeedDossierAsync(appId, closed: false);
        await harness.SeedDocumentAsync(dossierId);
        await harness.SeedBenefitPaymentAsync(BenefitPaymentStatus.Scheduled);
        await harness.SeedNotificationAsync(NotificationDeliveryStatus.Pending);

        var result = await harness.Guard.ScanAsync(harness.SolicitantSqid);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;
        dto.ApplicationsOpen.Should().Be(1);
        dto.DossiersOpen.Should().Be(1);
        dto.DocumentsOpen.Should().Be(1);
        dto.PaymentsOpen.Should().Be(1);
        dto.NotificationsOpen.Should().Be(1);
        dto.TotalOpen.Should().Be(5);
    }

    /// <summary>
    /// R0623 — Closed / terminal-state rows are intentionally NOT counted; the
    /// guard only blocks on OPEN rows so a Solicitant with only historical
    /// artefacts may still be soft-deleted.
    /// </summary>
    [Fact]
    public async Task ScanAsync_ClosedApplication_NotCounted()
    {
        var harness = await Harness.CreateWithSolicitantAsync();
        await harness.SeedApplicationAsync(ApplicationStatus.Closed);
        await harness.SeedApplicationAsync(ApplicationStatus.Rejected);
        await harness.SeedApplicationAsync(ApplicationStatus.Approved);
        await harness.SeedApplicationAsync(ApplicationStatus.Withdrawn);
        await harness.SeedBenefitPaymentAsync(BenefitPaymentStatus.Paid);
        await harness.SeedBenefitPaymentAsync(BenefitPaymentStatus.Returned);
        await harness.SeedBenefitPaymentAsync(BenefitPaymentStatus.Cancelled);
        await harness.SeedNotificationAsync(NotificationDeliveryStatus.Delivered);
        await harness.SeedNotificationAsync(NotificationDeliveryStatus.Failed);
        await harness.SeedNotificationAsync(NotificationDeliveryStatus.Suppressed);

        var result = await harness.Guard.ScanAsync(harness.SolicitantSqid);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalOpen.Should().Be(0, "terminal-state rows must NOT block deactivation");
    }

    /// <summary>
    /// R0623 — Scanning a Solicitant that does not exist short-circuits with
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </summary>
    [Fact]
    public async Task ScanAsync_UnknownSolicitant_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("MISSING").Returns(Result<long>.Success(9999L));

        var result = await harness.Guard.ScanAsync("MISSING");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────── SolicitantService.DeactivateAsync wiring ───────────────────

    /// <summary>
    /// R0623 — Deactivating a Solicitant referenced by ≥1 OPEN row fails with
    /// <see cref="ErrorCodes.SolicitantReferencedByOpenRecords"/> and leaves
    /// the row unchanged.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_ReferencedByOpenRecord_Fails_AndLeavesRowActive()
    {
        var harness = await Harness.CreateWithSolicitantAsync();
        await harness.SeedApplicationAsync(ApplicationStatus.Submitted);

        var result = await harness.Service.DeactivateAsync(harness.SolicitantSqid);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.SolicitantReferencedByOpenRecords);
        var row = harness.Db.Solicitants.Single(s => s.Id == harness.SolicitantId);
        row.IsActive.Should().BeTrue("the soft-delete must NOT proceed when references would be orphaned");
    }

    /// <summary>
    /// R0623 — Deactivating a Solicitant with no open references succeeds and
    /// flips <c>IsActive=false</c>.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_NoReferences_FlipsRowToInactive()
    {
        var harness = await Harness.CreateWithSolicitantAsync();

        var result = await harness.Service.DeactivateAsync(harness.SolicitantSqid);

        result.IsSuccess.Should().BeTrue();
        var row = harness.Db.Solicitants.Single(s => s.Id == harness.SolicitantId);
        row.IsActive.Should().BeFalse();
        row.UpdatedAtUtc.Should().Be(ClockNow);
    }

    /// <summary>
    /// R0623 — Deactivating a Solicitant with only closed-state references
    /// succeeds; historical artefacts must not block the soft-delete.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_ClosedRefsOnly_Succeeds()
    {
        var harness = await Harness.CreateWithSolicitantAsync();
        await harness.SeedApplicationAsync(ApplicationStatus.Closed);
        await harness.SeedBenefitPaymentAsync(BenefitPaymentStatus.Paid);
        await harness.SeedNotificationAsync(NotificationDeliveryStatus.Delivered);

        var result = await harness.Service.DeactivateAsync(harness.SolicitantSqid);

        result.IsSuccess.Should().BeTrue();
        harness.Db.Solicitants.Single(s => s.Id == harness.SolicitantId).IsActive.Should().BeFalse();
    }

    // ─────────────────────────── Test harness ───────────────────────────

    /// <summary>Deterministic clock; one instant for every test.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUTs + DB so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required SolicitantReferenceGuard Guard { get; init; }
        public required SolicitantService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public string SolicitantSqid { get; set; } = string.Empty;
        public long SolicitantId { get; set; }

        public static Harness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            // Default encode/decode for any id; specific overrides below.
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            // CnasDbContext implements BOTH ICnasDbContext + IReadOnlyCnasDbContext.
            IReadOnlyCnasDbContext readDb = db;
            var guard = new SolicitantReferenceGuard(readDb, sqids);

            var qbeConverter = new QbeToLinqConverter(new QbeRegistrySchemaProvider());
            var suggestions = new SearchSuggestionService(new QbeRegistrySchemaProvider());
            var budget = new QueryBudgetService(
                new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance),
                NullLogger<QueryBudgetService>.Instance);
            var accessFilter = new AccessScopeFilter();
            var caller = Substitute.For<ICallerContext>();
            caller.AccessScope.Returns(RolesBasedAccessScope.Unscoped);
            caller.UserSqid.Returns("SQID-ADMIN");

            var clock = new StubClock(ClockNow);
            var service = new SolicitantService(
                db, sqids, budget, qbeConverter, suggestions, accessFilter, caller, guard, clock);

            return new Harness
            {
                Db = db,
                Guard = guard,
                Service = service,
                Sqids = sqids,
            };
        }

        public static async Task<Harness> CreateWithSolicitantAsync()
        {
            var h = Create();
            var solicitant = new Solicitant
            {
                NationalId = "2000000000007",
                NationalIdHash = "h-base-solicitant",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Test Solicitant",
                PreferredLanguage = "ro",
                IsActive = true,
                CreatedAtUtc = ClockNow.AddYears(-1),
            };
            h.Db.Solicitants.Add(solicitant);
            await h.Db.SaveChangesAsync();
            h.SolicitantId = solicitant.Id;
            h.SolicitantSqid = $"SQID-{solicitant.Id}";
            h.Sqids.TryDecode(h.SolicitantSqid).Returns(Result<long>.Success(solicitant.Id));
            return h;
        }

        public async Task<long> SeedApplicationAsync(ApplicationStatus status)
        {
            var app = new ServiceApplication
            {
                SolicitantId = SolicitantId,
                ServicePassportId = 1,
                Status = status,
                FormPayloadJson = "{}",
                SubmittedAtUtc = ClockNow,
                ReferenceNumber = $"PS-{Guid.NewGuid():N}".Substring(0, 16),
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();
            return app.Id;
        }

        public async Task<long> SeedDossierAsync(long applicationId, bool closed)
        {
            var dossier = new Dossier
            {
                ApplicationId = applicationId,
                DossierNumber = $"D-{Guid.NewGuid():N}".Substring(0, 16),
                ClosedAtUtc = closed ? ClockNow : (DateTime?)null,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            return dossier.Id;
        }

        public async Task SeedDocumentAsync(long dossierId)
        {
            var doc = new Document
            {
                DossierId = dossierId,
                Kind = DocumentKind.Attachment,
                Title = "Some attachment",
                MimeType = "application/pdf",
                StorageObjectKey = "k",
                StorageBucket = "b",
                ContentSha256Hex = "abcd",
                SizeBytes = 1,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.Documents.Add(doc);
            await Db.SaveChangesAsync();
        }

        public async Task SeedBenefitPaymentAsync(BenefitPaymentStatus status)
        {
            var pay = new BenefitPayment
            {
                BeneficiarySolicitantId = SolicitantId,
                BenefitType = (BenefitType)0,
                PaymentMonth = new DateOnly(ClockNow.Year, ClockNow.Month, 1),
                GrossAmount = 100m,
                NetAmount = 100m,
                TaxWithheld = 0m,
                Status = status,
                Method = BenefitPaymentMethod.BankTransfer,
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.BenefitPayments.Add(pay);
            await Db.SaveChangesAsync();
        }

        public async Task SeedNotificationAsync(NotificationDeliveryStatus deliveryStatus)
        {
            var n = new Notification
            {
                RecipientUserId = SolicitantId,
                Channel = NotificationChannel.InApp,
                DeliveryStatus = deliveryStatus,
                Subject = "x",
                Body = "y",
                CreatedAtUtc = ClockNow,
                IsActive = true,
            };
            Db.Notifications.Add(n);
            await Db.SaveChangesAsync();
        }
    }

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-solicitant-guard-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }
}
