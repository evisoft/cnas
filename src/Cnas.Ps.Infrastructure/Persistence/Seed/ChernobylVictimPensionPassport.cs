using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.9-A — Pensie pentru victimele Cernobîl (Chernobyl-victim
/// pension) seed row. Eligibility requires recognized Chernobyl-victim status and
/// verification by the assessment commission; the benefit is a fixed 3 500 MDL
/// transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.9-A. Bază normativă: Legea 909/1992 privind protecția socială a
/// cetățenilor care au avut de suferit de pe urma catastrofei de la Cernobîl.
/// Suma de 3 500 MDL este o valoare provizorie — de actualizat după publicare
/// HG/Lege.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = ChernobylVictimPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ChernobylVictimPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.9-A-CHERNOBYL-VICTIM-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru victimele Cernobîl",
      "type": "object",
      "required": ["isChernobylVictim", "verifiedByCommission", "claimantIdnp"],
      "properties": {
        "isChernobylVictim":    { "type": "boolean" },
        "verifiedByCommission": { "type": "boolean" },
        "claimantIdnp":         { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// recognized Chernobyl-victim status and commission verification; the benefit
    /// is a flat 3 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "CHERNOBYL_VICTIM",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isChernobylVictim", "value": true,
          "failCode": "CHERNOBYL_VICTIM_INELIGIBLE_NOT_VICTIM" },
        { "rule": "fact-equals", "fact": "verifiedByCommission", "value": true,
          "failCode": "CHERNOBYL_VICTIM_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 3500.00,
        "currency": "MDL"
      },
      "successCode": "CHERNOBYL_VICTIM_ELIGIBLE"
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
            NameRo = "Pensie pentru victimele Cernobîl",
            NameEn = "Chernobyl-victim pension",
            NameRu = "Пенсия для жертв Чернобыля",
            DescriptionRo =
                "Pensie specială acordată persoanelor recunoscute ca victime ale catastrofei " +
                "de la Cernobîl, verificate de comisia de evaluare, conform Legii 909/1992.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-CHERNOBYL-VICTIM-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
