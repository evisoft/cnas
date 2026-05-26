using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.14-A — Asigurare de viață pentru veteranii de
/// război (War-veteran life insurance) seed row. Eligibility requires the
/// claimant to be a recognized war veteran whose status has been verified by
/// the competent commission; the benefit is a fixed insurance payout.
/// </summary>
/// <remarks>
/// <para>TOR §3.14-A. Bază normativă: Legea 190/2003 privind veteranii. The
/// 5 000 MDL fixed amount is a Moldovan default — valoare provizorie, de
/// actualizat la indexarea anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = LifeInsuranceVeteranPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LifeInsuranceVeteranPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.14-A-LIFE-INSURANCE-VETERAN";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Asigurare de viață veteran",
      "type": "object",
      "required": ["isWarVeteran", "verifiedByCommission", "claimantIdnp"],
      "properties": {
        "isWarVeteran":         { "type": "boolean" },
        "verifiedByCommission": { "type": "boolean" },
        "claimantIdnp":         { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// veteran status and commission verification; the benefit is a fixed 5 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LIFE_INS_VETERAN",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isWarVeteran", "value": true,
          "failCode": "LIFE_INS_VETERAN_INELIGIBLE_NOT_VETERAN" },
        { "rule": "fact-equals", "fact": "verifiedByCommission", "value": true,
          "failCode": "LIFE_INS_VETERAN_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 5000.00,
        "currency": "MDL"
      },
      "successCode": "LIFE_INS_VETERAN_ELIGIBLE"
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
            NameRo = "Asigurare de viață veteran",
            NameEn = "War-veteran life insurance",
            NameRu = "Страхование жизни ветерана войны",
            DescriptionRo =
                "Indemnizație de asigurare de viață acordată veteranilor de război " +
                "recunoscuți și verificați de comisie, conform Legii 190/2003.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LIFE-INS-VETERAN-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
