namespace Cnas.Ps.Contracts;

/// <summary>
/// R0932 / TOR §10.1 — single editable row inside the Fișa de calcul recalc UI.
/// Represents one calculation period contributing a partial monetary sum.
/// </summary>
/// <param name="Period">Free-text period label (e.g. "2024-01", "Q1-2025").</param>
/// <param name="AmountMdl">Computed sum for this row (MDL).</param>
public sealed record FisaDeCalculRowDto(string Period, decimal AmountMdl);

/// <summary>
/// R0932 / TOR §10.1 — recalc input envelope. Carries the operator-edited
/// row set; the server re-runs the formula evaluator (a sum-of-rows for the
/// MVP shape) and returns a refreshed total.
/// </summary>
/// <param name="DossierSqid">Sqid-encoded dossier id whose Fișa is being edited.</param>
/// <param name="Rows">Operator-edited row set.</param>
public sealed record FisaDeCalculRecalcInputDto(
    string DossierSqid,
    IReadOnlyList<FisaDeCalculRowDto> Rows);

/// <summary>
/// R0932 / TOR §10.1 — recalc output envelope. Carries the refreshed total
/// computed by <c>IFisaDeCalculRecalculator</c>.
/// </summary>
/// <param name="DossierSqid">Sqid-encoded dossier id.</param>
/// <param name="TotalAmountMdl">Refreshed total = sum(rows).</param>
/// <param name="Rows">Echoed row set (allows the UI to keep its display state in sync).</param>
public sealed record FisaDeCalculRecalcResultDto(
    string DossierSqid,
    decimal TotalAmountMdl,
    IReadOnlyList<FisaDeCalculRowDto> Rows);
