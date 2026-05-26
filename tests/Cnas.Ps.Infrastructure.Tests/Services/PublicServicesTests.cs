using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.PublicServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0511 / R0512 / R0513 / TOR CF 02.01 — integration tests for the public
/// anonymous services. Exercises:
/// <list type="bullet">
///   <item>R0511 <see cref="MedicalCertificateStatusService"/> — happy path,
///   unknown cert, captcha rejection, audit hash, PCCM unavailable.</item>
///   <item>R0512 <see cref="OnlineAppointmentBookingService"/> — directory
///   ordering, deep-link substitution, unknown-branch handling.</item>
///   <item>R0513 <see cref="ExtractCnasCodeService"/> — happy path, IDNP
///   mismatch, DOB mismatch, captcha rejection, audit-hash prefix.</item>
/// </list>
/// Anti-enumeration discipline (no PII echo, no audit-row PII) is asserted
/// across all three services.
/// </summary>
public sealed class PublicServicesTests
{
    /// <summary>Fixed clock instant used by every audit-row timestamp.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-public-services-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Builds a syntactically valid IDNP from a 12-digit prefix by computing the
    /// mod-10 weighted checksum (same algorithm as the Idnp value object).
    /// </summary>
    private static string BuildIdnp(string twelveDigitPrefix)
    {
        int[] weights = { 7, 3, 1 };
        int sum = 0;
        for (int i = 0; i < 12; i++)
        {
            sum += (twelveDigitPrefix[i] - '0') * weights[i % 3];
        }
        int check = (10 - (sum % 10)) % 10;
        return twelveDigitPrefix + check.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Canonical valid IDNP used by happy-path cases.</summary>
    private static readonly string ValidIdnp = BuildIdnp("200012345678");

    /// <summary>Second canonical IDNP used by mismatch cases.</summary>
    private static readonly string ValidIdnpB = BuildIdnp("199912345678");

    /// <summary>Captcha token the in-test verifier accepts.</summary>
    private const string ValidCaptchaToken = "valid-test-token";

    // ────────────────────────────────────────────────────────────────────────
    // R0511 — MedicalCertificateStatusService
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R0511 / Test 1 — valid PCCM-known certificate + valid captcha returns
    /// the projected status DTO including issued date.
    /// </summary>
    [Fact]
    public async Task R0511_LookupAsync_KnownActiveCertificate_ReturnsActiveStatusAndIssuedDate()
    {
        var harness = MedicalCertHarness.Create();
        var request = new MedicalCertificateLookupDto(
            MockPccmGateway.ActiveSampleCertificateNumber,
            ValidIdnp,
            new DateOnly(1980, 1, 1),
            ValidCaptchaToken);

        var result = await harness.Service.LookupAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Active");
        result.Value.IssuedDate.Should().NotBeNull();
        result.Value.IssuerName.Should().Be(MockPccmGateway.SampleIssuerName);
        result.Value.CertificateNumber.Should().Be(MockPccmGateway.ActiveSampleCertificateNumber);
    }

    /// <summary>
    /// R0511 / Test 2 — unknown certificate collapses to <c>Status="NotFound"</c>
    /// without revealing whether the certificate or the IDNP was at fault.
    /// </summary>
    [Fact]
    public async Task R0511_LookupAsync_UnknownCertificate_ReturnsNotFound_WithoutLeakingEnumeration()
    {
        var harness = MedicalCertHarness.Create();
        var request = new MedicalCertificateLookupDto(
            "PCCM-DOES-NOT-EXIST-9999",
            ValidIdnp,
            new DateOnly(1980, 1, 1),
            ValidCaptchaToken);

        var result = await harness.Service.LookupAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("NotFound");
        result.Value.IssuedDate.Should().BeNull();
        result.Value.IssuerName.Should().BeNull();
    }

    /// <summary>
    /// R0511 / Test 3 — empty / invalid CAPTCHA token surfaces the stable
    /// <see cref="ErrorCodes.CaptchaTokenMissing"/> or
    /// <see cref="ErrorCodes.CaptchaTokenInvalid"/> code (never collapses to
    /// generic Internal).
    /// </summary>
    [Fact]
    public async Task R0511_LookupAsync_InvalidCaptcha_ReturnsCaptchaTokenInvalid()
    {
        var harness = MedicalCertHarness.Create();
        var request = new MedicalCertificateLookupDto(
            MockPccmGateway.ActiveSampleCertificateNumber,
            ValidIdnp,
            new DateOnly(1980, 1, 1),
            CaptchaToken: "wrong-token");

        var result = await harness.Service.LookupAsync(request);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenInvalid);
    }

    /// <summary>
    /// R0511 / Test 4 — the audit row carries the SHA-256 HASH of the
    /// certificate number, never the plaintext. The IDNP is never written.
    /// </summary>
    [Fact]
    public async Task R0511_LookupAsync_AuditRow_CarriesHashedCertNumber_NotPlaintext()
    {
        var harness = MedicalCertHarness.Create();
        var certNumber = MockPccmGateway.ActiveSampleCertificateNumber;
        var request = new MedicalCertificateLookupDto(
            certNumber,
            ValidIdnp,
            new DateOnly(1980, 1, 1),
            ValidCaptchaToken);

        await harness.Service.LookupAsync(request);

        var capturedDetails = harness.LastAuditDetailsJson;
        capturedDetails.Should().NotBeNull();
        // Plaintext cert number must NOT appear in the audit details.
        capturedDetails!.Should().NotContain(certNumber);
        // The IDNP must NOT appear in the audit details either.
        capturedDetails.Should().NotContain(ValidIdnp);
        // The details payload should be a JSON object carrying a hex hash.
        using var doc = JsonDocument.Parse(capturedDetails!);
        doc.RootElement.TryGetProperty("certificateNumberHash", out var hashElem).Should().BeTrue();
        hashElem.GetString().Should().NotBeNullOrWhiteSpace();
        hashElem.GetString()!.Length.Should().Be(64); // SHA-256 in lower hex.
    }

    /// <summary>
    /// R0511 / Test 5 — PCCM gateway failure (MConnect down, malformed payload)
    /// surfaces <see cref="ErrorCodes.MConnectFailed"/>.
    /// </summary>
    [Fact]
    public async Task R0511_LookupAsync_PccmUnavailable_ReturnsMConnectFailed()
    {
        var harness = MedicalCertHarness.Create(failingGateway: true);
        var request = new MedicalCertificateLookupDto(
            MockPccmGateway.ActiveSampleCertificateNumber,
            ValidIdnp,
            new DateOnly(1980, 1, 1),
            ValidCaptchaToken);

        var result = await harness.Service.LookupAsync(request);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    /// <summary>
    /// R0511 / Test 6 — malformed IDNP returns the specific
    /// <see cref="ErrorCodes.InvalidIdnp"/> code (anti-enumeration applies only
    /// to lookup outcomes, not to client-side validation bugs).
    /// </summary>
    [Fact]
    public async Task R0511_LookupAsync_MalformedIdnp_ReturnsInvalidIdnp()
    {
        var harness = MedicalCertHarness.Create();
        var request = new MedicalCertificateLookupDto(
            MockPccmGateway.ActiveSampleCertificateNumber,
            Idnp: "not-13-digits",
            new DateOnly(1980, 1, 1),
            ValidCaptchaToken);

        var result = await harness.Service.LookupAsync(request);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    // ────────────────────────────────────────────────────────────────────────
    // R0512 — OnlineAppointmentBookingService
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R0512 / Test 6 — directory returns the five seeded branches in
    /// alphabetical-by-name order.
    /// </summary>
    [Fact]
    public async Task R0512_GetDirectoryAsync_ReturnsAllActiveBranches_OrderedByName()
    {
        var harness = AppointmentsHarness.Create();
        await harness.SeedDefaultBranchesAsync();

        var result = await harness.Service.GetDirectoryAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Branches.Should().HaveCount(5);
        var names = result.Value.Branches.Select(b => b.Name).ToList();
        names.Should().BeInAscendingOrder();
    }

    /// <summary>
    /// R0512 / Test 7 — unknown branch returns
    /// <see cref="ErrorCodes.NotFound"/> with the stable
    /// <c>"BRANCH_NOT_FOUND"</c> human message.
    /// </summary>
    [Fact]
    public async Task R0512_ResolveDeepLinkAsync_UnknownBranch_ReturnsNotFound()
    {
        var harness = AppointmentsHarness.Create();
        await harness.SeedDefaultBranchesAsync();

        var result = await harness.Service.ResolveDeepLinkAsync("UNKNOWN-BRANCH");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        result.ErrorMessage.Should().Be("BRANCH_NOT_FOUND");
    }

    /// <summary>
    /// R0512 / Test 8 — the deep-link template's <c>{branchCode}</c> placeholder
    /// is substituted with the actual branch code.
    /// </summary>
    [Fact]
    public async Task R0512_ResolveDeepLinkAsync_KnownBranch_SubstitutesBranchCodePlaceholder()
    {
        var harness = AppointmentsHarness.Create();
        await harness.SeedDefaultBranchesAsync();

        var result = await harness.Service.ResolveDeepLinkAsync("BALTI");

        result.IsSuccess.Should().BeTrue();
        result.Value.Url.Should().Contain("branch=BALTI");
        result.Value.Url.Should().NotContain("{branchCode}");
    }

    /// <summary>
    /// R0512 supplementary — soft-deleted branch is invisible from the public
    /// surface.
    /// </summary>
    [Fact]
    public async Task R0512_GetDirectoryAsync_DoesNotReturnInactiveBranches()
    {
        var harness = AppointmentsHarness.Create();
        await harness.SeedBranchAsync("ACTIVE", "Active Branch", "Chișinău");
        await harness.SeedBranchAsync("INACTIVE", "Inactive Branch", "Bălți", isActive: false);

        var result = await harness.Service.GetDirectoryAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Branches.Should().HaveCount(1);
        result.Value.Branches[0].Code.Should().Be("ACTIVE");
    }

    // ────────────────────────────────────────────────────────────────────────
    // R0513 — ExtractCnasCodeService
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R0513 / Test 9 — matching (IDNP, DOB) pair returns Found=true with a
    /// synthesized CNAS code.
    /// </summary>
    [Fact]
    public async Task R0513_LookupAsync_MatchingIdnpAndDob_ReturnsFoundWithCnasCode()
    {
        var harness = ExtractHarness.Create();
        var birthDate = new DateOnly(1980, 1, 1);
        await harness.SeedInsuredPersonAsync(ValidIdnp, birthDate);

        var request = new ExtractCnasCodeLookupDto(ValidIdnp, birthDate, ValidCaptchaToken);
        var result = await harness.Service.LookupAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Found.Should().BeTrue();
        result.Value.CnasCode.Should().NotBeNullOrWhiteSpace();
        result.Value.CnasCode.Should().StartWith(ExtractCnasCodeService.CnasCodePrefix);
    }

    /// <summary>
    /// R0513 / Test 10 — IDNP not in the InsuredPerson table → Found=false,
    /// audit row still written.
    /// </summary>
    [Fact]
    public async Task R0513_LookupAsync_UnknownIdnp_ReturnsNotFoundAndAudits()
    {
        var harness = ExtractHarness.Create();
        await harness.SeedInsuredPersonAsync(ValidIdnp, new DateOnly(1980, 1, 1));

        var request = new ExtractCnasCodeLookupDto(ValidIdnpB, new DateOnly(1985, 6, 1), ValidCaptchaToken);
        var result = await harness.Service.LookupAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Found.Should().BeFalse();
        result.Value.CnasCode.Should().BeNull();
        harness.LastAuditDetailsJson.Should().NotBeNull();
    }

    /// <summary>
    /// R0513 / Test 11 — IDNP matches but DOB doesn't → Found=false (parity
    /// with the unknown-IDNP shape for anti-enumeration).
    /// </summary>
    [Fact]
    public async Task R0513_LookupAsync_DobMismatch_ReturnsNotFound()
    {
        var harness = ExtractHarness.Create();
        await harness.SeedInsuredPersonAsync(ValidIdnp, new DateOnly(1980, 1, 1));

        var request = new ExtractCnasCodeLookupDto(ValidIdnp, new DateOnly(1990, 12, 31), ValidCaptchaToken);
        var result = await harness.Service.LookupAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Found.Should().BeFalse();
        result.Value.CnasCode.Should().BeNull();
    }

    /// <summary>
    /// R0513 / Test 12 — empty / invalid CAPTCHA token surfaces the stable
    /// captcha error code.
    /// </summary>
    [Fact]
    public async Task R0513_LookupAsync_InvalidCaptcha_ReturnsCaptchaTokenInvalid()
    {
        var harness = ExtractHarness.Create();
        await harness.SeedInsuredPersonAsync(ValidIdnp, new DateOnly(1980, 1, 1));

        var request = new ExtractCnasCodeLookupDto(ValidIdnp, new DateOnly(1980, 1, 1), "wrong-token");
        var result = await harness.Service.LookupAsync(request);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CaptchaTokenInvalid);
    }

    /// <summary>
    /// R0513 / Test 13 — the audit details JSON carries only the first 8 hex
    /// characters of the IDNP hash; the raw IDNP is never written.
    /// </summary>
    [Fact]
    public async Task R0513_LookupAsync_AuditRow_CarriesIdnpHashPrefixOnly()
    {
        var harness = ExtractHarness.Create();
        var birthDate = new DateOnly(1980, 1, 1);
        await harness.SeedInsuredPersonAsync(ValidIdnp, birthDate);

        var request = new ExtractCnasCodeLookupDto(ValidIdnp, birthDate, ValidCaptchaToken);
        await harness.Service.LookupAsync(request);

        var details = harness.LastAuditDetailsJson;
        details.Should().NotBeNull();
        details!.Should().NotContain(ValidIdnp);
        using var doc = JsonDocument.Parse(details!);
        doc.RootElement.TryGetProperty("idnpHashPrefix", out var prefixElem).Should().BeTrue();
        var prefix = prefixElem.GetString();
        prefix.Should().NotBeNullOrWhiteSpace();
        // Length of the hash prefix is the constant defined by the service.
        prefix!.Length.Should().Be(ExtractCnasCodeService.IdnpHashPrefixLength);
        // The full 44-char base64 hash must NOT appear in the details payload.
        var fullHash = IdHashHelper.Hash(ValidIdnp);
        details.Should().NotContain(fullHash);
    }

    /// <summary>
    /// R0513 / Test 14 — IDNP validator rejects non-13-digit input.
    /// </summary>
    [Fact]
    public async Task R0513_LookupAsync_MalformedIdnp_ReturnsInvalidIdnp()
    {
        var harness = ExtractHarness.Create();

        var request = new ExtractCnasCodeLookupDto("123", new DateOnly(1980, 1, 1), ValidCaptchaToken);
        var result = await harness.Service.LookupAsync(request);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    /// <summary>
    /// R0513 supplementary — soft-deleted insured person is invisible from the
    /// lookup (anti-enumeration parity with unknown IDNP).
    /// </summary>
    [Fact]
    public async Task R0513_LookupAsync_SoftDeletedInsuredPerson_ReturnsNotFound()
    {
        var harness = ExtractHarness.Create();
        var birthDate = new DateOnly(1980, 1, 1);
        await harness.SeedInsuredPersonAsync(ValidIdnp, birthDate, isActive: false);

        var request = new ExtractCnasCodeLookupDto(ValidIdnp, birthDate, ValidCaptchaToken);
        var result = await harness.Service.LookupAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Found.Should().BeFalse();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test harnesses — one per service so the dependency graphs stay focused.
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Stub clock returning <see cref="ClockNow"/>.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    /// <summary>
    /// In-test captcha verifier that accepts exactly
    /// <see cref="ValidCaptchaToken"/>; null/whitespace returns
    /// <c>CaptchaTokenMissing</c>; anything else returns
    /// <c>CaptchaTokenInvalid</c>.
    /// </summary>
    private sealed class StubCaptchaVerifier : ICaptchaVerifier
    {
        public Task<Result> VerifyAsync(string? token, string? remoteIp, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Task.FromResult(Result.Failure(ErrorCodes.CaptchaTokenMissing, "missing"));
            }
            if (string.Equals(token, ValidCaptchaToken, StringComparison.Ordinal))
            {
                return Task.FromResult(Result.Success());
            }
            return Task.FromResult(Result.Failure(ErrorCodes.CaptchaTokenInvalid, "invalid"));
        }
    }

    /// <summary>
    /// Failing PCCM gateway used by the unavailable-path test. Always returns a
    /// failed <see cref="Result{T}"/> with <see cref="ErrorCodes.MConnectFailed"/>.
    /// </summary>
    private sealed class FailingPccmGateway : IPccmGateway
    {
        public Task<Result<PccmCertificateStatus>> LookupCertificateAsync(
            string certificateNumber,
            string idnp,
            DateOnly dateOfBirth,
            CancellationToken ct = default)
            => Task.FromResult(Result<PccmCertificateStatus>.Failure(
                ErrorCodes.MConnectFailed,
                "PCCM unavailable."));
    }

    /// <summary>Captures the first <c>detailsJson</c> argument passed to <see cref="IAuditService"/>.</summary>
    private static (IAuditService Audit, Func<string?> Last) NewAuditCapture()
    {
        string? lastDetails = null;
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Do<string>(s => lastDetails = s),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        return (audit, () => lastDetails);
    }

    /// <summary>Builds a Sqid mock that mirrors the production encoder's "SQID-{id}" shape.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Builds an anonymous-caller context (no UserSqid / UserId).</summary>
    private static ICallerContext NewAnonymousCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns((string?)null);
        caller.UserId.Returns((long?)null);
        caller.SourceIp.Returns("203.0.113.1");
        caller.CorrelationId.Returns("corr-public-1");
        caller.Roles.Returns(Array.Empty<string>());
        return caller;
    }

    /// <summary>Bundles dependencies for the R0511 medical-cert service.</summary>
    private sealed class MedicalCertHarness
    {
        public required MedicalCertificateStatusService Service { get; init; }
        public required Func<string?> AuditDetailsAccessor { get; init; }

        public string? LastAuditDetailsJson => AuditDetailsAccessor();

        public static MedicalCertHarness Create(bool failingGateway = false)
        {
            var (audit, getDetails) = NewAuditCapture();
            var caller = NewAnonymousCaller();
            IPccmGateway gateway = failingGateway ? new FailingPccmGateway() : new MockPccmGateway();
            var captcha = new StubCaptchaVerifier();
            var svc = new MedicalCertificateStatusService(gateway, captcha, audit, caller);
            return new MedicalCertHarness
            {
                Service = svc,
                AuditDetailsAccessor = getDetails,
            };
        }
    }

    /// <summary>Bundles dependencies for the R0512 appointments service.</summary>
    private sealed class AppointmentsHarness
    {
        public required CnasDbContext Db { get; init; }
        public required OnlineAppointmentBookingService Service { get; init; }

        public static AppointmentsHarness Create()
        {
            var db = CreateContext();
            var (audit, _) = NewAuditCapture();
            var caller = NewAnonymousCaller();
            var options = Options.Create(new AppointmentBookingOptions
            {
                DeepLinkTemplate = "https://programare.cnas.md/?branch={branchCode}&lang=ro",
            });
            var svc = new OnlineAppointmentBookingService(db, audit, caller, options);
            return new AppointmentsHarness { Db = db, Service = svc };
        }

        public async Task SeedBranchAsync(string code, string name, string city, bool isActive = true)
        {
            Db.CnasBranches.Add(new CnasBranch
            {
                Code = code,
                Name = name,
                City = city,
                Address = "Strada Test 1",
                Phone = "+37322111111",
                IsActive = isActive,
                CreatedAtUtc = ClockNow,
            });
            await Db.SaveChangesAsync();
        }

        public async Task SeedDefaultBranchesAsync()
        {
            await SeedBranchAsync("BALTI", "CNAS Bălți", "Bălți");
            await SeedBranchAsync("CAHUL", "CNAS Cahul", "Cahul");
            await SeedBranchAsync("CHISINAU-CENTRU", "CNAS Chișinău Centru", "Chișinău");
            await SeedBranchAsync("COMRAT", "CNAS Comrat", "Comrat");
            await SeedBranchAsync("EDINET", "CNAS Edineț", "Edineț");
        }
    }

    /// <summary>Bundles dependencies for the R0513 extract-CNAS-code service.</summary>
    private sealed class ExtractHarness
    {
        public required CnasDbContext Db { get; init; }
        public required ExtractCnasCodeService Service { get; init; }
        public required Func<string?> AuditDetailsAccessor { get; init; }

        public string? LastAuditDetailsJson => AuditDetailsAccessor();

        public static ExtractHarness Create()
        {
            var db = CreateContext();
            var (audit, getDetails) = NewAuditCapture();
            var caller = NewAnonymousCaller();
            var captcha = new StubCaptchaVerifier();
            var sqids = NewSqidMock();
            var svc = new ExtractCnasCodeService(db, captcha, IdHashHelper.Instance, sqids, audit, caller);
            return new ExtractHarness
            {
                Db = db,
                Service = svc,
                AuditDetailsAccessor = getDetails,
            };
        }

        public async Task SeedInsuredPersonAsync(string idnp, DateOnly birthDate, bool isActive = true)
        {
            Db.InsuredPersons.Add(new InsuredPerson
            {
                Idnp = idnp,
                IdnpHash = IdHashHelper.Hash(idnp),
                LastName = "Popescu",
                FirstName = "Ion",
                BirthDate = birthDate,
                IsDeceased = false,
                RegisteredAtUtc = ClockNow.AddDays(-30),
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = isActive,
            });
            await Db.SaveChangesAsync();
        }
    }
}
