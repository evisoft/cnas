using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Financials;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0814 — controller-level tests for
/// <see cref="BassRefundsController"/>.
/// </summary>
public sealed class BassRefundsControllerTests
{
    /// <summary>Builds a canonical request-input DTO.</summary>
    private static BassRefundRequestInputDto SampleRequestInput() => new(
        ContributorSqid: "SQID-1",
        RelatedMonth: new DateOnly(2026, 4, 1),
        RefundAmount: 250m,
        AuthorisationDocumentReference: "DOC-1");

    /// <summary>Builds a canonical output DTO returned by the service mock.</summary>
    private static BassRefundDto SampleRefundDto() => new(
        Id: "BRF-1",
        ContributorSqid: "SQID-1",
        RelatedMonth: new DateOnly(2026, 4, 1),
        RefundAmount: 250m,
        Status: nameof(BassRefundStatus.Requested),
        AuthorisationDocumentReference: "DOC-1",
        RequestedByUserSqid: "USR-1",
        ApprovedByUserSqid: null,
        ApprovedDate: null,
        TreasuryDispatchReference: null,
        IssuedDate: null,
        ConfirmedDate: null,
        CancelReason: null,
        CancelledDate: null);

    /// <summary>Recognised Sqid prefixes used by the stub round-trip.</summary>
    private static readonly string[] SqidPrefixes = ["SQID-", "BRF-"];

    /// <summary>Sqid stub that round-trips "BRF-{id}" / "SQID-{id}" strings.</summary>
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

    /// <summary>R0814 — POST /api/bass-refunds/request returns 201 with the Sqid id on success.</summary>
    [Fact]
    public async Task RequestAsync_ServiceReturnsSuccess_Returns201()
    {
        var svc = Substitute.For<IBassRefundService>();
        svc.RequestAsync(Arg.Any<BassRefundRequestInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<BassRefundDto>.Success(SampleRefundDto()));
        var controller = new BassRefundsController(svc, NewSqidStub());

        var result = await controller.RequestAsync(SampleRequestInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        var dto = created.Value.Should().BeOfType<BassRefundDto>().Subject;
        dto.Id.Should().Be("BRF-1");
        dto.Status.Should().Be(nameof(BassRefundStatus.Requested));
    }

    /// <summary>R0814 — POST /api/bass-refunds/{sqid}/approve returns 204 on success.</summary>
    [Fact]
    public async Task ApproveAsync_ServiceReturnsSuccess_Returns204()
    {
        var svc = Substitute.For<IBassRefundService>();
        svc.ApproveAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var controller = new BassRefundsController(svc, NewSqidStub());

        var result = await controller.ApproveAsync("BRF-1", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }
}
