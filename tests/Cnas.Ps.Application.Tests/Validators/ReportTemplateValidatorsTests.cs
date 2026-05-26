using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — unit tests for
/// <see cref="ReportTemplateCreateDtoValidator"/>.
/// </summary>
public sealed class ReportTemplateValidatorsTests
{
    private readonly ReportTemplateCreateDtoValidator _validator = new();

    /// <summary>Reused defaults — avoids inline new[] (CA1861).</summary>
    private static readonly string[] DefaultSelected = ["Id", "DisplayName"];

    /// <summary>Single ASC ordering used by happy-path tests.</summary>
    private static readonly ReportOrderingDto[] DefaultOrdering =
        [new ReportOrderingDto("DisplayName", ReportOrderingDto.Asc)];

    private static ReportTemplateCreateDto BuildValid(
        IReadOnlyList<string>? selectedFields = null,
        string? groupBy = null) =>
        new(
            Code: "report.solicitants.valid",
            Name: "Valid",
            Description: null,
            Registry: QueryBudgetRegistries.Solicitant,
            SelectedFields: selectedFields ?? DefaultSelected,
            Filter: new QbeFilterDto(QbeFilter.CombinatorAnd, Array.Empty<QbeConditionDto>()),
            Ordering: DefaultOrdering,
            GroupByField: groupBy,
            IsShared: false);

    [Fact]
    public void Defaults_AreValid()
    {
        _validator.TestValidate(BuildValid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void SelectedFields_OverCap_Fails()
    {
        // 26 entries — exceeds the 25 cap.
        var oversize = Enumerable.Range(0, 26).Select(i => $"F{i}").ToList();
        var dto = BuildValid(selectedFields: oversize);

        var result = _validator.TestValidate(dto);

        result.ShouldHaveValidationErrorFor(x => x.SelectedFields);
    }

    [Fact]
    public void GroupByField_NotInSelectedFields_Fails()
    {
        // Kind isn't in the selected list — validator must reject.
        var dto = BuildValid(selectedFields: DefaultSelected, groupBy: "Kind");

        var result = _validator.TestValidate(dto);

        result.ShouldHaveValidationErrorFor(x => x.GroupByField);
    }

    [Fact]
    public void Code_WithInvalidShape_Fails()
    {
        var dto = BuildValid() with { Code = "BADCODE" }; // uppercase — fails regex
        var result = _validator.TestValidate(dto);
        result.ShouldHaveValidationErrorFor(x => x.Code);
    }
}
