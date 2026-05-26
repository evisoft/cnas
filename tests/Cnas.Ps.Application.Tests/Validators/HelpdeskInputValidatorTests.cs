using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2500 / TOR PIR 020-023 — unit tests for the helpdesk input validators.
/// Exercises the category-code regex, SLA-minute bounds, severity enum,
/// and the reason / submit / comment / filter envelopes.
/// </summary>
public sealed class HelpdeskInputValidatorTests
{
    private static SupportTicketCategoryCreateInputDto GoodCategory(
        string code = "AUTH",
        int firstResponseMinutes = 60,
        int resolutionMinutes = 480) => new(
            Code: code,
            DisplayName: "Authentication issues",
            Description: "Account locking, password reset, etc.",
            DefaultSeverity: "Normal",
            FirstResponseSlaMinutes: firstResponseMinutes,
            ResolutionSlaMinutes: resolutionMinutes,
            EscalationQueueCode: "L2_AUTH");

    [Fact]
    public void CreateCategory_HappyPath_Accepted()
    {
        var v = new SupportTicketCategoryCreateInputValidator();
        v.Validate(GoodCategory()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateCategory_BadCode_Rejected()
    {
        var v = new SupportTicketCategoryCreateInputValidator();
        v.Validate(GoodCategory(code: "lower_case")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateCategory_BadSlaBounds_Rejected()
    {
        var v = new SupportTicketCategoryCreateInputValidator();
        v.Validate(GoodCategory(firstResponseMinutes: 1)).IsValid.Should().BeFalse();
        v.Validate(GoodCategory(resolutionMinutes: 1)).IsValid.Should().BeFalse();
        v.Validate(GoodCategory(firstResponseMinutes: 99999)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Submit_HappyPath_Accepted()
    {
        var v = new SupportTicketSubmitInputValidator();
        var good = new SupportTicketSubmitInputDto(
            CategoryCode: "AUTH",
            Title: "Cannot login",
            Description: "I keep seeing 'invalid credentials' on every login attempt.",
            Severity: null);
        v.Validate(good).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Submit_BadSeverity_Rejected()
    {
        var v = new SupportTicketSubmitInputValidator();
        var bad = new SupportTicketSubmitInputDto(
            CategoryCode: "AUTH",
            Title: "Cannot login",
            Description: "valid body",
            Severity: "Mega");
        v.Validate(bad).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Resolution_RequiresSummary()
    {
        var v = new SupportTicketResolutionInputValidator();
        v.Validate(new SupportTicketResolutionInputDto(Summary: "")).IsValid.Should().BeFalse();
        v.Validate(new SupportTicketResolutionInputDto(Summary: "ok done")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Filter_Take_Bounded()
    {
        var v = new SupportTicketFilterValidator();
        v.Validate(new SupportTicketFilterDto(Take: 200)).IsValid.Should().BeFalse();
        v.Validate(new SupportTicketFilterDto(Take: 50)).IsValid.Should().BeTrue();
    }
}
