using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Treasury;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0911 — controller-level tests for <see cref="TreasuryPaymentsController"/>.
/// </summary>
public sealed class TreasuryPaymentsControllerTests
{
    /// <summary>Sample input DTO for the import endpoint.</summary>
    private static TreasuryPaymentReceiptImportInputDto SampleInput() => new(
        TreasuryReferenceNumber: "TRS-0001",
        ReceiptDate: new DateOnly(2026, 5, 10),
        PayerContributorSqid: "SQID-1",
        ReportingMonth: new DateOnly(2026, 4, 1),
        AmountReceived: 5_000m);

    /// <summary>Sample output DTO returned by the service mock.</summary>
    private static TreasuryPaymentReceiptDto SampleOutput() => new(
        Id: "TRS-SQ-1",
        TreasuryReferenceNumber: "TRS-0001",
        ReceiptDate: new DateOnly(2026, 5, 10),
        PayerContributorSqid: "SQID-1",
        ReportingMonth: new DateOnly(2026, 4, 1),
        AmountReceived: 5_000m,
        DistributionStatus: nameof(TreasuryPaymentDistributionStatus.Pending),
        DistributedAtUtc: null,
        DistributionFailureReason: null,
        UndistributedRemainderAmount: null);

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

    /// <summary>R0911 — POST /api/treasury-payments/import returns 201 with Sqid id on success.</summary>
    [Fact]
    public async Task ImportAsync_ServiceReturnsSuccess_Returns201()
    {
        var svc = Substitute.For<ITreasuryPaymentService>();
        svc.ImportReceiptAsync(Arg.Any<TreasuryPaymentReceiptImportInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<TreasuryPaymentReceiptDto>.Success(SampleOutput()));
        var controller = new TreasuryPaymentsController(svc, NewSqidStub());

        var result = await controller.ImportAsync(SampleInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        var dto = created.Value.Should().BeOfType<TreasuryPaymentReceiptDto>().Subject;
        dto.Id.Should().Be("TRS-SQ-1");
        dto.DistributionStatus.Should().Be(nameof(TreasuryPaymentDistributionStatus.Pending));
    }
}
