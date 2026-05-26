using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0529 / TOR CF 03.14 — FluentValidation rules for
/// <see cref="ReportExportInputDto"/>. Defends the universal report-export
/// pipeline at the API boundary against malformed shapes BEFORE any
/// exporter touches the matrix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Row cap (DOS protection).</b> The 100_000-row ceiling is the single
/// source of truth across the four exporters — anything larger should run
/// as a background job per R0252 / TOR PSR 010. Each exporter trusts the
/// validator to have already enforced the cap.
/// </para>
/// <para>
/// <b>Column-row coherence.</b> Every row's cell-count MUST equal
/// <c>Columns.Count</c>. Ragged matrices silently truncate or duplicate
/// data across rendering libraries (ClosedXML pads, QuestPDF skips,
/// OpenXML throws); rejecting them at the boundary eliminates the
/// renderer-specific surprise.
/// </para>
/// </remarks>
public sealed class ReportExportInputDtoValidator : AbstractValidator<ReportExportInputDto>
{
    /// <summary>Minimum allowed length of <see cref="ReportExportInputDto.ReportTitle"/>.</summary>
    public const int MinTitleLength = 1;

    /// <summary>Maximum allowed length of <see cref="ReportExportInputDto.ReportTitle"/>.</summary>
    public const int MaxTitleLength = 256;

    /// <summary>Minimum allowed number of columns.</summary>
    public const int MinColumnCount = 1;

    /// <summary>Maximum allowed number of columns (defence against amplification).</summary>
    public const int MaxColumnCount = 100;

    /// <summary>Maximum allowed number of rows (DOS protection — TOR PSR 010 background-job threshold).</summary>
    public const int MaxRowCount = 100_000;

    /// <summary>Builds the validator with the full rule set.</summary>
    public ReportExportInputDtoValidator()
    {
        RuleFor(x => x.ReportTitle)
            .NotNull().WithMessage("ReportTitle is required.")
            .Must(t => t is not null && t.Length >= MinTitleLength)
            .WithMessage($"ReportTitle must be at least {MinTitleLength} character(s) long.")
            .Must(t => t is null || t.Length <= MaxTitleLength)
            .WithMessage($"ReportTitle must not exceed {MaxTitleLength} characters.");

        RuleFor(x => x.Columns)
            .NotNull().WithMessage("Columns are required.")
            .Must(c => c is not null && c.Count >= MinColumnCount)
            .WithMessage($"At least {MinColumnCount} column is required.")
            .Must(c => c is null || c.Count <= MaxColumnCount)
            .WithMessage($"Column count must not exceed {MaxColumnCount}.");

        RuleForEach(x => x.Columns).ChildRules(col =>
        {
            col.RuleFor(c => c.Header)
                .NotEmpty().WithMessage("Column Header is required.")
                .Must(h => h is null || h.Length <= MaxTitleLength)
                .WithMessage($"Column Header must not exceed {MaxTitleLength} characters.");
            col.RuleFor(c => c.Width)
                .Must(w => !w.HasValue || (w.Value > 0.0 && w.Value < 1.0))
                .WithMessage("Column Width, when supplied, must lie in (0.0, 1.0).");
        });

        RuleFor(x => x.Rows)
            .NotNull().WithMessage("Rows are required.")
            .Must(r => r is null || r.Count <= MaxRowCount)
            .WithMessage($"Row count must not exceed {MaxRowCount}.");

        // Coherence — every row's cell count must match the column count.
        // Uses a dto-level rule so we can reach both Columns and Rows.
        RuleFor(x => x).Must(HaveCoherentMatrix!)
            .WithMessage("Each row must carry exactly one cell per column.")
            .When(x => x.Columns is not null && x.Rows is not null);
    }

    /// <summary>
    /// Verifies that every row carries exactly <c>Columns.Count</c> cells.
    /// Null rows are rejected; null cells inside a row are permitted (a
    /// renderer projects them to an empty string).
    /// </summary>
    /// <param name="input">DTO under validation.</param>
    /// <returns><c>true</c> when the matrix is rectangular; <c>false</c> otherwise.</returns>
    private static bool HaveCoherentMatrix(ReportExportInputDto input)
    {
        var expected = input.Columns.Count;
        foreach (var row in input.Rows)
        {
            if (row is null || row.Count != expected)
            {
                return false;
            }
        }
        return true;
    }
}
