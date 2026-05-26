using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.12-D — Ajutor pentru bunuri esențiale pentru
/// nou-născut (Newborn essentials allowance) seed row. Eligibility requires that
/// the claim is filed within 90 days of birth and that the parent is insured;
/// the benefit is a fixed lump-sum.
/// </summary>
/// <remarks>
/// <para>TOR §3.12-D. Bază normativă: Legea 289/2004. The 2 500 MDL fixed amount
/// is a Moldovan default — valoare provizorie, de actualizat la indexarea
/// anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = NewbornEssentialsPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class NewbornEssentialsPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.12-D-NEWBORN-ESSENTIALS";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Ajutor pentru bunuri esențiale (nou-născut)",
      "type": "object",
      "required": ["birthDateUtc", "claimDateUtc", "isInsured", "parentIdnp"],
      "properties": {
        "birthDateUtc":  { "type": "string", "format": "date-time" },
        "claimDateUtc":  { "type": "string", "format": "date-time" },
        "isInsured":     { "type": "boolean" },
        "parentIdnp":    { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the claim is filed within 90 days of birth and that the parent is
    /// insured; the benefit is a fixed 2 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "NEWBORN_ESSENTIALS",
      "eligibility": [
        { "rule": "date-within-days", "fact": "birthDateUtc",
          "referenceFact": "claimDateUtc", "maxDays": 90,
          "failCode": "NEWBORN_ESSENTIALS_INELIGIBLE_LATE_CLAIM" },
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "NEWBORN_ESSENTIALS_INELIGIBLE_NOT_INSURED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2500.00,
        "currency": "MDL"
      },
      "successCode": "NEWBORN_ESSENTIALS_ELIGIBLE"
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
            NameRo = "Ajutor pentru bunuri esențiale (nou-născut)",
            NameEn = "Newborn essentials allowance",
            NameRu = "Помощь на товары первой необходимости для новорождённого",
            DescriptionRo =
                "Ajutor unic pentru bunuri esențiale acordat părintelui asigurat în primele " +
                "90 de zile de la nașterea copilului.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-NEWBORN-ESSENTIALS-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
