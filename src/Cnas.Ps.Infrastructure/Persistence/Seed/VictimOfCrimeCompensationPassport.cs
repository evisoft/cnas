using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.16-A — Compensație pentru victime ale infracțiunilor
/// (Victim-of-crime compensation) seed row. Eligibility requires the claimant
/// to be a recognized crime victim and to be verified by the competent
/// commission; the benefit is a fixed lump-sum compensation.
/// </summary>
/// <remarks>
/// <para>TOR §3.16-A. Bază normativă: Legea 137/2016 privind reabilitarea
/// victimelor infracțiunilor. The 8 000 MDL fixed amount is a Moldovan default
/// — valoare provizorie, de actualizat la indexarea anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = VictimOfCrimeCompensationPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class VictimOfCrimeCompensationPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.16-A-VICTIM-OF-CRIME-COMPENSATION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Compensație victimă infracțiune",
      "type": "object",
      "required": ["recognizedCrimeVictim", "verifiedByCommission", "claimantIdnp"],
      "properties": {
        "recognizedCrimeVictim": { "type": "boolean" },
        "verifiedByCommission":  { "type": "boolean" },
        "claimantIdnp":          { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// recognized-victim status and commission verification; the benefit is a
    /// fixed 8 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "CRIME_VICTIM_COMP",
      "eligibility": [
        { "rule": "fact-equals", "fact": "recognizedCrimeVictim", "value": true,
          "failCode": "CRIME_VICTIM_COMP_INELIGIBLE_NOT_RECOGNIZED" },
        { "rule": "fact-equals", "fact": "verifiedByCommission", "value": true,
          "failCode": "CRIME_VICTIM_COMP_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 8000.00,
        "currency": "MDL"
      },
      "successCode": "CRIME_VICTIM_COMP_ELIGIBLE"
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
            NameRo = "Compensație victimă infracțiune",
            NameEn = "Victim-of-crime compensation",
            NameRu = "Компенсация жертве преступления",
            DescriptionRo =
                "Compensație unică acordată victimelor recunoscute ale infracțiunilor, " +
                "conform Legii 137/2016.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-CRIME-VICTIM-COMP-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
