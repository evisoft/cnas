using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0910 / R0913 — validator-level tests for the REV-5 + insured-person-
/// adjustment input DTOs.
/// </summary>
public sealed class Rev5InputValidatorsTests
{
    /// <summary>R0910 — empty Rows array is rejected.</summary>
    [Fact]
    public async Task Rev5DeclarationRegister_EmptyRows_Fails()
    {
        var v = new Rev5DeclarationRegisterInputDtoValidator();
        var input = new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: "SQID-1",
            ReportingMonth: new DateOnly(2026, 4, 1),
            ReferenceNumber: "REV5-001",
            Rows: Array.Empty<Rev5DeclarationRowInputDto>());

        var result = await v.ValidateAsync(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.Rows));
    }

    /// <summary>R0910 — more than 50 000 rows is rejected.</summary>
    [Fact]
    public async Task Rev5DeclarationRegister_TooManyRows_Fails()
    {
        var v = new Rev5DeclarationRegisterInputDtoValidator();
        var rows = new Rev5DeclarationRowInputDto[50_001];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new Rev5DeclarationRowInputDto("HASH" + i, 1m, 1m);
        }
        var input = new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: "SQID-1",
            ReportingMonth: new DateOnly(2026, 4, 1),
            ReferenceNumber: "REV5-001",
            Rows: rows);

        var result = await v.ValidateAsync(input);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>R0910 — ReportingMonth with day != 1 is rejected.</summary>
    [Fact]
    public async Task Rev5DeclarationRegister_NonFirstOfMonth_Fails()
    {
        var v = new Rev5DeclarationRegisterInputDtoValidator();
        var input = new Rev5DeclarationRegisterInputDto(
            FilingContributorSqid: "SQID-1",
            ReportingMonth: new DateOnly(2026, 4, 15),
            ReferenceNumber: "REV5-001",
            Rows: [new Rev5DeclarationRowInputDto("HASH", 1m, 1m)]);

        var result = await v.ValidateAsync(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("first day", StringComparison.Ordinal));
    }

    /// <summary>R0913 — AdjustmentAmount above the magnitude cap is rejected.</summary>
    [Fact]
    public async Task InsuredPersonAdjustment_AmountAboveCap_Fails()
    {
        var v = new InsuredPersonContributionAdjustmentInputDtoValidator();
        var input = new InsuredPersonContributionAdjustmentInputDto(
            InsuredPersonSolicitantSqid: "SQID-1",
            Month: new DateOnly(2026, 4, 1),
            AdjustmentAmount: 10_000_001m,
            SourceDocumentCode: "CourtDecision");

        var result = await v.ValidateAsync(input);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>R0913 — unknown SourceDocumentCode is rejected.</summary>
    [Fact]
    public async Task InsuredPersonAdjustment_UnknownSourceCode_Fails()
    {
        var v = new InsuredPersonContributionAdjustmentInputDtoValidator();
        var input = new InsuredPersonContributionAdjustmentInputDto(
            InsuredPersonSolicitantSqid: "SQID-1",
            Month: new DateOnly(2026, 4, 1),
            AdjustmentAmount: 100m,
            SourceDocumentCode: "Bogus");

        var result = await v.ValidateAsync(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(input.SourceDocumentCode));
    }
}
