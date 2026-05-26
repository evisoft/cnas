using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.17-A — Capital matern (al doilea copil)
/// (Maternal capital — 2nd child) seed row. Eligibility requires the parent to
/// be insured, the birth-order to be exactly 2, and the claim to be filed within
/// 365 days of birth; the benefit is a fixed lump-sum.
/// </summary>
/// <remarks>
/// <para>TOR §3.17-A. Bază normativă: Legea 289/2004 cu modificările privind
/// capitalul matern. The 25 000 MDL fixed amount is a Moldovan default —
/// valoare provizorie, de actualizat la indexarea anuală.</para>
/// <para>Engine note: birth-order equality (<c>== 2</c>) is expressed via
/// <c>fact-equals</c> with a numeric value. The engine compares integer facts
/// via <c>decimal</c> equality (see <c>EvalFactEquals</c>).</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = MaternalCapital2ndChildPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class MaternalCapital2ndChildPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.17-A-MATERNAL-CAPITAL-2ND-CHILD";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Capital matern (al doilea copil)",
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
    /// the insured-parent status, birth-order == 2, and a claim within 365 days of
    /// birth; the benefit is a fixed 25 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "MATERNAL_CAPITAL_2",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "MATERNAL_CAPITAL_2_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-equals", "fact": "birthOrder", "value": 2,
          "failCode": "MATERNAL_CAPITAL_2_INELIGIBLE_BIRTH_ORDER" },
        { "rule": "date-within-days", "fact": "birthDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "MATERNAL_CAPITAL_2_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 25000.00,
        "currency": "MDL"
      },
      "successCode": "MATERNAL_CAPITAL_2_ELIGIBLE"
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
            NameRo = "Capital matern (al doilea copil)",
            NameEn = "Maternal capital (2nd child)",
            NameRu = "Материнский капитал (второй ребёнок)",
            DescriptionRo =
                "Capital matern unic acordat părintelui asigurat la nașterea celui de-al doilea " +
                "copil, conform Legii 289/2004.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-MATERNAL-CAPITAL-2-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
