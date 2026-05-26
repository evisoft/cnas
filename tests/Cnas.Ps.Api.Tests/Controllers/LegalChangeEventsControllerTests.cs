using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Recalculation;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1503 / TOR §3.7-D — tests for <see cref="LegalChangeEventsController"/>.
/// </summary>
public sealed class LegalChangeEventsControllerTests
{
    /// <summary>Static-readonly in-scope benefit-type list reused across multiple DTOs.</summary>
    private static readonly string[] SampleBenefitTypes = { "OldAgePension" };

    private static LegalChangeEventDto SampleDto() => new(
        Id: "SQID-1",
        Code: "LCE-2026-000001",
        Title: "Test",
        Description: null,
        EffectiveFrom: new DateOnly(2026, 7, 1),
        Scope: "Pension",
        BenefitTypesInScope: SampleBenefitTypes,
        ChangePayloadJson: null,
        Status: "Draft",
        RegisteredAt: DateTime.UtcNow,
        CancellationReason: null);

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(LegalChangeEventsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task Register_HappyPath_Returns201()
    {
        var svc = Substitute.For<ILegalChangeEventService>();
        svc.RegisterAsync(Arg.Any<LegalChangeEventRegisterInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<LegalChangeEventDto>.Success(SampleDto())));
        var controller = new LegalChangeEventsController(svc);

        var result = await controller.RegisterAsync(
            new LegalChangeEventRegisterInputDto(
                Code: null,
                Title: "T",
                Description: null,
                EffectiveFrom: new DateOnly(2026, 7, 1),
                Scope: "Pension",
                BenefitTypesInScope: SampleBenefitTypes,
                ChangePayloadJson: null),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Get_HappyPath_Returns200()
    {
        var svc = Substitute.For<ILegalChangeEventService>();
        svc.GetByIdAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<LegalChangeEventDto>.Success(SampleDto())));
        var controller = new LegalChangeEventsController(svc);

        var r = await controller.GetByIdAsync("SQID-1");

        r.Result.Should().BeOfType<OkObjectResult>();
    }
}
