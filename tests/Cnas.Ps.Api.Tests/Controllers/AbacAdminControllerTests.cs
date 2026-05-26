using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2271 / TOR SEC 025 — tests for <see cref="AbacAdminController"/>. Verifies
/// the cnas-admin authorize gate, the create happy-path, and the parse-error
/// → HTTP 400 mapping.
/// </summary>
public sealed class AbacAdminControllerTests
{
    private static AbacRuleSetDto SampleRuleSet(string id = "SQID-1")
        => new(
            Id: id,
            PolicyName: "DOSSIER.READ",
            DisplayName: "Read dossiers",
            Description: null,
            DefaultEffect: "Deny",
            IsActive: true,
            Rules: Array.Empty<AbacRuleDto>());

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(AbacAdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task CreateRuleSet_HappyPath_Returns201()
    {
        var dto = SampleRuleSet();
        var svc = Substitute.For<IAbacRuleRegistryService>();
        svc.CreateRuleSetAsync(Arg.Any<AbacRuleSetCreateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AbacRuleSetDto>.Success(dto)));
        var controller = new AbacAdminController(svc);

        var result = await controller.CreateRuleSetAsync(
            new AbacRuleSetCreateInputDto("DOSSIER.READ", "Read dossiers", null, "Deny"),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedResult>().Subject;
        created.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task AddRule_ParseError_Returns400AbacParseError()
    {
        var svc = Substitute.For<IAbacRuleRegistryService>();
        svc.AddRuleAsync(Arg.Any<string>(), Arg.Any<AbacRuleInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AbacRuleDto>.Failure(ErrorCodes.AbacParseError, "stub: bad expression.")));
        var controller = new AbacAdminController(svc);

        var result = await controller.AddRuleAsync(
            "SQID-1",
            new AbacRuleInputDto(0, "Allow", "bad", null),
            CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value).Should().Be(ErrorCodes.AbacParseError);
    }

    [Fact]
    public async Task GetRuleSetByPolicyName_NotFound_Returns404()
    {
        var svc = Substitute.For<IAbacRuleRegistryService>();
        svc.GetRuleSetByPolicyNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AbacRuleSetDto>.Failure(ErrorCodes.AbacNotFound, "missing")));
        var controller = new AbacAdminController(svc);

        var result = await controller.GetRuleSetByPolicyNameAsync("NOPE.NEVER", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }
}
