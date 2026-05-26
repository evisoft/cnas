using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.1-F — Indemnizație unică la naștere (mama)
/// (One-off maternity allowance paid to the mother) seed row. Eligibility requires
/// that the mother is insured and that the claim is submitted within one year of
/// the birth date. The benefit is a fixed amount in MDL.
/// </summary>
/// <remarks>
/// TOR §3.1-F. The 11 000 MDL fixed value matches the Government Decision currently
/// in force for the one-off maternity allowance paid to the mother; it can be tuned
/// via passport upsert without code changes when indexation is published.
/// </remarks>
/// <example>
/// <code>
/// var passport = MaternityAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class MaternityAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.1-F-MATERNITY-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație unică la naștere (mama)",
      "type": "object",
      "required": ["isInsured", "birthDateUtc", "claimDateUtc"],
      "properties": {
        "isInsured":    { "type": "boolean" },
        "birthDateUtc": { "type": "string", "format": "date-time" },
        "claimDateUtc": { "type": "string", "format": "date-time" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the mother is insured and that the claim is submitted within 365 days
    /// of the birth date; the benefit is a fixed 11 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "MATERNITY_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "MATERNITY_ALLOWANCE_INELIGIBLE_NOT_INSURED" },
        { "rule": "date-within-days", "fact": "birthDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "MATERNITY_ALLOWANCE_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 11000.00,
        "currency": "MDL"
      },
      "successCode": "MATERNITY_ALLOWANCE_ELIGIBLE"
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
    /// var passport = MaternityAllowancePassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Indemnizație unică la naștere (mama)",
            NameEn = "One-off maternity allowance (mother)",
            NameRu = "Единовременное пособие при рождении (матери)",
            DescriptionRo =
                "Indemnizație unică acordată mamei asigurate la nașterea copilului, " +
                "în cuantumul stabilit anual prin hotărâre de Guvern.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-MATERNITY-ALLOWANCE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
