using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1906 / TOR Annex 6 — FluentValidation rules for the report-distribution
/// admin DTOs. Exercises the report-code regex, the conditional email
/// validation, the date-range coherence, and the change-reason length
/// window.
/// </summary>
public class ReportDistributionInputValidatorTests
{
    private static ReportDistributionRuleCreateInputDto ValidCreate() => new(
        ReportCode: "ACCESS_RIGHTS.FULL_MATRIX",
        Channel: "Email",
        RecipientKind: "EmailAddress",
        RecipientCode: "ops@example.org",
        Format: "Pdf",
        Priority: "Normal",
        EffectiveFrom: new DateOnly(2026, 5, 1),
        EffectiveUntil: new DateOnly(2026, 12, 31),
        Notes: "Quarterly access-rights audit fan-out.");

    [Fact]
    public void Create_HappyPath_Passes()
    {
        var v = new ReportDistributionRuleCreateInputValidator();
        var result = v.Validate(ValidCreate());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_BadReportCodeShape_Rejected()
    {
        var v = new ReportDistributionRuleCreateInputValidator();
        var result = v.Validate(ValidCreate() with { ReportCode = "access.lowercase" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportDistributionRuleCreateInputDto.ReportCode));
    }

    [Fact]
    public void Create_BadEmailWhenKindIsEmailAddress_Rejected()
    {
        var v = new ReportDistributionRuleCreateInputValidator();
        var result = v.Validate(ValidCreate() with
        {
            RecipientKind = "EmailAddress",
            RecipientCode = "not-an-email",
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportDistributionRuleCreateInputDto.RecipientCode));
    }

    [Fact]
    public void Create_EffectiveUntilBeforeFrom_Rejected()
    {
        var v = new ReportDistributionRuleCreateInputValidator();
        var result = v.Validate(ValidCreate() with
        {
            EffectiveFrom = new DateOnly(2026, 5, 10),
            EffectiveUntil = new DateOnly(2026, 5, 1),
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportDistributionRuleCreateInputDto.EffectiveUntil));
    }

    [Fact]
    public void Create_NotesTooLong_Rejected()
    {
        var v = new ReportDistributionRuleCreateInputValidator();
        var notes = new string('x', 1001);
        var result = v.Validate(ValidCreate() with { Notes = notes });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportDistributionRuleCreateInputDto.Notes));
    }

    [Fact]
    public void Create_NonEmailRecipientKind_AcceptsArbitraryRecipientCode()
    {
        var v = new ReportDistributionRuleCreateInputValidator();
        var result = v.Validate(ValidCreate() with
        {
            RecipientKind = "Group",
            RecipientCode = "OFFICE_CHISINAU_CENTRU",
        });
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Modify_RequiresChangeReason()
    {
        var v = new ReportDistributionRuleModifyInputValidator();
        var result = v.Validate(new ReportDistributionRuleModifyInputDto(
            Channel: null,
            RecipientKind: null,
            RecipientCode: null,
            Format: null,
            Priority: null,
            EffectiveFrom: null,
            EffectiveUntil: null,
            Notes: null,
            ChangeReason: "ok"));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportDistributionRuleModifyInputDto.ChangeReason));
    }

    [Fact]
    public void Reason_HappyPath_Passes()
    {
        var v = new ReportDistributionReasonInputValidator();
        var result = v.Validate(new ReportDistributionReasonInputDto(
            "Disabling — recipient on extended leave through Q3."));
        result.IsValid.Should().BeTrue();
    }
}
