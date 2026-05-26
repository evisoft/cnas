using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.6-E — Ajutor pentru venit mic (Low-income support) seed
/// row. Eligibility requires the current monthly household income to be below
/// 1 200 MDL and the household to have more than one member; the benefit is a fixed
/// 600 MDL transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.6-E. Bază normativă: Legea 133/2008 cu privire la ajutorul social.
/// Pragul de 1 200 MDL și suma de 600 MDL sunt valori provizorii — de actualizat
/// după publicare HG/Lege.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = LowIncomeSupportPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LowIncomeSupportPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.6-E-INCOME-SUPPORT-LOW-INCOME";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Ajutor pentru venit mic",
      "type": "object",
      "required": ["currentMonthlyIncomeMdl", "householdSize", "claimantIdnp"],
      "properties": {
        "currentMonthlyIncomeMdl": { "type": "number",  "minimum": 0 },
        "householdSize":           { "type": "integer", "minimum": 1 },
        "claimantIdnp":            { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the monthly household income is strictly below 1 200 MDL and that the
    /// household includes more than one person; the benefit is a flat 600 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LOW_INCOME_SUPPORT",
      "eligibility": [
        { "rule": "fact-less-than", "fact": "currentMonthlyIncomeMdl", "value": 1200,
          "failCode": "LOW_INCOME_SUPPORT_INELIGIBLE_INCOME_ABOVE_THRESHOLD" },
        { "rule": "fact-greater-than", "fact": "householdSize", "value": 1,
          "failCode": "LOW_INCOME_SUPPORT_INELIGIBLE_HOUSEHOLD_SIZE" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 600.00,
        "currency": "MDL"
      },
      "successCode": "LOW_INCOME_SUPPORT_ELIGIBLE"
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
            NameRo = "Ajutor pentru venit mic",
            NameEn = "Low-income support",
            NameRu = "Поддержка малоимущих",
            DescriptionRo =
                "Ajutor social lunar pentru gospodăriile cu venit lunar mai mic decât pragul " +
                "stabilit, conform Legii 133/2008.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LOW-INCOME-SUPPORT-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
