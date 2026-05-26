using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.8-C — Indemnizație pentru îngrijirea adultului cu
/// dizabilitate (Adult-disability care grant) seed row. Eligibility requires the
/// claimant to be the recognized caretaker of a disabled adult and to be currently
/// unemployed; the benefit is a fixed 1 500 MDL transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.8-C. Bază normativă: Legea 60/2012 privind incluziunea socială a
/// persoanelor cu dizabilități. Suma de 1 500 MDL este o valoare provizorie —
/// de actualizat după publicare HG/Lege.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = AdultDisabilityCareGrantPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class AdultDisabilityCareGrantPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.8-C-ADULT-DISABILITY-CARE-GRANT";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație pentru îngrijirea adultului cu dizabilitate",
      "type": "object",
      "required": ["caretakerForDisabledAdult", "caretakerNotEmployed", "claimantIdnp"],
      "properties": {
        "caretakerForDisabledAdult": { "type": "boolean" },
        "caretakerNotEmployed":      { "type": "boolean" },
        "claimantIdnp":              { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// recognized caretaker status and that the caretaker is not currently employed;
    /// the benefit is a flat 1 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "ADULT_DISABILITY_CARE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "caretakerForDisabledAdult", "value": true,
          "failCode": "ADULT_DISABILITY_CARE_INELIGIBLE_NOT_CARETAKER" },
        { "rule": "fact-equals", "fact": "caretakerNotEmployed", "value": true,
          "failCode": "ADULT_DISABILITY_CARE_INELIGIBLE_EMPLOYED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1500.00,
        "currency": "MDL"
      },
      "successCode": "ADULT_DISABILITY_CARE_ELIGIBLE"
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
            NameRo = "Indemnizație pentru îngrijirea adultului cu dizabilitate",
            NameEn = "Adult-disability care grant",
            NameRu = "Пособие по уходу за взрослым инвалидом",
            DescriptionRo =
                "Indemnizație lunară acordată îngrijitorului neangajat al unei persoane adulte " +
                "cu dizabilitate, conform Legii 60/2012.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-ADULT-DISABILITY-CARE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
