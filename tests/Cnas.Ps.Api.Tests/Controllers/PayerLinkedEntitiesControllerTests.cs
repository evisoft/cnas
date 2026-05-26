using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Payers;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0301 — unit tests for <see cref="PayerLinkedEntitiesController"/>. Direct construction
/// with NSubstitute fakes for the two service dependencies; <c>WebApplicationFactory</c>
/// is intentionally avoided.
/// </summary>
public sealed class PayerLinkedEntitiesControllerTests
{
    private const string PayerSqid = "p-sqid-123";
    private const long PayerId = 42L;

    /// <summary>Builds a fresh substitute pair (sqid + service) and returns the controller.</summary>
    private static (PayerLinkedEntitiesController Controller, IPayerLinkedEntitiesService Service, ISqidService Sqids)
        Build()
    {
        var svc = Substitute.For<IPayerLinkedEntitiesService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode(PayerSqid).Returns(Result<long>.Success(PayerId));
        return (new PayerLinkedEntitiesController(svc, sqids), svc, sqids);
    }

    [Fact]
    public async Task PutAddress_ServiceSuccess_Returns200WithDto()
    {
        var (controller, svc, _) = Build();
        var output = new PayerAddressDto(
            "addr-sqid", PayerSqid, "S", "C", "R", "MD2001", "MD",
            DateTime.UtcNow, null, "init", "caller");
        svc.UpdateAddressAsync(PayerId, Arg.Any<PayerAddressInputDto>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<PayerAddressDto>.Success(output));
        var input = new PayerAddressInputDto("S", "C", "R", "MD2001", "MD");

        var result = await controller.PutAddressAsync(PayerSqid, input, null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(output);
    }

    [Fact]
    public async Task PostBankAccount_ServiceSuccess_Returns200WithDto()
    {
        var (controller, svc, _) = Build();
        var dto = new PayerBankAccountDto(
            "ba-sqid", PayerSqid, "SRL Test", "MD24AG000000022500931776",
            "Agroindbank", "AGRNMD2X", true, "MDL",
            DateTime.UtcNow, null, "init", "caller");
        svc.AddBankAccountAsync(PayerId, Arg.Any<PayerBankAccountInputDto>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<PayerBankAccountDto>.Success(dto));
        var input = new PayerBankAccountInputDto(
            "SRL Test", "MD24AG000000022500931776", "Agroindbank", "AGRNMD2X", true, "MDL");

        var result = await controller.PostBankAccountAsync(PayerSqid, input, null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetBankAccountsCurrent_ServiceSuccess_Returns200WithRows()
    {
        var (controller, svc, _) = Build();
        var rows = new List<PayerBankAccountDto>
        {
            new("ba-sqid", PayerSqid, "SRL Test", "MD24AG000000022500931776",
                "Agroindbank", "AGRNMD2X", true, "MDL",
                DateTime.UtcNow, null, null, null),
        };
        svc.ListCurrentBankAccountsAsync(PayerId, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<PayerBankAccountDto>>.Success(rows));

        var result = await controller.ListCurrentBankAccountsAsync(PayerSqid, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(rows);
    }
}
