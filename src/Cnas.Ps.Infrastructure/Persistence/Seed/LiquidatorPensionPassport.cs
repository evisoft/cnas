using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.9-C — Pensie pentru lichidatorii avariei de la Cernobîl
/// (Chernobyl liquidator pension) seed row. Eligibility requires that the claimant
/// participated in the Chernobyl clean-up operations and that the status has been
/// verified by the competent commission; the benefit is a fixed monthly amount.
/// </summary>
/// <remarks>
/// <para>TOR §3.9-C. Bază normativă: Legea 909/1992 privind protecția socială
/// a cetățenilor care au avut de suferit de pe urma catastrofei de la Cernobîl.</para>
/// <para>Valoare provizorie (4 000 MDL) — de actualizat conform Hotărârii de Guvern
/// anuale de indexare. The fixed amount keeps the seed deterministic for tests.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = LiquidatorPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LiquidatorPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.9-C-LIQUIDATOR-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie lichidator Cernobîl",
      "type": "object",
      "required": ["wasChernobylLiquidator", "verifiedByCommission", "claimantIdnp"],
      "properties": {
        "wasChernobylLiquidator": { "type": "boolean" },
        "verifiedByCommission":   { "type": "boolean" },
        "claimantIdnp":           { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// liquidator status and commission verification; the benefit is a fixed 4 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LIQUIDATOR_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasChernobylLiquidator", "value": true,
          "failCode": "LIQUIDATOR_PENSION_INELIGIBLE_NOT_LIQUIDATOR" },
        { "rule": "fact-equals", "fact": "verifiedByCommission", "value": true,
          "failCode": "LIQUIDATOR_PENSION_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 4000.00,
        "currency": "MDL"
      },
      "successCode": "LIQUIDATOR_PENSION_ELIGIBLE"
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
            NameRo = "Pensie pentru lichidatorii avariei de la Cernobîl",
            NameEn = "Chernobyl liquidator pension",
            NameRu = "Пенсия ликвидаторам Чернобыльской аварии",
            DescriptionRo =
                "Pensie lunară acordată cetățenilor care au participat la lichidarea consecințelor " +
                "avariei de la Cernobîl, conform Legii 909/1992.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LIQUIDATOR-PENSION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
