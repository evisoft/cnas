using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0196 / TOR CF 23.02 — unit tests for the audit-category input validators.
/// Exercises the SCREAMING_SNAKE_CASE category-code regex (including dotted
/// namespace segments), the severity enum-membership rule, and the modify /
/// filter envelopes.
/// </summary>
public sealed class AuditCategoryInputValidatorTests
{
    private static AuditCategoryCreateInputDto Good(string code = "AUTH") => new(
        Code: code,
        DisplayName: "Authentication & session lifecycle",
        Description: "Login / logout / session-lock events.",
        DefaultSeverity: "Notice");

    [Fact]
    public void Create_HappyPath_Accepted()
    {
        var v = new AuditCategoryCreateInputValidator();
        v.Validate(Good()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_DottedCode_Accepted()
    {
        // Seed list includes APPLICATION.RECEIVE — dotted namespace segment.
        var v = new AuditCategoryCreateInputValidator();
        v.Validate(Good(code: "APPLICATION.RECEIVE")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_LowercaseCode_Rejected()
    {
        var v = new AuditCategoryCreateInputValidator();
        v.Validate(Good(code: "auth")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_UnknownSeverity_Rejected()
    {
        var v = new AuditCategoryCreateInputValidator();
        var input = Good() with { DefaultSeverity = "NotASeverity" };
        v.Validate(input).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Modify_HappyPath_Accepted()
    {
        var v = new AuditCategoryModifyInputValidator();
        var input = new AuditCategoryModifyInputDto(
            DisplayName: "Renamed",
            Description: null,
            DefaultSeverity: "Critical",
            ChangeReason: "Rebrand");
        v.Validate(input).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Modify_ShortChangeReason_Rejected()
    {
        var v = new AuditCategoryModifyInputValidator();
        var input = new AuditCategoryModifyInputDto(
            DisplayName: null,
            Description: null,
            DefaultSeverity: null,
            ChangeReason: "x");
        v.Validate(input).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Filter_NegativeSkip_Rejected()
    {
        var v = new AuditCategoryFilterValidator();
        v.Validate(new AuditCategoryFilterDto(IsActive: null, Skip: -1, Take: 10)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Filter_TakeAboveCap_Rejected()
    {
        var v = new AuditCategoryFilterValidator();
        v.Validate(new AuditCategoryFilterDto(IsActive: true, Skip: 0, Take: 9999)).IsValid.Should().BeFalse();
    }
}
