using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.16-B — Indemnizație pentru victime ale traficului
/// de ființe umane (Victim-of-trafficking allowance) seed row. Eligibility
/// requires the claimant to be a recognized trafficking victim and to be
/// verified by the competent commission; the benefit is a fixed lump-sum.
/// </summary>
/// <remarks>
/// <para>TOR §3.16-B. Bază normativă: Legea 241/2005 privind prevenirea și
/// combaterea traficului de ființe umane. The 6 000 MDL fixed amount is a
/// Moldovan default — valoare provizorie, de actualizat la indexarea anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = VictimOfTraffickingPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class VictimOfTraffickingPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.16-B-VICTIM-OF-TRAFFICKING";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație victimă a traficului",
      "type": "object",
      "required": ["recognizedTraffickingVictim", "verifiedByCommission", "claimantIdnp"],
      "properties": {
        "recognizedTraffickingVictim": { "type": "boolean" },
        "verifiedByCommission":        { "type": "boolean" },
        "claimantIdnp":                { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// recognized-victim status and commission verification; the benefit is a
    /// fixed 6 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "TRAFFICKING_VICTIM",
      "eligibility": [
        { "rule": "fact-equals", "fact": "recognizedTraffickingVictim", "value": true,
          "failCode": "TRAFFICKING_VICTIM_INELIGIBLE_NOT_RECOGNIZED" },
        { "rule": "fact-equals", "fact": "verifiedByCommission", "value": true,
          "failCode": "TRAFFICKING_VICTIM_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 6000.00,
        "currency": "MDL"
      },
      "successCode": "TRAFFICKING_VICTIM_ELIGIBLE"
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
            NameRo = "Indemnizație victimă a traficului",
            NameEn = "Victim-of-trafficking allowance",
            NameRu = "Пособие жертве торговли людьми",
            DescriptionRo =
                "Indemnizație unică acordată victimelor recunoscute ale traficului de ființe " +
                "umane, conform Legii 241/2005.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-TRAFFICKING-VICTIM-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
