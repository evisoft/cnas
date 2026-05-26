using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.12-B — Alocație pentru familii numeroase
/// (Large-family allowance) seed row. Eligibility requires a household with more
/// than 4 members and a certified vulnerable status; the benefit is a fixed
/// monthly amount.
/// </summary>
/// <remarks>
/// TOR §3.12-B. Bază normativă: Legea 133/2008 privind ajutorul social. The
/// 1 500 MDL fixed amount is a Moldovan default — valoare provizorie, de
/// actualizat la indexarea anuală.
/// </remarks>
/// <example>
/// <code>
/// var passport = LargeFamilyAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LargeFamilyAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.12-B-LARGE-FAMILY-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Alocație pentru familii numeroase",
      "type": "object",
      "required": ["householdSize", "householdCertifiedVulnerable", "claimantIdnp"],
      "properties": {
        "householdSize":                 { "type": "integer", "minimum": 1 },
        "householdCertifiedVulnerable":  { "type": "boolean" },
        "claimantIdnp":                  { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// a household size strictly greater than 4 and a certified vulnerable status;
    /// the benefit is a fixed 1 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LARGE_FAMILY",
      "eligibility": [
        { "rule": "fact-greater-than", "fact": "householdSize", "value": 4,
          "failCode": "LARGE_FAMILY_INELIGIBLE_HOUSEHOLD_TOO_SMALL" },
        { "rule": "fact-equals", "fact": "householdCertifiedVulnerable", "value": true,
          "failCode": "LARGE_FAMILY_INELIGIBLE_NOT_VULNERABLE" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1500.00,
        "currency": "MDL"
      },
      "successCode": "LARGE_FAMILY_ELIGIBLE"
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
            NameRo = "Alocație pentru familii numeroase",
            NameEn = "Large-family allowance",
            NameRu = "Пособие многодетным семьям",
            DescriptionRo =
                "Alocație lunară acordată familiilor numeroase (peste 4 membri) certificate " +
                "vulnerabile, conform Legii 133/2008.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LARGE-FAMILY-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
