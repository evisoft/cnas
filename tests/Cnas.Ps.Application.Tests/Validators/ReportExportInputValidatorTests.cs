using System.Collections.Generic;
using System.Linq;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0529 / TOR CF 03.14 — input-validation rules for
/// <see cref="ReportExportInputDtoValidator"/>. Each test exercises one
/// branch of the rule set: title length, column count, row count,
/// matrix-coherence, and the column-width range.
/// </summary>
public sealed class ReportExportInputValidatorTests
{
    /// <summary>Baseline columns shared across tests (CA1861 — lifted to static readonly).</summary>
    private static readonly ReportExportColumnDto[] BaselineColumns =
    [
        new("Timestamp"),
        new("Actor", Width: 0.3),
        new("Action"),
    ];

    /// <summary>Baseline rows shared across tests.</summary>
    private static readonly IReadOnlyList<string>[] BaselineRows =
    [
        ["2026-05-24T10:00:00Z", "alice", "LOGIN"],
        ["2026-05-24T10:01:00Z", "bob",   "LOGOUT"],
    ];

    /// <summary>Builds a baseline DTO that satisfies every rule.</summary>
    /// <returns>A valid DTO callers can <c>with</c>-clone in each test.</returns>
    private static ReportExportInputDto Valid() => new(
        ReportTitle: "Audit log — 2026-05",
        Columns: BaselineColumns,
        Rows: BaselineRows);

    /// <summary>The baseline DTO must pass validation.</summary>
    [Fact]
    public void Validate_Baseline_Succeeds()
    {
        var sut = new ReportExportInputDtoValidator();

        var result = sut.Validate(Valid());

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    /// <summary>Empty title is rejected with a property-level error.</summary>
    [Fact]
    public void Validate_RejectsEmptyTitle()
    {
        var sut = new ReportExportInputDtoValidator();
        var dto = Valid() with { ReportTitle = string.Empty };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportExportInputDto.ReportTitle));
    }

    /// <summary>Title longer than the cap (256) is rejected.</summary>
    [Fact]
    public void Validate_RejectsTitleOver256Chars()
    {
        var sut = new ReportExportInputDtoValidator();
        var dto = Valid() with { ReportTitle = new string('x', 257) };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportExportInputDto.ReportTitle));
    }

    /// <summary>An empty column list is rejected (at least one column is required).</summary>
    [Fact]
    public void Validate_RejectsEmptyColumns()
    {
        var sut = new ReportExportInputDtoValidator();
        // Empty columns + empty rows so the matrix-coherence rule is satisfied trivially
        // and the failure is attributable to the column-count rule.
        var dto = Valid() with
        {
            Columns = System.Array.Empty<ReportExportColumnDto>(),
            Rows = System.Array.Empty<IReadOnlyList<string>>(),
        };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportExportInputDto.Columns));
    }

    /// <summary>More than 100 columns is rejected (amplification guard).</summary>
    [Fact]
    public void Validate_RejectsTooManyColumns()
    {
        var sut = new ReportExportInputDtoValidator();
        var tooMany = Enumerable.Range(1, 101)
            .Select(i => new ReportExportColumnDto($"Col{i}"))
            .ToArray();
        var dto = Valid() with
        {
            Columns = tooMany,
            // Keep matrix coherent so we hit the column-count rule, not the coherence rule.
            Rows = new IReadOnlyList<string>[] { Enumerable.Repeat("v", 101).ToArray() },
        };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportExportInputDto.Columns));
    }

    /// <summary>Shared single-row instance reused for the 100_001-row DOS-cap test (CA1861).</summary>
    private static readonly IReadOnlyList<string> SharedThreeCellRow = ["a", "b", "c"];

    /// <summary>More than 100_000 rows is rejected (DOS protection).</summary>
    [Fact]
    public void Validate_RejectsTooManyRows()
    {
        var sut = new ReportExportInputDtoValidator();
        var rows = Enumerable.Range(0, 100_001)
            .Select(_ => SharedThreeCellRow)
            .ToArray();
        var dto = Valid() with { Rows = rows };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportExportInputDto.Rows));
    }

    /// <summary>Ragged-row fixture (only two cells; baseline columns have three).</summary>
    private static readonly IReadOnlyList<string>[] RaggedRows =
    [
        ["2026-05-24T10:00:00Z", "alice"],
    ];

    /// <summary>Out-of-range column fixture used in the width-rule test.</summary>
    private static readonly ReportExportColumnDto[] OutOfRangeWidthColumns =
    [
        new("Timestamp"),
        new("Actor", Width: 1.5),
        new("Action"),
    ];

    /// <summary>A row whose cell count does not match the column count is rejected.</summary>
    [Fact]
    public void Validate_RejectsRaggedMatrix()
    {
        var sut = new ReportExportInputDtoValidator();
        var dto = Valid() with
        {
            // baseline has 3 columns; this row only has 2 cells.
            Rows = RaggedRows,
        };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>Width must lie in (0.0, 1.0); 1.5 is out of range.</summary>
    [Fact]
    public void Validate_RejectsOutOfRangeWidth()
    {
        var sut = new ReportExportInputDtoValidator();
        var dto = Valid() with
        {
            Columns = OutOfRangeWidthColumns,
        };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
    }
}
