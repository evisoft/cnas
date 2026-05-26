using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1600 / R1406 — unit tests for the executory-document input validators.
/// Covers IDNP, IBAN, amount, percentage, date-order, and priority-rank
/// invariants.
/// </summary>
public sealed class ExecutoryDocumentInputValidatorTests
{
    /// <summary>Fixed UTC clock used by the register validator.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stub clock returning the fixed instant.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow => ClockNow;
    }

    /// <summary>Builds a canonical register-input DTO that should pass validation.</summary>
    private static ExecutoryDocumentRegisterInputDto Valid() => new(
        DocumentSeriesNumber: "OE-2026-1234",
        DebtorIdnp: "2002000000007",
        Kind: nameof(ExecutoryDocumentKind.CourtOrder),
        IssuedBy: "Judecătoria Chișinău",
        IssuedDate: new DateOnly(2026, 5, 1),
        EffectiveFrom: new DateOnly(2026, 5, 15),
        EffectiveUntil: new DateOnly(2027, 5, 15),
        WithholdingMode: nameof(ExecutoryDocumentWithholdingMode.FixedAmount),
        WithholdingAmountMdl: 1_000m,
        WithholdingPercentage: null,
        PriorityRank: 1,
        CreditorAccountIban: "MD24AG000225100013104168",
        CreditorName: "Direcția Asistență Socială",
        TotalOwedMdl: 50_000m);

    // ───────── Register validator — IDNP rules ─────────

    /// <summary>Bad IDNP (too short) → rejected with IDNP-targeted error.</summary>
    [Fact]
    public void Register_BadIdnpTooShort_Rejected()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());
        var input = Valid() with { DebtorIdnp = "12345" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.DebtorIdnp));
    }

    /// <summary>Valid 13-digit IDNP → accepted.</summary>
    [Fact]
    public void Register_GoodIdnp_Accepted()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());

        var result = v.Validate(Valid());

        result.IsValid.Should().BeTrue();
    }

    // ───────── Register validator — IBAN rules ─────────

    /// <summary>Bad IBAN (lowercase) → rejected with IBAN-targeted error.</summary>
    [Fact]
    public void Register_BadIbanLowercase_Rejected()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());
        var input = Valid() with { CreditorAccountIban = "md24ag000225100013104168" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.CreditorAccountIban));
    }

    /// <summary>Bad IBAN (wrong country code) → rejected.</summary>
    [Fact]
    public void Register_BadIbanWrongCountry_Rejected()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());
        var input = Valid() with { CreditorAccountIban = "RO24AG000225100013104168" };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.CreditorAccountIban));
    }

    // ───────── Register validator — amount / percentage rules ─────────

    /// <summary>Amount over 100M → rejected.</summary>
    [Fact]
    public void Register_AmountOverCap_Rejected()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());
        var input = Valid() with { WithholdingAmountMdl = 200_000_000m };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith(
            nameof(input.WithholdingAmountMdl), StringComparison.Ordinal));
    }

    /// <summary>Percentage over 70% → rejected.</summary>
    [Fact]
    public void Register_PercentageOver70_Rejected()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());
        var input = Valid() with
        {
            WithholdingMode = nameof(ExecutoryDocumentWithholdingMode.Percentage),
            WithholdingAmountMdl = null,
            WithholdingPercentage = 80m,
        };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith(
            nameof(input.WithholdingPercentage), StringComparison.Ordinal));
    }

    // ───────── Register validator — date / priority-rank rules ─────────

    /// <summary>EffectiveFrom before IssuedDate → rejected.</summary>
    [Fact]
    public void Register_EffectiveFromBeforeIssuedDate_Rejected()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());
        var input = Valid() with
        {
            IssuedDate = new DateOnly(2026, 5, 10),
            EffectiveFrom = new DateOnly(2026, 5, 1),
        };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>PriorityRank = 0 → rejected.</summary>
    [Fact]
    public void Register_PriorityRankZero_Rejected()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());
        var input = Valid() with { PriorityRank = 0 };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.PriorityRank));
    }

    /// <summary>PriorityRank = 6 → rejected.</summary>
    [Fact]
    public void Register_PriorityRankSix_Rejected()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());
        var input = Valid() with { PriorityRank = 6 };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.PriorityRank));
    }

    /// <summary>IssuedDate in the future → rejected.</summary>
    [Fact]
    public void Register_IssuedDateInFuture_Rejected()
    {
        var v = new ExecutoryDocumentRegisterInputValidator(new StubClock());
        var input = Valid() with { IssuedDate = new DateOnly(2027, 1, 1) };

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.IssuedDate));
    }

    // ───────── Modify validator ─────────

    /// <summary>Missing ChangeReason → rejected.</summary>
    [Fact]
    public void Modify_MissingChangeReason_Rejected()
    {
        var v = new ExecutoryDocumentModifyInputValidator();
        var input = new ExecutoryDocumentModifyInputDto(
            IssuedBy: null,
            EffectiveUntil: null,
            WithholdingMode: null,
            WithholdingAmountMdl: null,
            WithholdingPercentage: null,
            PriorityRank: null,
            CreditorAccountIban: null,
            CreditorName: null,
            TotalOwedMdl: null,
            ChangeReason: string.Empty);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.ChangeReason));
    }

    /// <summary>Valid ChangeReason + nullable fields → accepted.</summary>
    [Fact]
    public void Modify_ValidChangeReason_Accepted()
    {
        var v = new ExecutoryDocumentModifyInputValidator();
        var input = new ExecutoryDocumentModifyInputDto(
            IssuedBy: null,
            EffectiveUntil: null,
            WithholdingMode: null,
            WithholdingAmountMdl: null,
            WithholdingPercentage: null,
            PriorityRank: null,
            CreditorAccountIban: null,
            CreditorName: null,
            TotalOwedMdl: null,
            ChangeReason: "Court re-evaluated debt amount");

        var result = v.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    // ───────── Reason validator ─────────

    /// <summary>Empty Reason → rejected.</summary>
    [Fact]
    public void Reason_Empty_Rejected()
    {
        var v = new ExecutoryDocumentReasonInputValidator();

        var result = v.Validate(new ExecutoryDocumentReasonInputDto(string.Empty));

        result.IsValid.Should().BeFalse();
    }

    /// <summary>Reason between 3..500 chars → accepted.</summary>
    [Fact]
    public void Reason_Valid_Accepted()
    {
        var v = new ExecutoryDocumentReasonInputValidator();

        var result = v.Validate(new ExecutoryDocumentReasonInputDto("Debtor filed appeal"));

        result.IsValid.Should().BeTrue();
    }
}
