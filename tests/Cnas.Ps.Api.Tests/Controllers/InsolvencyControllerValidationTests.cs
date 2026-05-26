using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// iter-149 / Fix 13 — pins the contract that <see cref="InsolvencyController"/>
/// invokes its FluentValidation gates BEFORE forwarding to the service. The
/// previous incarnation injected no validators; a tampered DTO would slip
/// through to the service-layer which then either accepted or returned a
/// less-specific error. These tests wire the REAL validators so a regression
/// that severs the gate would re-surface here.
/// </summary>
public sealed class InsolvencyControllerValidationTests
{
    private static InsolvencyController NewControllerWithRealValidators(IInsolvencyLifecycleService svc)
    {
        var resolveValidator = new InsolvencyResolveInputValidator();
        var claimValidator = new InsolvencyClaimInputValidator();
        var paymentValidator = new InsolvencyPaymentInputValidator();
        var controller = new InsolvencyController(svc, resolveValidator, claimValidator, paymentValidator)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    /// <summary>
    /// Fix 13 — too-short resolution must be rejected by the validator with 400
    /// and the service must NOT be reached.
    /// </summary>
    [Fact]
    public async Task Resolve_TamperedShortResolution_Returns400_WithoutCallingService()
    {
        var svc = Substitute.For<IInsolvencyLifecycleService>();
        var controller = NewControllerWithRealValidators(svc);

        var result = await controller.ResolveAsync(
            "SQID-CASE",
            new InsolvencyResolveInputDto("ab"), // 2 chars — under the 3-char minimum.
            CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceive().ResolveAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Fix 13 — claim with a non-positive amount must be rejected at the
    /// controller boundary with 400; the service must NOT be called.
    /// </summary>
    [Fact]
    public async Task AddClaim_TamperedNegativeAmount_Returns400_WithoutCallingService()
    {
        var svc = Substitute.For<IInsolvencyLifecycleService>();
        var controller = NewControllerWithRealValidators(svc);

        var tampered = new InsolvencyClaimInputDto(
            Amount: -1m,
            Currency: "MDL",
            Description: "tampered",
            IncurredOn: new DateOnly(2026, 1, 1));

        var result = await controller.AddClaimAsync("SQID-CASE", tampered, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceive().AddClaimAsync(
            Arg.Any<string>(), Arg.Any<InsolvencyClaimInputDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Fix 13 — payment with a non-positive amount must be rejected at the
    /// controller boundary with 400; the service must NOT be called.
    /// </summary>
    [Fact]
    public async Task AddPayment_TamperedNegativeAmount_Returns400_WithoutCallingService()
    {
        var svc = Substitute.For<IInsolvencyLifecycleService>();
        var controller = NewControllerWithRealValidators(svc);

        var tampered = new InsolvencyPaymentInputDto(
            Amount: 0m,
            PaymentDate: new DateOnly(2026, 1, 1),
            Reference: null);

        var result = await controller.AddPaymentAsync("SQID-CASE", tampered, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceive().AddPaymentAsync(
            Arg.Any<string>(), Arg.Any<InsolvencyPaymentInputDto>(), Arg.Any<CancellationToken>());
    }
}
