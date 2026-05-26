using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0210 / R0225 — FluentValidation tests for the translation + help validators.
/// Covers the kebab-case code regex, language allow-list, length caps, and the
/// title / body length constraints on the help registry.
/// </summary>
public class TranslationAndHelpValidatorsTests
{
    [Fact]
    public void TranslationKey_RejectsCodeWithUppercaseOrUnderscore()
    {
        var v = new TranslationKeyUpsertDtoValidator();

        // Uppercase letter — invalid (validator requires kebab-case starting with lowercase).
        var withUpper = v.Validate(new TranslationKeyUpsertDto("Pages.Foo", null, null));
        withUpper.IsValid.Should().BeFalse("uppercase letters in the code must be rejected");

        // Underscore — invalid (the body alphabet allows only lowercase letters, digits, dot and hyphen).
        var withUnderscore = v.Validate(new TranslationKeyUpsertDto("pages_foo", null, null));
        withUnderscore.IsValid.Should().BeFalse("underscores in the code must be rejected");
    }

    [Fact]
    public void TranslationKey_AcceptsCanonicalKebabDottedCode()
    {
        var v = new TranslationKeyUpsertDtoValidator();
        var ok = v.Validate(new TranslationKeyUpsertDto("pages.applications.list.title", null, null));
        ok.IsValid.Should().BeTrue();
    }

    [Fact]
    public void TranslationValueUpsert_Rejects_BlankText()
    {
        var v = new TranslationValueUpsertDtoValidator();
        var result = v.Validate(new TranslationValueUpsertDto(Text: "", TranslatorNote: null));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void TranslationValueUpsert_LanguageHelper_RejectsUnknownLanguage()
    {
        TranslationValueUpsertDtoValidator.LanguageIsSupported("de").Should().BeFalse(
            "the registry only carries ro / en / ru");
        TranslationValueUpsertDtoValidator.LanguageIsSupported("ro").Should().BeTrue();
        TranslationValueUpsertDtoValidator.LanguageIsSupported(null).Should().BeFalse();
    }

    [Fact]
    public void HelpTopic_RejectsBlankModule()
    {
        var v = new HelpTopicUpsertDtoValidator();
        var result = v.Validate(new HelpTopicUpsertDto(
            Code: "pages.applications.applicant-section",
            Module: "",
            AnchorSelector: null));
        result.IsValid.Should().BeFalse("module is required");
    }

    [Fact]
    public void HelpTopicTranslation_AcceptsValid_RejectsOversizedBody()
    {
        var v = new HelpTopicTranslationUpsertDtoValidator();
        var ok = v.Validate(new HelpTopicTranslationUpsertDto(
            Title: "Despre solicitant",
            BodyMarkdown: "# Despre\n\nSecțiune.",
            TranslatorNote: null));
        ok.IsValid.Should().BeTrue();

        var oversize = v.Validate(new HelpTopicTranslationUpsertDto(
            Title: "T",
            BodyMarkdown: new string('a', 20_001),
            TranslatorNote: null));
        oversize.IsValid.Should().BeFalse("body must not exceed the 20000-character cap");
    }
}
