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
/// R0815 — controller-level tests for
/// <see cref="PaymentCorrectionsController"/>.
/// </summary>
public sealed class PaymentCorrectionsControllerTests
{
    /// <summary>Builds a canonical create-input DTO.</summary>
    private static PaymentCorrectionCreateInputDto SampleCreateInput() => new(
        OriginalReceiptSqid: "SQID-1",
        Kind: nameof(PaymentCorrectionKind.Reverse),
        Reason: "Duplicate receipt.");

    /// <summary>Builds a canonical output DTO returned by the service mock.</summary>
    private static PaymentCorrectionDto SampleDto() => new(
        Id: "PCR-1",
        OriginalReceiptSqid: "SQID-1",
        Kind: nameof(PaymentCorrectionKind.Reverse),
        Status: nameof(PaymentCorrectionStatus.Draft),
        RedirectedToContributorSqid: null,
        RedirectedToMonth: null,
        AdjustedAmount: null,
        RequestedByUserSqid: "USR-1",
        ApprovedByUserSqid: null,
        Reason: "Duplicate receipt.",
        CreatedUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
        AppliedUtc: null,
        CancelReason: null);

    /// <summary>Recognised Sqid prefixes used by the stub round-trip.</summary>
    private static readonly string[] SqidPrefixes = ["SQID-", "PCR-"];

    /// <summary>Sqid stub that round-trips "PCR-{id}" / "SQID-{id}" strings.</summary>
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

    /// <summary>R0815 — POST /api/payment-corrections returns 201 with the Sqid id on success.</summary>
    [Fact]
    public async Task CreateAsync_ServiceReturnsSuccess_Returns201()
    {
        var svc = Substitute.For<IPaymentCorrectionService>();
        svc.CreateAsync(Arg.Any<PaymentCorrectionCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<PaymentCorrectionDto>.Success(SampleDto()));
        var controller = new PaymentCorrectionsController(svc, NewSqidStub());

        var result = await controller.CreateAsync(SampleCreateInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        var dto = created.Value.Should().BeOfType<PaymentCorrectionDto>().Subject;
        dto.Id.Should().Be("PCR-1");
    }

    /// <summary>R0815 — POST /api/payment-corrections/{sqid}/apply returns 200 on success.</summary>
    [Fact]
    public async Task ApplyAsync_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IPaymentCorrectionService>();
        svc.ApplyAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var controller = new PaymentCorrectionsController(svc, NewSqidStub());

        var result = await controller.ApplyAsync("PCR-1", CancellationToken.None);

        var ok = result.Should().BeOfType<OkResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
    }
}
