using Cnas.Ps.Application.ExecutoryDocuments;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Observability;

namespace Cnas.Ps.Infrastructure.Services.ExecutoryDocuments;

/// <summary>
/// R1406 / TOR §3.6-G — wire between the unemployment-benefit (indemnizație
/// șomaj) payment pipeline and the executory-documents registry. Computes
/// the per-payment withholding plan via
/// <see cref="IExecutoryDocumentWithholdingCalculator"/> and, post-payment,
/// commits each non-zero allocation via
/// <see cref="IExecutoryDocumentService.RecordWithholdingAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-phase contract.</b> The upstream service MUST call
/// <see cref="ComputePlanAsync"/> BEFORE disbursement (to learn how much to
/// withhold and therefore the NET payable amount) and
/// <see cref="CommitPlanAsync"/> AFTER the payment has been committed (to
/// update the per-row TotalWithheldMdl tallies). Splitting the two prevents
/// double-counting when a payment fails and is reissued.
/// </para>
/// <para>
/// <b>Per-row metric.</b> Each non-zero allocation row commits via the
/// registry service (which emits its own Information audit row) and the
/// applier increments
/// <see cref="CnasMeter.ExecutoryDocumentWithholdingApplied"/> tagged with
/// <c>priority_rank</c>.
/// </para>
/// </remarks>
public sealed class UnemploymentBenefitWithholdingApplier : IUnemploymentBenefitWithholdingApplier
{
    private readonly IExecutoryDocumentWithholdingCalculator _calculator;
    private readonly IExecutoryDocumentService _service;

    /// <summary>Constructs the applier with its collaborators.</summary>
    /// <param name="calculator">Pure-calculation service that produces the per-payment plan.</param>
    /// <param name="service">Registry service that commits the per-row TotalWithheldMdl updates.</param>
    public UnemploymentBenefitWithholdingApplier(
        IExecutoryDocumentWithholdingCalculator calculator,
        IExecutoryDocumentService service)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        ArgumentNullException.ThrowIfNull(service);
        _calculator = calculator;
        _service = service;
    }

    /// <inheritdoc />
    public Task<Result<ExecutoryDocumentWithholdingPlanDto>> ComputePlanAsync(
        string debtorIdnp,
        decimal grossBenefitMdl,
        decimal legalMinimumMdl,
        DateOnly benefitPeriod,
        CancellationToken ct = default) =>
        _calculator.CalculateWithholdingsAsync(
            debtorIdnp,
            grossBenefitMdl,
            legalMinimumMdl,
            benefitPeriod,
            ct);

    /// <inheritdoc />
    public async Task<Result> CommitPlanAsync(
        ExecutoryDocumentWithholdingPlanDto plan,
        string sourceReference,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(sourceReference))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "sourceReference is required.");
        }

        foreach (var row in plan.Rows)
        {
            if (row.AllocatedMdl <= 0m)
            {
                continue;
            }

            var commit = await _service.RecordWithholdingAsync(
                row.DocumentSqid,
                row.AllocatedMdl,
                sourceReference,
                ct).ConfigureAwait(false);
            if (commit.IsFailure)
            {
                return Result.Failure(commit.ErrorCode!, commit.ErrorMessage!);
            }

            CnasMeter.ExecutoryDocumentWithholdingApplied.Add(
                1,
                new KeyValuePair<string, object?>("priority_rank", row.PriorityRank));
        }

        return Result.Success();
    }
}
