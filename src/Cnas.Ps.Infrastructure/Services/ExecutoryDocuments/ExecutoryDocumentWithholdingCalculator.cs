using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ExecutoryDocuments;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.ExecutoryDocuments;

/// <summary>
/// R1406 / TOR §3.6-G — concrete implementation of
/// <see cref="IExecutoryDocumentWithholdingCalculator"/>. Pulls the relevant
/// Active executory documents, computes per-row withholding using each
/// document's <c>WithholdingMode</c>, applies the 70% cap (art. 156 CMP),
/// and returns the resulting plan as a calculation DTO.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cap allocation algorithm.</b> Documents are processed in PriorityRank
/// ASC order. The cap (70% of gross benefit) defines the maximum total of
/// AllocatedMdl across all rows. For each document the calculator computes
/// the requested amount, then allocates <c>min(requested, residualCap)</c>
/// where <c>residualCap = cap - alreadyAllocated</c>. Rows whose residual is
/// zero are recorded with <c>AllocatedMdl = 0</c> and rationale
/// <c>CAP_EXCEEDED</c>; rows whose residual is positive but smaller than the
/// request get <c>PARTIAL_ALLOCATION</c>; full allocations get
/// <c>FULL_ALLOCATION</c>.
/// </para>
/// <para>
/// <b>Banker's rounding.</b> Per-row amounts are rounded to 2 decimals using
/// <see cref="MidpointRounding.ToEven"/> which is the recommended rule for
/// financial computation: half-values round to the nearest even integer so
/// repeated computations do not accumulate a directional bias.
/// </para>
/// </remarks>
public sealed class ExecutoryDocumentWithholdingCalculator : IExecutoryDocumentWithholdingCalculator
{
    /// <summary>Rationale code emitted when the row's full requested amount fits within the residual cap.</summary>
    public const string RationaleFull = "FULL_ALLOCATION";

    /// <summary>Rationale code emitted when the row's allocation was clipped by the residual cap.</summary>
    public const string RationalePartial = "PARTIAL_ALLOCATION";

    /// <summary>Rationale code emitted when the cap was already exhausted and the row received zero.</summary>
    public const string RationaleCapExceeded = "CAP_EXCEEDED";

    /// <summary>Statutory cap (per art. 156 CMP) — the cumulative allocation may not exceed this fraction of the gross benefit.</summary>
    public const decimal StatutoryCapFraction = 0.70m;

    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly IDeterministicHasher _hasher;

    /// <summary>Constructs the calculator with its read-side collaborators.</summary>
    /// <param name="db">Per-request DbContext (only used as a read source; no writes).</param>
    /// <param name="sqids">Sqid encoder used to project the per-row document id.</param>
    /// <param name="hasher">Deterministic hasher used to compute the IDNP lookup key.</param>
    public ExecutoryDocumentWithholdingCalculator(
        ICnasDbContext db,
        ISqidService sqids,
        IDeterministicHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(hasher);
        _db = db;
        _sqids = sqids;
        _hasher = hasher;
    }

    /// <inheritdoc />
    public async Task<Result<ExecutoryDocumentWithholdingPlanDto>> CalculateWithholdingsAsync(
        string debtorIdnp,
        decimal grossBenefitMdl,
        decimal legalMinimumMdl,
        DateOnly benefitPeriod,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(debtorIdnp))
        {
            return Result<ExecutoryDocumentWithholdingPlanDto>.Failure(
                ErrorCodes.ValidationFailed, "debtorIdnp is required.");
        }
        if (grossBenefitMdl < 0m)
        {
            return Result<ExecutoryDocumentWithholdingPlanDto>.Failure(
                ErrorCodes.ValidationFailed, "grossBenefitMdl must be >= 0.");
        }
        if (legalMinimumMdl < 0m)
        {
            return Result<ExecutoryDocumentWithholdingPlanDto>.Failure(
                ErrorCodes.ValidationFailed, "legalMinimumMdl must be >= 0.");
        }

        var canonicalIdnp = debtorIdnp.Trim().ToUpperInvariant();
        var hash = _hasher.ComputeHash(canonicalIdnp);

        var docs = await _db.ExecutoryDocuments
            .Where(d => d.IsActive
                && d.Status == ExecutoryDocumentStatus.Active
                && d.DebtorIdnpHash == hash
                && d.EffectiveFrom <= benefitPeriod
                && (d.EffectiveUntil == null || d.EffectiveUntil >= benefitPeriod))
            .OrderBy(d => d.PriorityRank)
            .ThenBy(d => d.IssuedDate)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var cap = Round(grossBenefitMdl * StatutoryCapFraction);
        var allocatedSoFar = 0m;
        var rows = new List<ExecutoryDocumentWithholdingPlanRowDto>(docs.Count);
        var capHit = false;

        foreach (var doc in docs)
        {
            var requested = ComputeRequested(doc, grossBenefitMdl, legalMinimumMdl);
            // Respect the document's TotalOwedMdl outstanding-balance ceiling
            // when present — never withhold more than the residual debt.
            if (doc.TotalOwedMdl.HasValue)
            {
                var remaining = doc.TotalOwedMdl.Value - doc.TotalWithheldMdl;
                if (remaining < 0m)
                {
                    remaining = 0m;
                }
                if (requested > remaining)
                {
                    requested = remaining;
                }
            }
            requested = Round(requested);

            var residualCap = cap - allocatedSoFar;
            decimal allocated;
            string rationale;
            if (residualCap <= 0m)
            {
                allocated = 0m;
                rationale = RationaleCapExceeded;
                capHit = true;
            }
            else if (requested <= residualCap)
            {
                allocated = requested;
                rationale = RationaleFull;
            }
            else
            {
                allocated = residualCap;
                rationale = RationalePartial;
                capHit = true;
            }

            allocatedSoFar += allocated;

            rows.Add(new ExecutoryDocumentWithholdingPlanRowDto(
                DocumentSqid: _sqids.Encode(doc.Id),
                DocumentSeriesNumber: doc.DocumentSeriesNumber,
                PriorityRank: doc.PriorityRank,
                RequestedMdl: requested,
                AllocatedMdl: Round(allocated),
                Rationale: rationale,
                CreditorAccountIbanLast4: LastFour(doc.CreditorAccountIban)));
        }

        if (capHit)
        {
            CnasMeter.ExecutoryDocumentCapExceeded.Add(1);
        }

        var totalWithheld = Round(allocatedSoFar);
        var net = Round(grossBenefitMdl - totalWithheld);
        var plan = new ExecutoryDocumentWithholdingPlanDto(
            DebtorIdnp: canonicalIdnp,
            GrossBenefitMdl: Round(grossBenefitMdl),
            LegalMinimumMdl: Round(legalMinimumMdl),
            BenefitPeriod: benefitPeriod,
            TotalWithheldMdl: totalWithheld,
            NetPayableMdl: net,
            CapHit: capHit,
            Rows: rows);
        return Result<ExecutoryDocumentWithholdingPlanDto>.Success(plan);
    }

    /// <summary>Computes the per-document REQUESTED withholding amount (before cap).</summary>
    /// <param name="doc">Active executory document.</param>
    /// <param name="grossBenefitMdl">Gross benefit (MDL).</param>
    /// <param name="legalMinimumMdl">Legal minimum-subsistence floor (MDL).</param>
    /// <returns>Requested amount in MDL (non-negative).</returns>
    private static decimal ComputeRequested(
        ExecutoryDocument doc,
        decimal grossBenefitMdl,
        decimal legalMinimumMdl) => doc.WithholdingMode switch
        {
            ExecutoryDocumentWithholdingMode.FixedAmount => doc.WithholdingAmountMdl ?? 0m,
            ExecutoryDocumentWithholdingMode.Percentage =>
                grossBenefitMdl * (doc.WithholdingPercentage ?? 0m) / 100m,
            ExecutoryDocumentWithholdingMode.FullExcessOverMinimum =>
                Math.Max(0m, grossBenefitMdl - legalMinimumMdl),
            _ => 0m,
        };

    /// <summary>Rounds to 2 decimals using banker's rounding (<see cref="MidpointRounding.ToEven"/>).</summary>
    /// <param name="value">Amount to round.</param>
    /// <returns>Rounded amount.</returns>
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.ToEven);

    /// <summary>Returns the last 4 characters of <paramref name="value"/> (or fewer when the string is shorter).</summary>
    /// <param name="value">String to mask.</param>
    /// <returns>The masked tail; never null.</returns>
    private static string LastFour(string value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= 4 ? value : value[^4..];
}
