using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0932 / TOR §10.1 — concrete recalculator for the Fișa de calcul editor.
/// MVP implementation: re-evaluates the total as the sum of every supplied
/// row. The formula DSL hand-off lives in the iter-100 decision engine; this
/// service is a thin Application-layer port so the Blazor recalc UI can do
/// an interactive preview without traversing the full decision pipeline.
/// </summary>
public sealed class FisaDeCalculRecalculator : IFisaDeCalculRecalculator
{
    /// <inheritdoc />
    public Task<Result<FisaDeCalculRecalcResultDto>> RecalculateAsync(
        FisaDeCalculRecalcInputDto input,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            return Task.FromResult(Result<FisaDeCalculRecalcResultDto>.Failure(
                ErrorCodes.ValidationFailed, "Input is required."));
        }

        if (string.IsNullOrWhiteSpace(input.DossierSqid))
        {
            return Task.FromResult(Result<FisaDeCalculRecalcResultDto>.Failure(
                ErrorCodes.ValidationFailed, "DossierSqid is required."));
        }

        if (input.Rows is null || input.Rows.Count == 0)
        {
            return Task.FromResult(Result<FisaDeCalculRecalcResultDto>.Failure(
                ErrorCodes.ValidationFailed, "At least one row is required."));
        }

        decimal total = 0m;
        foreach (var row in input.Rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Period))
            {
                return Task.FromResult(Result<FisaDeCalculRecalcResultDto>.Failure(
                    ErrorCodes.ValidationFailed, "Each row must carry a non-empty period."));
            }
            if (row.AmountMdl < 0m)
            {
                return Task.FromResult(Result<FisaDeCalculRecalcResultDto>.Failure(
                    ErrorCodes.ValidationFailed, "Row amounts must be non-negative."));
            }
            total += row.AmountMdl;
        }

        var result = new FisaDeCalculRecalcResultDto(
            DossierSqid: input.DossierSqid,
            TotalAmountMdl: total,
            Rows: input.Rows);
        return Task.FromResult(Result<FisaDeCalculRecalcResultDto>.Success(result));
    }
}
