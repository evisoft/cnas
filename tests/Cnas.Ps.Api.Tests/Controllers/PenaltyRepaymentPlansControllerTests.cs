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
/// R0817 / TOR BP 1.2-H — controller-level tests for
/// <see cref="PenaltyRepaymentPlansController"/>.
/// </summary>
public sealed class PenaltyRepaymentPlansControllerTests
{
    /// <summary>Sample plan DTO returned by the service mock.</summary>
    private static PenaltyRepaymentPlanDto SampleDto() => new(
        Id: "PRP-1",
        LatePaymentPenaltySqid: "SQID-99",
        InstallmentCount: 3,
        InstallmentAmount: 33.33m,
        FirstInstallmentDueDate: new DateOnly(2026, 6, 1),
        Status: nameof(PenaltyRepaymentPlanStatus.Active),
        PaidInstallmentCount: 0,
        RemainingAmount: 100m,
        CreatedUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
        CompletedUtc: null,
        CancelledUtc: null,
        CancelReason: null);

    /// <summary>Recognised Sqid prefixes used by the stub round-trip.</summary>
    private static readonly string[] SqidPrefixes = ["PRP-", "SQID-"];

    /// <summary>Sqid stub that round-trips "PRP-{id}" / "SQID-{id}" strings.</summary>
    private static ISqidService NewSqidStub()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is null) return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad");
            foreach (var prefix in SqidPrefixes)
            {
                if (s.StartsWith(prefix, StringComparison.Ordinal)
                    && long.TryParse(s[prefix.Length..], out var id))
                {
                    return Result<long>.Success(id);
                }
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad");
        });
        return sqids;
    }

    /// <summary>R0817 — POST /api/penalty-repayment-plans returns 201 with the Sqid id on success.</summary>
    [Fact]
    public async Task CreateAsync_HappyPath_Returns201WithSqidId()
    {
        var svc = Substitute.For<IPenaltyRepaymentService>();
        svc.CreatePlanAsync(Arg.Any<PenaltyRepaymentCreatePlanInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<PenaltyRepaymentPlanDto>.Success(SampleDto()));
        var controller = new PenaltyRepaymentPlansController(svc, NewSqidStub());

        var input = new PenaltyRepaymentCreatePlanInputDto(
            LatePaymentPenaltySqid: "SQID-99",
            InstallmentCount: 3,
            FirstInstallmentDueDate: new DateOnly(2026, 6, 1));
        var result = await controller.CreateAsync(input, CancellationToken.None);

        var created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        var dto = created.Value.Should().BeOfType<PenaltyRepaymentPlanDto>().Subject;
        dto.Id.Should().Be("PRP-1");
        dto.Status.Should().Be(nameof(PenaltyRepaymentPlanStatus.Active));
    }
}
