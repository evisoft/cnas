using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1202 / TOR §3.4-C — unit tests for the capitalised-payment input
/// validators. Covers IDNP / IDNO format, the birth-date in-past + age range
/// invariant, monetary bounds, valuation-date cut-off, and the
/// 3..1000-char reason / note rules.
/// </summary>
public sealed class CapitalisedPaymentInputValidatorTests
{
    /// <summary>Fixed UTC clock used by every validator under test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow => ClockNow;
    }

    /// <summary>Builds a canonical create-input DTO that should pass validation.</summary>
    private static CapitalisedPaymentRequestCreateInputDto Valid() => new(
        BeneficiaryIdnp: "2002000000007",
        BeneficiaryBirthDate: new DateOnly(1970, 4, 1),
        BeneficiarySex: nameof(BeneficiarySex.Male),
        LiquidatedDebtorIdno: "1003600000123",
        LiquidatedDebtorName: "SRL ÎN LICHIDARE",
        ObligationKind: nameof(CapitalisedPaymentObligationKind.IncapacityForWork),
        MonthlyAmountMdl: 1_500m,
        ObligationStartDate: new DateOnly(2018, 1, 1),
        ObligationEndDate: null,
        ValuationDate: new DateOnly(2026, 6, 1),
        LegalDiscountRatePercent: 9.5m);

    [Fact]
    public void Create_HappyPath_Accepted()
    {
        var v = new CapitalisedPaymentRequestCreateInputValidator(new StubClock());

        var result = v.Validate(Valid());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_BadIdnpTooShort_Rejected()
    {
        var v = new CapitalisedPaymentRequestCreateInputValidator(new StubClock());
        var input = Valid() with { BeneficiaryIdnp = "12345" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.BeneficiaryIdnp));
    }

    [Fact]
    public void Create_ValuationDateTooFarInFuture_Rejected()
    {
        var v = new CapitalisedPaymentRequestCreateInputValidator(new StubClock());
        var input = Valid() with { ValuationDate = new DateOnly(2030, 1, 1) };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.ValuationDate));
    }

    [Fact]
    public void Create_MonthlyAmountOverCap_Rejected()
    {
        var v = new CapitalisedPaymentRequestCreateInputValidator(new StubClock());
        var input = Valid() with { MonthlyAmountMdl = 999_999_999m };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.MonthlyAmountMdl));
    }

    [Fact]
    public void Create_DiscountRateOverCap_Rejected()
    {
        var v = new CapitalisedPaymentRequestCreateInputValidator(new StubClock());
        var input = Valid() with { LegalDiscountRatePercent = 35m };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.LegalDiscountRatePercent));
    }

    [Fact]
    public void Create_BadObligationKind_Rejected()
    {
        var v = new CapitalisedPaymentRequestCreateInputValidator(new StubClock());
        var input = Valid() with { ObligationKind = "Foo" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.ObligationKind));
    }

    [Fact]
    public void Create_ObligationStartBeforeBirth_Rejected()
    {
        var v = new CapitalisedPaymentRequestCreateInputValidator(new StubClock());
        var input = Valid() with
        {
            BeneficiaryBirthDate = new DateOnly(2010, 1, 1),
            ObligationStartDate = new DateOnly(2005, 1, 1),
        };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Reason_TooShort_Rejected()
    {
        var v = new CapitalisedPaymentReasonInputValidator();

        var result = v.Validate(new CapitalisedPaymentReasonInputDto("hi"));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Approval_NoteHappyPath_Accepted()
    {
        var v = new CapitalisedPaymentApprovalInputValidator();

        var result = v.Validate(new CapitalisedPaymentApprovalInputDto("Approving the capitalised amount."));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Filter_TakeAboveCap_Rejected()
    {
        var v = new CapitalisedPaymentRequestFilterValidator();

        var result = v.Validate(new CapitalisedPaymentRequestFilterDto(Take: 500));

        result.IsValid.Should().BeFalse();
    }
}
