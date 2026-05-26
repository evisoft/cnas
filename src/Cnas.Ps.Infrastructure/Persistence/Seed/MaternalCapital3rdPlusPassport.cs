using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.17-B — Capital matern (al treilea copil sau mai mult)
/// (Maternal capital — 3rd or subsequent child) seed row. Eligibility requires
/// the parent to be insured, the birth-order to be strictly greater than 2, and
/// the claim to be filed within 365 days of birth; the benefit is a fixed lump-sum.
/// </summary>
/// <remarks>
/// <para>TOR §3.17-B. Bază normativă: Legea 289/2004 cu modificările privind
/// capitalul matern. The 35 000 MDL fixed amount is a Moldovan default —
/// valoare provizorie, de actualizat la indexarea anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = MaternalCapital3rdPlusPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class MaternalCapital3rdPlusPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.17-B-MATERNAL-CAPITAL-3RD-PLUS";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Capital matern (al treilea copil sau mai mult)",
      "type": "object",
      "required": ["isInsured", "birthOrder", "birthDateUtc", "claimDateUtc", "parentIdnp"],
      "properties": {
        "isInsured":    { "type": "boolean" },
        "birthOrder":   { "type": "integer", "minimum": 1, "maximum": 10 },
        "birthDateUtc": { "type": "string",  "format": "date-time" },
        "claimDateUtc": { "type": "string",  "format": "date-time" },
        "parentIdnp":   { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the insured-parent status, birth-order &gt; 2, and a claim within 365 days of
    /// birth; the benefit is a fixed 35 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "MATERNAL_CAPITAL_3P",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "MATERNAL_CAPITAL_3P_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-greater-than", "fact": "birthOrder", "value": 2,
          "failCode": "MATERNAL_CAPITAL_3P_INELIGIBLE_BIRTH_ORDER" },
        { "rule": "date-within-days", "fact": "birthDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "MATERNAL_CAPITAL_3P_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 35000.00,
        "currency": "MDL"
      },
      "successCode": "MATERNAL_CAPITAL_3P_ELIGIBLE"
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
            NameRo = "Capital matern (al treilea copil sau mai mult)",
            NameEn = "Maternal capital (3rd or subsequent child)",
            NameRu = "Материнский капитал (третий и последующие)",
            DescriptionRo =
                "Capital matern unic acordat părintelui asigurat la nașterea celui de-al " +
                "treilea sau următorului copil, conform Legii 289/2004.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-MATERNAL-CAPITAL-3P-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
