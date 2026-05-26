using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.13-A — Indemnizație pentru îngrijitor de persoană
/// vârstnică (Elderly-caretaker allowance) seed row. Eligibility requires the
/// claimant to act as caretaker for an elderly person older than 74, and to be
/// not employed elsewhere; the benefit is a fixed monthly amount.
/// </summary>
/// <remarks>
/// <para>TOR §3.13-A. Bază normativă: Legea 60/2012 privind incluziunea socială
/// a persoanelor cu dizabilități extinsă prin practica MMPS. The 1 300 MDL fixed
/// amount is a Moldovan default — valoare provizorie, de actualizat la indexarea
/// anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = CaretakerElderlyPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class CaretakerElderlyPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.13-A-CARETAKER-ELDERLY";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație îngrijitor persoană vârstnică",
      "type": "object",
      "required": ["caretakerForElderly", "elderlyAgeYears", "caretakerNotEmployed", "claimantIdnp"],
      "properties": {
        "caretakerForElderly":   { "type": "boolean" },
        "elderlyAgeYears":       { "type": "integer", "minimum": 0 },
        "caretakerNotEmployed":  { "type": "boolean" },
        "claimantIdnp":          { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the caretaker role, the elderly age &gt; 74, and the caretaker not-employed
    /// status; the benefit is a fixed 1 300 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "CARETAKER_ELDERLY",
      "eligibility": [
        { "rule": "fact-equals", "fact": "caretakerForElderly", "value": true,
          "failCode": "CARETAKER_ELDERLY_INELIGIBLE_NOT_CARETAKER" },
        { "rule": "fact-greater-than", "fact": "elderlyAgeYears", "value": 74,
          "failCode": "CARETAKER_ELDERLY_INELIGIBLE_ELDERLY_TOO_YOUNG" },
        { "rule": "fact-equals", "fact": "caretakerNotEmployed", "value": true,
          "failCode": "CARETAKER_ELDERLY_INELIGIBLE_CARETAKER_EMPLOYED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1300.00,
        "currency": "MDL"
      },
      "successCode": "CARETAKER_ELDERLY_ELIGIBLE"
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
            NameRo = "Indemnizație îngrijitor persoană vârstnică",
            NameEn = "Elderly-caretaker allowance",
            NameRu = "Пособие лицу, ухаживающему за пожилым человеком",
            DescriptionRo =
                "Indemnizație lunară acordată persoanei neîncadrate în câmpul muncii care " +
                "îngrijește la domiciliu o persoană vârstnică peste 75 ani.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-CARETAKER-ELDERLY-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
