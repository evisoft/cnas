using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.2-E — Pensie de merit (Merit pension) seed row.
/// Eligibility requires the claimant to belong to a recognized merit category
/// (culture, science, sport, labour) and to have at least 20 years of contribution.
/// </summary>
/// <remarks>
/// <para>TOR §3.2-E. Bază normativă: Legea 544/1995 privind statutul cadrelor
/// didactice și Legea 1544/2002 privind pensiile pentru merite deosebite.</para>
/// <para>The fixed 4 000 MDL value is a reasonable Moldovan default; the actual
/// amount is indexed annually by Government Decision and can be tuned via passport
/// upsert without code changes.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = MeritPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class MeritPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.2-E-MERIT-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie de merit",
      "type": "object",
      "required": ["meritCategory", "contributionYears", "claimantIdnp"],
      "properties": {
        "meritCategory":     { "type": "string", "enum": ["culture", "science", "sport", "labor"] },
        "contributionYears": { "type": "integer", "minimum": 0 },
        "claimantIdnp":      { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// merit category membership and a contribution stage of strictly more than 19
    /// years (i.e. at least 20); the benefit is a fixed 4 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "MERIT_PENSION",
      "eligibility": [
        { "rule": "fact-in-set", "fact": "meritCategory",
          "values": ["culture", "science", "sport", "labor"],
          "failCode": "MERIT_PENSION_INELIGIBLE_CATEGORY" },
        { "rule": "fact-greater-than", "fact": "contributionYears", "value": 19,
          "failCode": "MERIT_PENSION_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 4000.00,
        "currency": "MDL"
      },
      "successCode": "MERIT_PENSION_ELIGIBLE"
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
            NameRo = "Pensie de merit",
            NameEn = "Merit pension",
            NameRu = "Пенсия за особые заслуги",
            DescriptionRo =
                "Pensie acordată persoanelor cu merite deosebite în cultură, știință, sport " +
                "sau muncă și care au realizat cel puțin 20 ani de stagiu de cotizare.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-MERIT-PENSION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
