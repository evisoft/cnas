using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.2-D — Pensie de invaliditate pentru veteranii de război
/// (War-veteran invalidity pension) seed row. Eligibility requires the claimant to
/// be a recognized war veteran with a registered disability degree; the benefit is
/// tiered by disability severity.
/// </summary>
/// <remarks>
/// <para>TOR §3.2-D. Bază normativă: Legea 190/2003 privind veteranii și HG-urile
/// anuale care indexează indemnizațiile.</para>
/// <para>Tier values (3 500 / 2 700 / 2 000 MDL) are reasonable Moldovan defaults
/// derived from the task brief; they can be tuned via passport upsert once the
/// next indexation HG is published.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = WarVeteranInvalidityPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class WarVeteranInvalidityPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.2-D-INVALIDITY-WAR-VETERAN";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie de invaliditate pentru veteranii de război",
      "type": "object",
      "required": ["isWarVeteran", "disabilityDegree", "claimantIdnp"],
      "properties": {
        "isWarVeteran":     { "type": "boolean" },
        "disabilityDegree": { "type": "string", "enum": ["severe", "accentuated", "medium"] },
        "claimantIdnp":     { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// veteran status and a recognized disability degree; the benefit is looked up
    /// in a tier table keyed by disability degree.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "WAR_VETERAN_INVALIDITY",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isWarVeteran", "value": true,
          "failCode": "WAR_VETERAN_INVALIDITY_INELIGIBLE_NOT_VETERAN" },
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated", "medium"],
          "failCode": "WAR_VETERAN_INVALIDITY_INELIGIBLE_DEGREE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      3500.00,
          "accentuated": 2700.00,
          "medium":      2000.00
        }
      },
      "successCode": "WAR_VETERAN_INVALIDITY_ELIGIBLE"
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
            NameRo = "Pensie de invaliditate pentru veteranii de război",
            NameEn = "War-veteran invalidity pension",
            NameRu = "Пенсия по инвалидности для ветеранов войны",
            DescriptionRo =
                "Pensie acordată veteranilor de război cu dizabilitate severă, accentuată " +
                "sau medie, conform Legii 190/2003 și HG-urilor anuale de indexare.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-WAR-VETERAN-INVALIDITY-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
