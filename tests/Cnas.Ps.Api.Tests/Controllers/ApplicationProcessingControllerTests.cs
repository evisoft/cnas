using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.ApplicationProcessing;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0701 / TOR CF 21.01-02 — controller-level unit tests for
/// <see cref="ApplicationProcessingController"/>. Validates the authorize-policy
/// gate (cnas-user / cnas-admin roles), the Sqid-decode path, the 200 happy-path
/// shape, and the 403 ProblemDetails mapping when the service returns
/// <see cref="ErrorCodes.Forbidden"/>.
/// </summary>
public sealed class ApplicationProcessingControllerTests
{
    /// <summary>Returns a fresh service substitute.</summary>
    private static IApplicationProcessingContextService NewServiceMock() =>
        Substitute.For<IApplicationProcessingContextService>();

    /// <summary>Builds the controller with a deterministic Sqid decoder ("SQID-{id}" ↔ id).</summary>
    private static ApplicationProcessingController NewController(
        IApplicationProcessingContextService svc,
        ISqidService? sqids = null)
    {
        if (sqids is null)
        {
            sqids = Substitute.For<ISqidService>();
            sqids.TryDecode(Arg.Any<string?>())
                 .Returns(call =>
                 {
                     var s = call.Arg<string?>();
                     if (s is not null
                         && s.StartsWith("SQID-", StringComparison.Ordinal)
                         && long.TryParse(s.AsSpan(5), out var id))
                     {
                         return Result<long>.Success(id);
                     }
                     return Result<long>.Failure(ErrorCodes.InvalidSqid, "Invalid sqid.");
                 });
        }
        return new ApplicationProcessingController(svc, sqids);
    }

    /// <summary>Builds a minimal valid context DTO for happy-path tests.</summary>
    private static ApplicationProcessingContextDto SampleContext(string appSqid)
        => new(
            ApplicationSqid: appSqid,
            Status: "Submitted",
            Applicant: new ApplicantProfileDto(
                SolicitantSqid: "SQID-1",
                DisplayName: "Maria",
                NationalIdHashPrefix: "deadbeef",
                Email: null,
                PhoneE164: null,
                CurrentAddress: null,
                CurrentContact: null,
                CurrentCivilStatus: null,
                RecentActivityPeriods: Array.Empty<ContributorActivityPeriodDto>()),
            OpenTasks: Array.Empty<WorkflowTaskBriefDto>(),
            DecisionDrafts: Array.Empty<DecisionBriefDto>(),
            Attachments: Array.Empty<AttachmentBriefDto>(),
            AuditTimeline: Array.Empty<AuditTimelineEntryDto>(),
            SuggestedNextActions: Array.Empty<string>(),
            HasUnappliedPrefill: false,
            GeneratedAtUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc));

    /// <summary>The controller MUST be gated by an [Authorize] attribute.</summary>
    [Fact]
    public void Controller_HasAuthorizeAttribute()
    {
        var attrs = typeof(ApplicationProcessingController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();
        attrs.Should().NotBeEmpty(
            "the processing-context controller must be gated by [Authorize].");
        attrs.Any(a => a.Roles is not null
                       && a.Roles.Contains("cnas-admin", StringComparison.Ordinal)
                       && a.Roles.Contains("cnas-user", StringComparison.Ordinal))
            .Should().BeTrue("the controller's role gate must allow both cnas-user and cnas-admin.");
    }

    /// <summary>R0701 / Test 15 — GET success returns 200 with the populated DTO.</summary>
    [Fact]
    public async Task R0701_Get_Success_Returns200_WithContextDto()
    {
        var svc = NewServiceMock();
        var dto = SampleContext("SQID-42");
        svc.GetForCurrentUserAsync(42L, Arg.Any<CancellationToken>())
           .Returns(Result<ApplicationProcessingContextDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.GetProcessingContextAsync(
            "SQID-42",
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>R0701 — Forbidden from the service surfaces as 403 ProblemDetails.</summary>
    [Fact]
    public async Task R0701_Get_Forbidden_Returns403()
    {
        var svc = NewServiceMock();
        svc.GetForCurrentUserAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
           .Returns(Result<ApplicationProcessingContextDto>.Failure(
               ErrorCodes.Forbidden, "permission required"));
        var controller = NewController(svc);

        var result = await controller.GetProcessingContextAsync(
            "SQID-42",
            cancellationToken: CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(403);
    }

    /// <summary>R0701 — Bad Sqid returns 400 ProblemDetails.</summary>
    [Fact]
    public async Task R0701_Get_BadSqid_Returns400()
    {
        var svc = NewServiceMock();
        var controller = NewController(svc);

        var result = await controller.GetProcessingContextAsync(
            "not-a-sqid",
            cancellationToken: CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
    }
}
