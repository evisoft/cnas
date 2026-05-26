using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — unit tests for the
/// international-agreements routing validators. Pins the happy path plus
/// the negative cases that gate the regex + bounds rules.
/// </summary>
public sealed class IntlAgreementInputValidatorTests
{
    /// <summary>Builds a canonical create-input DTO that should pass validation.</summary>
    private static IntlAgreementReviewCaseCreateInputDto ValidCreate() => new(
        BenefitKind: nameof(IntlAgreementBenefitKind.IncapacityMaternity),
        BeneficiaryIdnp: "2002000000007",
        BeneficiaryDisplayName: "Ion Popescu",
        AgreementCode: "RO_MD_2006",
        HostCountryCode: "RO",
        ReferenceBenefitPassportSqid: null,
        EvidenceJson: null);

    [Fact]
    public void Create_HappyPath_Accepted()
    {
        var v = new IntlAgreementReviewCaseCreateInputValidator();

        var result = v.Validate(ValidCreate());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_BadAgreementCode_Rejected()
    {
        var v = new IntlAgreementReviewCaseCreateInputValidator();
        // Missing trailing year — does not match ^[A-Z]{2}_MD_\d{4}$.
        var input = ValidCreate() with { AgreementCode = "RO_MD_XX" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.AgreementCode));
    }

    [Fact]
    public void Create_HostCountryLowerCase_Rejected()
    {
        var v = new IntlAgreementReviewCaseCreateInputValidator();
        var input = ValidCreate() with { HostCountryCode = "ro" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.HostCountryCode));
    }

    [Fact]
    public void Create_EvidenceJsonTooLarge_Rejected()
    {
        var v = new IntlAgreementReviewCaseCreateInputValidator();
        // Build a JSON object whose total length exceeds the 16 384 cap.
        var hugePayload = new string('a', 17_000);
        var input = ValidCreate() with { EvidenceJson = $"{{\"field\":\"{hugePayload}\"}}" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.EvidenceJson));
    }

    [Fact]
    public void Create_BadBenefitKind_Rejected()
    {
        var v = new IntlAgreementReviewCaseCreateInputValidator();
        var input = ValidCreate() with { BenefitKind = "NotAKind" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.BenefitKind));
    }

    [Fact]
    public void Review_HappyPath_Accepted()
    {
        var v = new IntlAgreementReviewInputValidator();

        var result = v.Validate(new IntlAgreementReviewInputDto(
            Outcome: nameof(IntlAgreementReviewStepOutcome.Approved),
            Note: "Approved at local office — file complete."));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Review_BadOutcome_Rejected()
    {
        var v = new IntlAgreementReviewInputValidator();

        var result = v.Validate(new IntlAgreementReviewInputDto(
            Outcome: "Sideways",
            Note: "Some note long enough."));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IntlAgreementReviewInputDto.Outcome));
    }

    [Fact]
    public void Filter_BadStatus_Rejected()
    {
        var v = new IntlAgreementReviewCaseFilterValidator();

        var result = v.Validate(new IntlAgreementReviewCaseFilterDto(Status: "NotAStatus"));

        result.IsValid.Should().BeFalse();
    }
}
