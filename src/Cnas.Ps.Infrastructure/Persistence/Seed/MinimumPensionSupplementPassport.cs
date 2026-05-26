using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.2-F — Supliment la pensia minimă (Minimum pension
/// supplement) seed row. Eligibility requires the claimant to be a retiree whose
/// current monthly pension is strictly less than 1 500 MDL; the benefit is a fixed
/// 500 MDL supplement.
/// </summary>
/// <remarks>
/// <para>TOR §3.2-F. Bază normativă: HG anuală de indexare a pensiei minime
/// garantate. The 1 500 MDL threshold and 500 MDL supplement are reasonable
/// Moldovan defaults — valori provizorii, de tunat după publicarea HG.</para>
/// <para>Engine note: <c>fact-less-than</c> compares numeric facts only, so
/// <c>currentPensionMdl</c> is supplied as a raw decimal (not a <c>Money</c>) for
/// this eligibility rule.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = MinimumPensionSupplementPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class MinimumPensionSupplementPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.2-F-MINIMUM-PENSION-SUPPLEMENT";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Supliment la pensia minimă",
      "type": "object",
      "required": ["isRetiree", "currentPensionMdl", "claimantIdnp"],
      "properties": {
        "isRetiree":         { "type": "boolean" },
        "currentPensionMdl": { "type": "number",  "minimum": 0 },
        "claimantIdnp":      { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// retiree status and that the current pension is below 1 500 MDL; the benefit
    /// is a fixed 500 MDL supplement.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "MIN_PENSION_SUPPLEMENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isRetiree", "value": true,
          "failCode": "MIN_PENSION_SUPPLEMENT_INELIGIBLE_NOT_RETIREE" },
        { "rule": "fact-less-than", "fact": "currentPensionMdl", "value": 1500,
          "failCode": "MIN_PENSION_SUPPLEMENT_INELIGIBLE_ABOVE_THRESHOLD" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 500.00,
        "currency": "MDL"
      },
      "successCode": "MIN_PENSION_SUPPLEMENT_ELIGIBLE"
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
            NameRo = "Supliment la pensia minimă",
            NameEn = "Minimum pension supplement",
            NameRu = "Доплата к минимальной пенсии",
            DescriptionRo =
                "Supliment lunar acordat pensionarilor a căror pensie este sub pragul minim " +
                "garantat de stat, conform HG anuale de indexare.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-MIN-PENSION-SUPPLEMENT-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
