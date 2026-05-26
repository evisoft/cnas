using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.4-B — Bilet de reabilitare balneară (Spa rehabilitation
/// voucher) seed row. Eligibility requires a positive medical recommendation and
/// that more than one year has passed since the last rehabilitation; the
/// entitlement is an in-kind voucher rather than cash.
/// </summary>
/// <remarks>
/// <para>TOR §3.4-B. Bază normativă: HG 1478/2014 privind acordarea biletelor de
/// reabilitare balneară.</para>
/// <para>Engine note: the engine's <c>amount</c> section is mandatory and only
/// supports monetary values, so the voucher is recorded as <c>fixed 0 MDL</c> and
/// the in-kind allocation is handled by the downstream workflow. A future engine
/// extension could add a <c>voucher</c> amount kind so the contract is expressed
/// natively.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = SpaRehabilitationPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class SpaRehabilitationPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.4-B-SPA-REHAB";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Bilet de reabilitare balneară",
      "type": "object",
      "required": ["medicalRecommendationOnFile", "lastRehabilitationYears", "claimantIdnp"],
      "properties": {
        "medicalRecommendationOnFile": { "type": "boolean" },
        "lastRehabilitationYears":     { "type": "number",  "minimum": 0 },
        "claimantIdnp":                { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that a medical recommendation exists and that more than one year has passed
    /// since the last spa cure; the "amount" is a sentinel 0 MDL marker because the
    /// entitlement is a voucher.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "SPA_REHAB",
      "eligibility": [
        { "rule": "fact-equals", "fact": "medicalRecommendationOnFile", "value": true,
          "failCode": "SPA_REHAB_INELIGIBLE_NO_RECOMMENDATION" },
        { "rule": "fact-greater-than", "fact": "lastRehabilitationYears", "value": 1,
          "failCode": "SPA_REHAB_INELIGIBLE_TOO_RECENT" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 0.00,
        "currency": "MDL"
      },
      "successCode": "SPA_REHAB_ELIGIBLE"
    }
    """;

    /// <summary>
    /// Builds a fully-populated <see cref="ServicePassport"/> seed row stamped with
    /// the supplied clock's <c>UtcNow</c>.
    /// </summary>
    /// <param name="clock">Clock abstraction (UTC) used to stamp <c>CreatedAtUtc</c>.</param>
    /// <returns>A new <see cref="ServicePassport"/> ready to be inserted into the DB.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Bilet de reabilitare balneară",
            NameEn = "Spa rehabilitation voucher",
            NameRu = "Путевка на санаторно-курортную реабилитацию",
            DescriptionRo =
                "Bilet pentru reabilitare balneară acordat persoanelor cu recomandare medicală " +
                "care nu au beneficiat de cură similară în ultimele 12 luni, conform HG 1478/2014.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-SPA-REHAB-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
