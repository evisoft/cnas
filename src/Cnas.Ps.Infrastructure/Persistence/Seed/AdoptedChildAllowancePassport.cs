using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.1-C — Indemnizație unică la adopția copilului
/// (One-off allowance on child adoption) seed row. Eligibility requires that
/// the adoptive parent is insured and that the claim is filed within 365 days
/// of the legal adoption date.
/// </summary>
/// <remarks>
/// The 9 000 MDL fixed amount is a reasonable Moldovan default aligned with the
/// indemnization once unique for adopted children and can be tuned via passport
/// upsert when the annual Government Decision updates the rate.
/// </remarks>
/// <example>
/// <code>
/// var passport = AdoptedChildAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class AdoptedChildAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.1-C-ADOPTED-CHILD";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație unică la adopția copilului",
      "type": "object",
      "required": ["adoptionDateUtc", "claimDateUtc", "parentIdnp", "isInsured"],
      "properties": {
        "adoptionDateUtc": { "type": "string", "format": "date-time" },
        "claimDateUtc":    { "type": "string", "format": "date-time" },
        "parentIdnp":      { "type": "string", "pattern": "^[0-9]{13}$" },
        "isInsured":       { "type": "boolean" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the adoptive parent is insured and that the claim is filed within 365
    /// days of the adoption date; the benefit is a fixed 9 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "ADOPTED_CHILD_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "ADOPTED_CHILD_INELIGIBLE_NOT_INSURED" },
        { "rule": "date-within-days", "fact": "adoptionDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "ADOPTED_CHILD_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 9000.00,
        "currency": "MDL"
      },
      "successCode": "ADOPTED_CHILD_ELIGIBLE"
    }
    """;

    /// <summary>
    /// Builds a fully-populated <see cref="ServicePassport"/> seed row stamped with
    /// the supplied clock's <c>UtcNow</c>.
    /// </summary>
    /// <param name="clock">Clock abstraction (UTC) used to stamp <c>CreatedAtUtc</c>.</param>
    /// <returns>A new <see cref="ServicePassport"/> ready to be inserted into the DB.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
    /// <example>
    /// <code>
    /// var passport = AdoptedChildAllowancePassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Indemnizație unică la adopția copilului",
            NameEn = "One-off allowance on child adoption",
            NameRu = "Единовременное пособие при усыновлении ребенка",
            DescriptionRo =
                "Indemnizație unică acordată părintelui adoptiv asigurat la adopția " +
                "unui copil, conform Legii 289/2004.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-ADOPTED-CHILD-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
