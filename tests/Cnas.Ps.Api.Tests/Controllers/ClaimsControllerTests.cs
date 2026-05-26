using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Claims;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0831 / R0832 — controller-level tests for
/// <see cref="ClaimsController"/>.
/// </summary>
public sealed class ClaimsControllerTests
{
    /// <summary>Builds a canonical register-input DTO.</summary>
    private static ClaimRegisterInputDto SampleRegisterInput() => new(
        ContributorSqid: "SQID-1",
        Kind: nameof(ClaimKind.Contribution),
        RelatedMonth: new DateOnly(2026, 4, 1),
        PrincipalAmount: 1_500m,
        OpenedDate: new DateOnly(2026, 5, 22));

    /// <summary>Builds a canonical output DTO returned by the service mock.</summary>
    private static ClaimDto SampleClaimDto() => new(
        Id: "CLM-1",
        ContributorSqid: "SQID-1",
        ClaimNumber: "CRN-2026-000001",
        Kind: nameof(ClaimKind.Contribution),
        Status: nameof(ClaimStatus.Open),
        PrincipalAmount: 1_500m,
        PaidAmount: 0m,
        RemainingAmount: 1_500m,
        RelatedMonth: new DateOnly(2026, 4, 1),
        OpenedDate: new DateOnly(2026, 5, 22),
        DueDate: null,
        SettledDate: null,
        CancelledDate: null,
        CancelReason: null,
        RelatedDocumentReference: null);

    /// <summary>Builds a canonical payment-output DTO returned by the service mock.</summary>
    private static ClaimPaymentDto SamplePaymentDto() => new(
        Id: "CLP-1",
        ClaimSqid: "CLM-1",
        PaidDate: new DateOnly(2026, 5, 22),
        Amount: 500m,
        PaymentReference: "PAY-REF-1",
        TreasuryReceiptSqid: null,
        Notes: null);

    /// <summary>Recognised Sqid prefixes used by the stub round-trip.</summary>
    private static readonly string[] SqidPrefixes = ["SQID-", "CLM-"];

    /// <summary>Sqid stub that round-trips "CLM-{id}" / "SQID-{id}" strings.</summary>
    private static ISqidService NewSqidStub()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is null)
            {
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
            }
            foreach (var prefix in SqidPrefixes)
            {
                if (s.StartsWith(prefix, StringComparison.Ordinal)
                    && long.TryParse(s[prefix.Length..], out var id))
                {
                    return Result<long>.Success(id);
                }
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>R0831 — POST /api/claims returns 201 with the Sqid id on success.</summary>
    [Fact]
    public async Task RegisterAsync_ServiceReturnsSuccess_Returns201()
    {
        var svc = Substitute.For<IClaimService>();
        svc.RegisterAsync(Arg.Any<ClaimRegisterInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<ClaimDto>.Success(SampleClaimDto()));
        var controller = new ClaimsController(svc, NewSqidStub());

        var result = await controller.RegisterAsync(SampleRegisterInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        var dto = created.Value.Should().BeOfType<ClaimDto>().Subject;
        dto.Id.Should().Be("CLM-1");
        dto.Status.Should().Be(nameof(ClaimStatus.Open));
    }

    /// <summary>R0832 — POST /api/claims/{sqid}/payments returns 200 with the payment DTO on success.</summary>
    [Fact]
    public async Task RegisterPaymentAsync_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IClaimService>();
        svc.RegisterPaymentAsync(
                Arg.Any<long>(), Arg.Any<ClaimPaymentInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<ClaimPaymentDto>.Success(SamplePaymentDto()));
        var controller = new ClaimsController(svc, NewSqidStub());

        var result = await controller.RegisterPaymentAsync(
            "CLM-1",
            new ClaimPaymentInputDto(
                PaidDate: new DateOnly(2026, 5, 22),
                Amount: 500m,
                PaymentReference: "PAY-REF-1"),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        var dto = ok.Value.Should().BeOfType<ClaimPaymentDto>().Subject;
        dto.Id.Should().Be("CLP-1");
        dto.Amount.Should().Be(500m);
    }

    /// <summary>R0831 — GET /api/contributors/{contributorSqid}/claims returns the list on success.</summary>
    [Fact]
    public async Task ListForContributorAsync_ServiceReturnsList_Returns200()
    {
        var svc = Substitute.For<IClaimService>();
        svc.ListForContributorAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClaimDto> { SampleClaimDto() });
        var controller = new ClaimsController(svc, NewSqidStub());

        var result = await controller.ListForContributorAsync("SQID-1", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        var list = ok.Value.Should().BeAssignableTo<IReadOnlyList<ClaimDto>>().Subject;
        list.Should().HaveCount(1);
        list[0].ClaimNumber.Should().Be("CRN-2026-000001");
    }
}
