using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Registers;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1601 / R1602 — controller-level unit tests for <see cref="RegistersController"/>.
///
/// <para>
/// iter-149 / Fix 6 — these tests pin the security contract that the optional
/// beneficiaryIdnp filter is sourced from the <c>X-Beneficiary-Idnp</c> request
/// header rather than the <c>?beneficiaryIdnp=</c> query-string parameter. The
/// query channel routinely leaks into reverse-proxy access logs, browser
/// history, and HTTP referrer headers; PII identifiers must move through a
/// channel that the operational log corpus does NOT capture.
/// </para>
/// </summary>
public sealed class RegistersControllerTests
{
    private static (RegistersController Controller, IBeneficiaryPaymentAccountsRegister Accounts) NewSut()
    {
        var decisions = Substitute.For<IDecisionsRegister>();
        var accounts = Substitute.For<IBeneficiaryPaymentAccountsRegister>();
        return (new RegistersController(decisions, accounts), accounts);
    }

    /// <summary>
    /// Fix 6 — the FromHeader-bound IDNP must reach the register projection as
    /// the first positional argument, demonstrating that the parameter binding
    /// works against the header channel.
    /// </summary>
    [Fact]
    public async Task ListPaymentAccounts_HeaderIdnp_ReachesService()
    {
        var (controller, accounts) = NewSut();
        var empty = new PagedResult<BeneficiaryPaymentAccountRowDto>(
            Array.Empty<BeneficiaryPaymentAccountRowDto>(), 1, 20, 0);
        accounts.ListAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<BeneficiaryPaymentAccountRowDto>>.Success(empty));

        const string idnp = "2002004123456";
        var result = await controller.ListPaymentAccountsAsync(
            beneficiaryIdnp: idnp,
            page: 1,
            pageSize: 20,
            cancellationToken: CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        await accounts.Received(1).ListAsync(idnp, 1, 20, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Fix 6 — when no header is supplied the controller forwards a null filter
    /// (matching the original "list all" behaviour). The query-string surface
    /// of the controller intentionally does NOT bind a beneficiaryIdnp parameter
    /// any more, so any consumer attempting to pass it through the URL receives
    /// the "list all" page (no filtering) — verified here by asserting the
    /// service was called with `null` even though the action was invoked with
    /// no header.
    /// </summary>
    [Fact]
    public async Task ListPaymentAccounts_NoHeader_ForwardsNullFilter()
    {
        var (controller, accounts) = NewSut();
        var empty = new PagedResult<BeneficiaryPaymentAccountRowDto>(
            Array.Empty<BeneficiaryPaymentAccountRowDto>(), 1, 20, 0);
        accounts.ListAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<BeneficiaryPaymentAccountRowDto>>.Success(empty));

        var result = await controller.ListPaymentAccountsAsync(
            beneficiaryIdnp: null,
            page: 1,
            pageSize: 20,
            cancellationToken: CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        await accounts.Received(1).ListAsync(null, 1, 20, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Fix 6 — reflection check that the controller's <c>beneficiaryIdnp</c>
    /// parameter is decorated with <see cref="Microsoft.AspNetCore.Mvc.FromHeaderAttribute"/>
    /// (and NOT with <see cref="Microsoft.AspNetCore.Mvc.FromQueryAttribute"/>),
    /// pinning the binding source so a future refactor cannot silently revert
    /// the parameter back to a query-string channel.
    /// </summary>
    [Fact]
    public void ListPaymentAccounts_BindsBeneficiaryIdnp_FromHeader()
    {
        var method = typeof(RegistersController)
            .GetMethod(nameof(RegistersController.ListPaymentAccountsAsync));
        method.Should().NotBeNull();
        var idnpParam = Array.Find(method!.GetParameters(), p => p.Name == "beneficiaryIdnp");
        idnpParam.Should().NotBeNull();

        var fromHeader = idnpParam!.GetCustomAttributes(typeof(FromHeaderAttribute), inherit: false);
        fromHeader.Should().NotBeEmpty(
            "Fix 6 — beneficiaryIdnp must be bound from the X-Beneficiary-Idnp request header, not a query-string parameter.");
        ((FromHeaderAttribute)fromHeader[0]).Name.Should().Be("X-Beneficiary-Idnp");

        var fromQuery = idnpParam.GetCustomAttributes(typeof(FromQueryAttribute), inherit: false);
        fromQuery.Should().BeEmpty(
            "Fix 6 — beneficiaryIdnp must NOT be bound from the query string (query strings leak PII into logs).");
    }
}
