using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.12-A — Indemnizație familială (Family indemnity) seed
/// row. Eligibility requires a household with more than 3 members and a current
/// monthly income strictly below 2 500 MDL; the benefit is a fixed monthly amount.
/// </summary>
/// <remarks>
/// <para>TOR §3.12-A. Bază normativă: Legea 133/2008 privind ajutorul social.
/// The 800 MDL fixed amount and the 2 500 MDL income threshold are Moldovan
/// defaults — valori provizorii, de actualizat la indexarea anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = FamilyIndemnityPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class FamilyIndemnityPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.12-A-FAMILY-INDEMNITY";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație familială",
      "type": "object",
      "required": ["householdSize", "currentMonthlyIncomeMdl", "claimantIdnp"],
      "properties": {
        "householdSize":            { "type": "integer", "minimum": 1 },
        "currentMonthlyIncomeMdl":  { "type": "number",  "minimum": 0 },
        "claimantIdnp":             { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// a household size strictly greater than 3 and an income strictly below
    /// 2 500 MDL; the benefit is a fixed 800 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "FAMILY_INDEMNITY",
      "eligibility": [
        { "rule": "fact-greater-than", "fact": "householdSize", "value": 3,
          "failCode": "FAMILY_INDEMNITY_INELIGIBLE_HOUSEHOLD_TOO_SMALL" },
        { "rule": "fact-less-than", "fact": "currentMonthlyIncomeMdl", "value": 2500,
          "failCode": "FAMILY_INDEMNITY_INELIGIBLE_INCOME_TOO_HIGH" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 800.00,
        "currency": "MDL"
      },
      "successCode": "FAMILY_INDEMNITY_ELIGIBLE"
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
            NameRo = "Indemnizație familială",
            NameEn = "Family indemnity",
            NameRu = "Семейное пособие",
            DescriptionRo =
                "Indemnizație lunară acordată familiilor cu peste 3 membri și venit lunar " +
                "sub pragul stabilit, conform Legii 133/2008.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-FAMILY-INDEMNITY-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
