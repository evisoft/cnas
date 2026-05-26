using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.1-B — Indemnizație lunară pentru îngrijirea copilului
/// până la vârsta de 3 ani (Monthly child-care allowance, under-3) seed row.
/// Eligibility requires that the claimant is insured and that the child has not
/// yet reached the third birthday on the claim date. The benefit is a percentage
/// of the parent's reference (insured) salary.
/// </summary>
/// <remarks>
/// Numeric values (the 30% rate, the 0–3 year window) reflect the Moldovan
/// Government Decision currently in force for insured child-care allowances and
/// can be tuned by upserting the passport without code changes.
/// </remarks>
/// <example>
/// <code>
/// var passport = ChildCareAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ChildCareAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.1-B-CHILD-CARE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație lunară pentru îngrijirea copilului până la 3 ani",
      "type": "object",
      "required": ["childDateOfBirthUtc", "claimDateUtc", "parentIdnp", "isInsured", "referenceSalaryMdl"],
      "properties": {
        "childDateOfBirthUtc": { "type": "string", "format": "date-time" },
        "claimDateUtc":        { "type": "string", "format": "date-time" },
        "parentIdnp":          { "type": "string", "pattern": "^[0-9]{13}$" },
        "isInsured":           { "type": "boolean" },
        "referenceSalaryMdl":  { "type": "number", "minimum": 0 }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the parent is insured and the child is between 0 and 3 years old at the
    /// claim date; the benefit is 30% of the reference salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "CHILD_CARE_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "CHILD_CARE_INELIGIBLE_NOT_INSURED" },
        { "rule": "age-at-date-between", "dobFact": "childDateOfBirthUtc",
          "referenceFact": "claimDateUtc", "min": 0, "max": 2,
          "failCode": "CHILD_CARE_INELIGIBLE_CHILD_TOO_OLD" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 30,
        "referenceFact": "referenceSalaryMdl"
      },
      "successCode": "CHILD_CARE_ELIGIBLE"
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
    /// var passport = ChildCareAllowancePassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Indemnizație lunară pentru îngrijirea copilului până la 3 ani",
            NameEn = "Monthly child-care allowance (under 3)",
            NameRu = "Ежемесячное пособие по уходу за ребенком до 3 лет",
            DescriptionRo =
                "Indemnizație lunară plătită părintelui asigurat care îngrijește copilul " +
                "până la vârsta de 3 ani, conform Legii 289/2004 și hotărârilor de Guvern subsecvente.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-CHILD-CARE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
