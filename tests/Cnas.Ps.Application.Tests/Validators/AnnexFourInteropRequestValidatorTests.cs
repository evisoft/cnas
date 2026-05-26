using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts.Interop;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1702-R1708 / TOR CF 14.12 / Annex 4 — validator tests for the second
/// batch of Annex-4 request envelopes. Exercises one happy + one rejection
/// path per validator, plus a couple of boundary cases for the
/// agreement-code regex on R1707.
/// </summary>
public sealed class AnnexFourInteropRequestValidatorTests
{
    /// <summary>Canonical valid IDNP shared across happy-path assertions.</summary>
    private const string ValidIdnp = "2000123456782";

    /// <summary>Canonical valid IDNO (13-digit, leading non-zero).</summary>
    private const string ValidIdno = "1003600012345";

    /// <summary>R1702 — happy path: well-formed IDNP envelope passes the validator.</summary>
    [Fact]
    public void ActiveDecisions_ValidIdnp_Passes()
    {
        var v = new ActiveDecisionsRequestDtoValidator();
        var r = v.Validate(new ActiveDecisionsRequestDto(ValidIdnp));
        r.IsValid.Should().BeTrue();
    }

    /// <summary>R1702 — rejected: 12-digit IDNP fails the length rule.</summary>
    [Fact]
    public void ActiveDecisions_TooShortIdnp_Fails()
    {
        var v = new ActiveDecisionsRequestDtoValidator();
        var r = v.Validate(new ActiveDecisionsRequestDto("200012345678"));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1703 — happy path: non-empty Sqid + reasonable year passes.</summary>
    [Fact]
    public void PaymentStatus_ValidEnvelope_Passes()
    {
        var v = new PaymentStatusRequestDtoValidator();
        var r = v.Validate(new PaymentStatusRequestDto("SQID-42", new DateOnly(2026, 1, 1)));
        r.IsValid.Should().BeTrue();
    }

    /// <summary>R1703 — empty DecisionSqid is rejected.</summary>
    [Fact]
    public void PaymentStatus_EmptySqid_Fails()
    {
        var v = new PaymentStatusRequestDtoValidator();
        var r = v.Validate(new PaymentStatusRequestDto(string.Empty, new DateOnly(2026, 1, 1)));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1703 — pathological year (year=1) is rejected.</summary>
    [Fact]
    public void PaymentStatus_OutOfRangeYear_Fails()
    {
        var v = new PaymentStatusRequestDtoValidator();
        var r = v.Validate(new PaymentStatusRequestDto("SQID-42", new DateOnly(1900, 1, 1)));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1704 — happy path: 13-digit numeric taxpayer code passes.</summary>
    [Fact]
    public void PayerData_ValidCode_Passes()
    {
        var v = new PayerDataRequestDtoValidator();
        var r = v.Validate(new PayerDataRequestDto(ValidIdno));
        r.IsValid.Should().BeTrue();
    }

    /// <summary>R1704 — non-digit taxpayer code is rejected.</summary>
    [Fact]
    public void PayerData_NonDigitCode_Fails()
    {
        var v = new PayerDataRequestDtoValidator();
        var r = v.Validate(new PayerDataRequestDto("100ABCDE12345"));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1705 — happy path: IDNP + benefit-type string passes.</summary>
    [Fact]
    public void IsBenefitBeneficiary_ValidEnvelope_Passes()
    {
        var v = new IsBenefitBeneficiaryRequestDtoValidator();
        var r = v.Validate(new IsBenefitBeneficiaryRequestDto(ValidIdnp, "OldAgePension"));
        r.IsValid.Should().BeTrue();
    }

    /// <summary>R1705 — empty BenefitType is rejected.</summary>
    [Fact]
    public void IsBenefitBeneficiary_EmptyBenefitType_Fails()
    {
        var v = new IsBenefitBeneficiaryRequestDtoValidator();
        var r = v.Validate(new IsBenefitBeneficiaryRequestDto(ValidIdnp, string.Empty));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1706 — happy path: 13-digit IDNO + sane year passes.</summary>
    [Fact]
    public void ContributionPaymentInfo_ValidEnvelope_Passes()
    {
        var v = new ContributionPaymentInfoRequestDtoValidator();
        var r = v.Validate(new ContributionPaymentInfoRequestDto(ValidIdno, new DateOnly(2026, 1, 1)));
        r.IsValid.Should().BeTrue();
    }

    /// <summary>R1706 — 12-digit IDNO is rejected.</summary>
    [Fact]
    public void ContributionPaymentInfo_ShortIdno_Fails()
    {
        var v = new ContributionPaymentInfoRequestDtoValidator();
        var r = v.Validate(new ContributionPaymentInfoRequestDto("100360001234", new DateOnly(2026, 1, 1)));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1707 — happy path: stable agreement code with underscores passes.</summary>
    [Fact]
    public void LegalApplicableForm_ValidEnvelope_Passes()
    {
        var v = new LegalApplicableFormRequestDtoValidator();
        var r = v.Validate(new LegalApplicableFormRequestDto(ValidIdnp, "RO_MD_2006"));
        r.IsValid.Should().BeTrue();
    }

    /// <summary>R1707 — agreement code with disallowed characters is rejected.</summary>
    [Theory]
    [InlineData("RO-MD-2006")] // dashes not allowed
    [InlineData("RO MD 2006")] // spaces not allowed
    [InlineData("AB")]            // too short
    [InlineData("")]              // empty
    public void LegalApplicableForm_BadAgreementCode_Fails(string agreementCode)
    {
        var v = new LegalApplicableFormRequestDtoValidator();
        var r = v.Validate(new LegalApplicableFormRequestDto(ValidIdnp, agreementCode));
        r.IsValid.Should().BeFalse();
    }
}
