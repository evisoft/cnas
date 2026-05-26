using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Cnas.Ps.Application.AthletePensions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.AthletePensions;

/// <summary>
/// R1403 / TOR §3.6-D — production implementation of
/// <see cref="IAthletePensionAmountCalculator"/>. Maps verified career
/// records onto the regulatory multiplier table and returns the monthly MDL
/// amount plus the snapshotted multiplier components.
/// </summary>
/// <remarks>
/// <para>
/// <b>PLACEHOLDER multiplier table.</b> The percentages below (Olympic gold
/// → 250%, Olympic silver → 220%, ..., world record additive 10%, European
/// record additive 5%, coach factor 0.80) are PLACEHOLDER values pending
/// the regulatory load. The contract surface — the input/output DTOs and
/// the algorithmic shape (highest-tier medal + record additives + role
/// factor + extras) — is stable; only the constants will move.
/// </para>
/// <para>
/// <b>No PII.</b> The breakdown JSON returned alongside the amount embeds
/// only achievement-kind codes + contribution percents + the final
/// composition step. It NEVER carries IDNP, display name, or any
/// beneficiary-identifying value.
/// </para>
/// </remarks>
public sealed class AthletePensionAmountCalculator : IAthletePensionAmountCalculator
{
    /// <summary>PLACEHOLDER — coach role factor applied to the base multiplier.</summary>
    private const decimal CoachFactor = 0.80m;

    /// <summary>PLACEHOLDER — world-record additive to the base multiplier (percent points).</summary>
    private const decimal WorldRecordAdditivePercent = 10m;

    /// <summary>PLACEHOLDER — European-record additive to the base multiplier (percent points).</summary>
    private const decimal EuropeanRecordAdditivePercent = 5m;

    /// <summary>
    /// PLACEHOLDER — medal-tier multiplier table. Maps each medal kind to
    /// the corresponding percentage of the regulatory base amount.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, decimal> MedalTierPercent = new Dictionary<string, decimal>
    {
        [nameof(AthleteAchievementKind.OlympicGold)] = 250m,
        [nameof(AthleteAchievementKind.OlympicSilver)] = 220m,
        [nameof(AthleteAchievementKind.OlympicBronze)] = 200m,
        [nameof(AthleteAchievementKind.WorldChampionGold)] = 180m,
        [nameof(AthleteAchievementKind.WorldChampionSilver)] = 165m,
        [nameof(AthleteAchievementKind.WorldChampionBronze)] = 150m,
        [nameof(AthleteAchievementKind.EuropeanChampionGold)] = 140m,
        [nameof(AthleteAchievementKind.EuropeanChampionSilver)] = 130m,
        [nameof(AthleteAchievementKind.EuropeanChampionBronze)] = 120m,
    };

    /// <summary>Cached JSON serializer options shared across breakdown-payload builders.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public Result<AthletePensionAmountComputationDto> Compute(AthletePensionAmountInputDto input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!Enum.TryParse<AthletePensionRole>(input.Role, ignoreCase: false, out var role))
        {
            return Result<AthletePensionAmountComputationDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Role must be a known AthletePensionRole enum name.");
        }
        if (input.RegulatoryBaseMdl <= 0m)
        {
            return Result<AthletePensionAmountComputationDto>.Failure(
                ErrorCodes.ValidationFailed,
                "RegulatoryBaseMdl must be > 0.");
        }

        // 1. Determine the highest medal-tier multiplier across verified records.
        var tieredContributions = input.VerifiedRecords
            .Where(r => MedalTierPercent.ContainsKey(r.AchievementKind))
            .Select(r => new { r.AchievementKind, r.AchievementYear, Percent = MedalTierPercent[r.AchievementKind] })
            .ToList();

        var basePercent = tieredContributions.Count > 0
            ? tieredContributions.Max(c => c.Percent)
            : 0m;
        var highestTierKind = tieredContributions
            .OrderByDescending(c => c.Percent)
            .ThenBy(c => c.AchievementYear)
            .Select(c => c.AchievementKind)
            .FirstOrDefault();

        // 2. Apply record additives.
        var hasWorldRecord = input.VerifiedRecords.Any(r =>
            r.AchievementKind == nameof(AthleteAchievementKind.WorldRecord));
        var hasEuropeanRecord = input.VerifiedRecords.Any(r =>
            r.AchievementKind == nameof(AthleteAchievementKind.EuropeanRecord));
        var basePlusAdditives = basePercent
            + (hasWorldRecord ? WorldRecordAdditivePercent : 0m)
            + (hasEuropeanRecord ? EuropeanRecordAdditivePercent : 0m);

        // 3. Apply coach factor if applicable.
        var afterRoleFactor = role == AthletePensionRole.Coach
            ? basePlusAdditives * CoachFactor
            : basePlusAdditives;

        // 4. Apply additional multipliers multiplicatively (each is a raw multiplier, e.g. 1.10).
        var finalPercent = afterRoleFactor;
        if (input.AdditionalMultipliers is not null)
        {
            foreach (var m in input.AdditionalMultipliers)
            {
                finalPercent *= m;
            }
        }

        // 5. Compute final monthly amount = base × (finalPercent / 100), banker's rounding to 2dp.
        var monthlyAmount = decimal.Round(
            input.RegulatoryBaseMdl * (finalPercent / 100m),
            2,
            MidpointRounding.ToEven);

        var breakdown = JsonSerializer.Serialize(new
        {
            role = input.Role,
            basePercent,
            highestTierKind,
            additives = new
            {
                worldRecord = hasWorldRecord ? WorldRecordAdditivePercent : 0m,
                europeanRecord = hasEuropeanRecord ? EuropeanRecordAdditivePercent : 0m,
            },
            roleFactor = role == AthletePensionRole.Coach ? CoachFactor : 1m,
            additionalMultipliers = input.AdditionalMultipliers ?? (IReadOnlyList<decimal>)Array.Empty<decimal>(),
            finalPercent,
            regulatoryBaseMdl = input.RegulatoryBaseMdl,
            monthlyAmountMdl = monthlyAmount,
        }, CachedJsonOptions);

        return Result<AthletePensionAmountComputationDto>.Success(new AthletePensionAmountComputationDto(
            MonthlyAmountMdl: monthlyAmount,
            RegulatoryBaseMdl: input.RegulatoryBaseMdl,
            BaseMultiplierPercent: basePlusAdditives,
            FinalMultiplierPercent: finalPercent,
            BreakdownJson: breakdown));
    }
}
