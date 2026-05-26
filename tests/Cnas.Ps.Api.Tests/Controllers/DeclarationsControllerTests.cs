using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Declarations;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0810 / R0811 / R0812 — controller-level tests for
/// <see cref="DeclarationsController"/>. Verifies the success/failure routing
/// for the SFS-register endpoint (the other paths share the same plumbing).
/// </summary>
public sealed class DeclarationsControllerTests
{
    /// <summary>Default canonical SFS input used by happy-path tests.</summary>
    private static DeclarationFromSfsInputDto SampleInput() => new(
        ContributorSqid: "SQID-1",
        ReportingMonth: new DateOnly(2026, 5, 1),
        ReferenceNumber: "SFS-001",
        DeclaredContributionAmount: 1000m);

    /// <summary>Default DTO returned by the service mock.</summary>
    private static DeclarationDto SampleOutput() => new(
        Id: "DECL-1",
        ContributorSqid: "SQID-1",
        Kind: "Sfs",
        ReportingMonth: new DateOnly(2026, 5, 1),
        FiledAtUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
        ReferenceNumber: "SFS-001",
        DeclaredContributionAmount: 1000m,
        AdjustedContributionAmount: null,
        Status: "Received",
        Notes: null,
        IsArchived: false);

    /// <summary>Sqid stub that round-trips "SQID-{id}" strings.</summary>
    private static ISqidService NewSqidStub()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>R0810 — POST /api/declarations/sfs returns 201 with the Sqid id on success.</summary>
    [Fact]
    public async Task RegisterFromSfs_ServiceReturnsSuccess_Returns201()
    {
        var svc = Substitute.For<IDeclarationService>();
        svc.RegisterFromSfsAsync(Arg.Any<DeclarationFromSfsInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<DeclarationDto>.Success(SampleOutput()));
        var controller = new DeclarationsController(svc, NewSqidStub());

        var result = await controller.RegisterFromSfsAsync(SampleInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<DeclarationDto>().Subject;
        dto.Id.Should().Be("DECL-1");
        dto.Kind.Should().Be("Sfs");
    }

    /// <summary>R0810 — service Conflict surfaces as 409 ProblemDetails.</summary>
    [Fact]
    public async Task RegisterFromSfs_ServiceReturnsConflict_Returns409()
    {
        var svc = Substitute.For<IDeclarationService>();
        svc.RegisterFromSfsAsync(Arg.Any<DeclarationFromSfsInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<DeclarationDto>.Failure(ErrorCodes.Conflict, "DECLARATION_DUPLICATE"));
        var controller = new DeclarationsController(svc, NewSqidStub());

        var result = await controller.RegisterFromSfsAsync(SampleInput(), CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(409);
    }

    /// <summary>R0821 — POST /api/declarations/{sqid}/scanned-copy returns 200 with the updated DTO.</summary>
    [Fact]
    public async Task AttachScannedCopy_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IDeclarationService>();
        var updated = SampleOutput() with { HasScannedCopy = true, OcrConfidenceLevel = "High" };
        svc.AttachScannedCopyAsync(
                Arg.Any<long>(),
                Arg.Any<ScannedDeclarationAttachmentInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<DeclarationDto>.Success(updated));
        var controller = new DeclarationsController(svc, NewSqidStub());

        var result = await controller.AttachScannedCopyAsync(
            "SQID-1",
            new ScannedDeclarationAttachmentInputDto(FileBase64: "VGVzdA==", FileName: "form.pdf"),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<DeclarationDto>().Subject;
        dto.HasScannedCopy.Should().BeTrue();
        dto.OcrConfidenceLevel.Should().Be("High");
    }

    /// <summary>R0822 — POST /api/declarations/search returns 200 with the page DTO.</summary>
    [Fact]
    public async Task Search_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IDeclarationService>();
        var page = new DeclarationsListPageDto(
            Items: new[] { SampleOutput() },
            TotalCount: 1,
            AppliedSuggestions: Array.Empty<string>());
        svc.SearchAsync(Arg.Any<DeclarationsSearchInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<DeclarationsListPageDto>.Success(page));
        var controller = new DeclarationsController(svc, NewSqidStub());

        var result = await controller.SearchAsync(new DeclarationsSearchInput(), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<DeclarationsListPageDto>().Subject;
        dto.Items.Should().HaveCount(1);
        dto.TotalCount.Should().Be(1);
    }
}
