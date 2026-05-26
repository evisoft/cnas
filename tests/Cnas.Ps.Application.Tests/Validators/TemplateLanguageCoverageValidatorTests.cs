using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2003 / R0133 — unit tests for the template-language coverage validators.
/// Covers the filter-envelope happy-path + page-bound rejection, the
/// language-code regex, and the acknowledgement-note length rule.
/// </summary>
public sealed class TemplateLanguageCoverageValidatorTests
{
    /// <summary>CA1861 — hoisted to a static field to avoid per-call allocation.</summary>
    private static readonly string[] RoEnRu = ["ro", "en", "ru"];

    /// <summary>CA1861 — single-entry uppercase required-language list used by the rejection test.</summary>
    private static readonly string[] UpperCaseRo = ["RO"];

    /// <summary>CA1861 — 11-entry required-language list used by the too-many-languages test.</summary>
    private static readonly string[] ElevenLanguages =
        ["aa", "bb", "cc", "dd", "ee", "ff", "gg", "hh", "ii", "jj", "kk"];

    // ─────────────── TemplateLanguageCoverageFilterValidator ───────────────

    [Fact]
    public void FilterValidator_HappyPath_Accepts()
    {
        var validator = new TemplateLanguageCoverageFilterValidator();
        var filter = new TemplateLanguageCoverageFilterDto(
            RequiredLanguages: RoEnRu,
            OnlyApproved: true,
            IncludeRetiredTemplates: false,
            Skip: 0,
            Take: 100);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void FilterValidator_NullRequiredLanguages_AcceptsAsDefault()
    {
        var validator = new TemplateLanguageCoverageFilterValidator();
        var filter = new TemplateLanguageCoverageFilterDto(
            RequiredLanguages: null,
            OnlyApproved: true,
            IncludeRetiredTemplates: false,
            Skip: 0,
            Take: 100);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void FilterValidator_TakeZero_Rejects()
    {
        var validator = new TemplateLanguageCoverageFilterValidator();
        var filter = new TemplateLanguageCoverageFilterDto(
            RequiredLanguages: null,
            OnlyApproved: true,
            IncludeRetiredTemplates: false,
            Skip: 0,
            Take: 0);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(filter.Take));
    }

    [Fact]
    public void FilterValidator_BadLanguageCode_Rejects()
    {
        var validator = new TemplateLanguageCoverageFilterValidator();
        var filter = new TemplateLanguageCoverageFilterDto(
            RequiredLanguages: UpperCaseRo, // uppercase — should fail
            OnlyApproved: true);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void FilterValidator_TooManyLanguages_Rejects()
    {
        var validator = new TemplateLanguageCoverageFilterValidator();
        var filter = new TemplateLanguageCoverageFilterDto(
            RequiredLanguages: ElevenLanguages,
            OnlyApproved: true);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeFalse();
    }

    // ─────────────── TemplateLanguageCoverageFindingFilterValidator ───────────────

    [Fact]
    public void FindingFilterValidator_HappyPath_Accepts()
    {
        var validator = new TemplateLanguageCoverageFindingFilterValidator();
        var filter = new TemplateLanguageCoverageFindingFilterDto(
            Acknowledged: false,
            MissingLanguage: "ru",
            Skip: 0,
            Take: 50);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void FindingFilterValidator_BadLanguage_Rejects()
    {
        var validator = new TemplateLanguageCoverageFindingFilterValidator();
        var filter = new TemplateLanguageCoverageFindingFilterDto(
            Acknowledged: null,
            MissingLanguage: "Z");

        var result = validator.Validate(filter);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void FindingFilterValidator_TakeOverCap_Rejects()
    {
        var validator = new TemplateLanguageCoverageFindingFilterValidator();
        var filter = new TemplateLanguageCoverageFindingFilterDto(
            Acknowledged: null,
            MissingLanguage: null,
            Skip: 0,
            Take: 201);

        var result = validator.Validate(filter);

        result.IsValid.Should().BeFalse();
    }

    // ─────────────── TemplateLanguageCoverageAcknowledgeInputValidator ───────────────

    [Fact]
    public void AcknowledgeValidator_HappyPath_Accepts()
    {
        var validator = new TemplateLanguageCoverageAcknowledgeInputValidator();
        var input = new TemplateLanguageCoverageAcknowledgeInputDto(
            Note: "Translation queued in batch 42.");

        var result = validator.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void AcknowledgeValidator_NoteTooShort_Rejects()
    {
        var validator = new TemplateLanguageCoverageAcknowledgeInputValidator();
        var input = new TemplateLanguageCoverageAcknowledgeInputDto(Note: "X");

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void AcknowledgeValidator_NoteTooLong_Rejects()
    {
        var validator = new TemplateLanguageCoverageAcknowledgeInputValidator();
        var input = new TemplateLanguageCoverageAcknowledgeInputDto(
            Note: new string('x', 1001));

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
    }
}
