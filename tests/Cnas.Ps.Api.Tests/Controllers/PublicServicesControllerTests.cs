using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0511 / R0512 / R0513 — controller-level unit tests for
/// <see cref="PublicServicesController"/>. Asserts the happy-path 200 shapes,
/// the 4xx ProblemDetails mapping, and the no-PII discipline (response bodies
/// must never carry raw IDNP or full name).
/// </summary>
public sealed class PublicServicesControllerTests
{
    private static IMedicalCertificateStatusService NewMedicalMock() => Substitute.For<IMedicalCertificateStatusService>();
    private static IOnlineAppointmentBookingService NewAppointmentsMock() => Substitute.For<IOnlineAppointmentBookingService>();
    private static IExtractCnasCodeService NewExtractMock() => Substitute.For<IExtractCnasCodeService>();

    private static PublicServicesController NewController(
        IMedicalCertificateStatusService? medical = null,
        IOnlineAppointmentBookingService? appointments = null,
        IExtractCnasCodeService? extract = null)
        => new(
            medical ?? NewMedicalMock(),
            appointments ?? NewAppointmentsMock(),
            extract ?? NewExtractMock());

    /// <summary>
    /// Controller test — R0511 happy path returns 200 with the projected DTO,
    /// no PII echoed.
    /// </summary>
    [Fact]
    public async Task R0511_LookupMedicalCertificateAsync_ServiceSuccess_Returns200_WithoutIdnpInBody()
    {
        var svc = NewMedicalMock();
        var dto = new MedicalCertificateStatusDto(
            CertificateNumber: "PCCM-ACTIVE-001",
            Status: "Active",
            IssuedDate: new DateOnly(2026, 5, 1),
            ValidFromDate: new DateOnly(2026, 5, 1),
            ValidToDate: new DateOnly(2026, 5, 21),
            IssuerName: "IMSP Centrul Medical");
        svc.LookupAsync(Arg.Any<MedicalCertificateLookupDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<MedicalCertificateStatusDto>.Success(dto));
        var controller = NewController(medical: svc);

        var body = new MedicalCertificateLookupDto(
            "PCCM-ACTIVE-001",
            "2000123456789",
            new DateOnly(1980, 1, 1),
            "valid-test-token");
        var result = await controller.LookupMedicalCertificateAsync(body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeOfType<MedicalCertificateStatusDto>().Subject;
        // No PII echoed — the response shape carries only certificate metadata.
        returned.GetType().GetProperties().Should().NotContain(p => p.Name.Contains("Idnp", StringComparison.Ordinal));
    }

    /// <summary>
    /// Controller test — R0511 captcha-invalid surfaces as 400 ProblemDetails
    /// carrying the stable <see cref="ErrorCodes.CaptchaTokenInvalid"/> code.
    /// </summary>
    [Fact]
    public async Task R0511_LookupMedicalCertificateAsync_CaptchaInvalid_Returns400()
    {
        var svc = NewMedicalMock();
        svc.LookupAsync(Arg.Any<MedicalCertificateLookupDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<MedicalCertificateStatusDto>.Failure(
               ErrorCodes.CaptchaTokenInvalid, "captcha rejected"));
        var controller = NewController(medical: svc);

        var body = new MedicalCertificateLookupDto(
            "PCCM-ACTIVE-001", "2000123456789", new DateOnly(1980, 1, 1), "bad-token");
        var result = await controller.LookupMedicalCertificateAsync(body, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["errorCode"].Should().Be(ErrorCodes.CaptchaTokenInvalid);
    }

    /// <summary>
    /// Controller test — R0512 directory returns 200 with the list payload.
    /// </summary>
    [Fact]
    public async Task R0512_GetDirectoryAsync_Returns200_WithBranches()
    {
        var svc = NewAppointmentsMock();
        var directory = new AppointmentBookingDirectoryDto(
            Branches: new[]
            {
                new AppointmentBranchDto("BALTI", "CNAS Bălți", "Bălți", null, null),
            },
            DeepLinkTemplate: "https://x/?b={branchCode}");
        svc.GetDirectoryAsync(Arg.Any<CancellationToken>())
           .Returns(Result<AppointmentBookingDirectoryDto>.Success(directory));
        var controller = NewController(appointments: svc);

        var result = await controller.GetAppointmentDirectoryAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<AppointmentBookingDirectoryDto>();
    }

    /// <summary>
    /// Controller test — R0512 unknown branch returns 404 ProblemDetails.
    /// </summary>
    [Fact]
    public async Task R0512_ResolveDeepLinkAsync_NotFound_Returns404()
    {
        var svc = NewAppointmentsMock();
        svc.ResolveDeepLinkAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<AppointmentDeepLinkDto>.Failure(ErrorCodes.NotFound, "BRANCH_NOT_FOUND"));
        var controller = NewController(appointments: svc);

        var result = await controller.ResolveAppointmentDeepLinkAsync("UNKNOWN", CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(404);
    }

    /// <summary>
    /// Controller test — R0513 happy path returns 200 with Found=true; the
    /// response body never carries the supplied IDNP.
    /// </summary>
    [Fact]
    public async Task R0513_ExtractCnasCodeAsync_ServiceSuccess_Returns200_WithoutIdnpEcho()
    {
        var svc = NewExtractMock();
        svc.LookupAsync(Arg.Any<ExtractCnasCodeLookupDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<ExtractCnasCodeResultDto>.Success(new ExtractCnasCodeResultDto(true, "PA-SQID-1")));
        var controller = NewController(extract: svc);

        const string suppliedIdnp = "2000123456789";
        var body = new ExtractCnasCodeLookupDto(suppliedIdnp, new DateOnly(1980, 1, 1), "valid-test-token");
        var result = await controller.ExtractCnasCodeAsync(body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeOfType<ExtractCnasCodeResultDto>().Subject;
        returned.Found.Should().BeTrue();
        returned.CnasCode.Should().Be("PA-SQID-1");
        // The DTO has no IDNP field — verify by reflection.
        returned.GetType().GetProperties().Should().NotContain(p => p.Name.Contains("Idnp", StringComparison.Ordinal));
        returned.GetType().GetProperties().Should().NotContain(p => p.Name.Contains("Name", StringComparison.Ordinal));
    }

    /// <summary>
    /// Controller test — R0513 captcha-invalid surfaces as 400 ProblemDetails.
    /// </summary>
    [Fact]
    public async Task R0513_ExtractCnasCodeAsync_CaptchaInvalid_Returns400()
    {
        var svc = NewExtractMock();
        svc.LookupAsync(Arg.Any<ExtractCnasCodeLookupDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<ExtractCnasCodeResultDto>.Failure(
               ErrorCodes.CaptchaTokenInvalid, "captcha rejected"));
        var controller = NewController(extract: svc);

        var body = new ExtractCnasCodeLookupDto("2000123456789", new DateOnly(1980, 1, 1), "bad-token");
        var result = await controller.ExtractCnasCodeAsync(body, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
    }
}
