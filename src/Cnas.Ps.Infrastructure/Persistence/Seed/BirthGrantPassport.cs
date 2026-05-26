using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the canonical Annex 3.1-A — Indemnizație la nașterea copilului (Birth grant)
/// <see cref="ServicePassport"/> seed row. The rules embedded in
/// <see cref="ServicePassport.DecisionRulesJson"/> mirror Legea 289/2004 and the
/// rates Government Decisions publish annually.
/// </summary>
/// <example>
/// <code>
/// var passport = BirthGrantPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class BirthGrantPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.1-A-BIRTH-GRANT";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing form. Mirrors UC06 intake.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație la nașterea copilului",
      "type": "object",
      "required": ["birthDateUtc", "birthOrder", "parentIdnp", "isInsured", "claimDateUtc"],
      "properties": {
        "birthDateUtc": { "type": "string", "format": "date-time" },
        "birthOrder":   { "type": "integer", "minimum": 1, "maximum": 10 },
        "parentIdnp":   { "type": "string", "pattern": "^[0-9]{13}$" },
        "isInsured":    { "type": "boolean" },
        "claimDateUtc": { "type": "string", "format": "date-time" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c> to decide eligibility
    /// and benefit amount for this service.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "BIRTH_GRANT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "INELIGIBLE_NOT_INSURED" },
        { "rule": "date-within-days", "fact": "birthDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "birthOrder",
        "currency": "MDL",
        "table": { "1": 11000.00, "2": 12000.00, "default": 13000.00 }
      },
      "successCode": "BIRTH_GRANT_ELIGIBLE"
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
            NameRo = "Indemnizație la nașterea copilului",
            NameEn = "Birth grant",
            NameRu = "Пособие при рождении ребенка",
            DescriptionRo = "Indemnizație unică acordată la nașterea copilului, conform Legii 289/2004.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-BIRTH-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
