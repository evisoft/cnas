using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.13-C — Indemnizație respiro pentru îngrijitor de
/// persoană cu dizabilitate (Respite-care allowance) seed row. Eligibility
/// requires the claimant to act as caretaker for a disabled relative and to
/// have provided more than 89 days of care; the benefit is a fixed amount.
/// </summary>
/// <remarks>
/// <para>TOR §3.13-C. Bază normativă: Legea 60/2012 privind incluziunea socială
/// a persoanelor cu dizabilități. The 600 MDL fixed amount is a Moldovan default
/// — valoare provizorie, de actualizat la indexarea anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = RespiteCareDisabilityPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class RespiteCareDisabilityPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.13-C-RESPITE-CARE-DISABILITY";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație respiro îngrijitor dizabilitate",
      "type": "object",
      "required": ["caretakerForDisabledRelative", "careDays", "claimantIdnp"],
      "properties": {
        "caretakerForDisabledRelative": { "type": "boolean" },
        "careDays":                     { "type": "integer", "minimum": 0 },
        "claimantIdnp":                 { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the caretaker role and more than 89 days of care provided; the benefit is
    /// a fixed 600 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "RESPITE_CARE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "caretakerForDisabledRelative", "value": true,
          "failCode": "RESPITE_CARE_INELIGIBLE_NOT_CARETAKER" },
        { "rule": "fact-greater-than", "fact": "careDays", "value": 89,
          "failCode": "RESPITE_CARE_INELIGIBLE_INSUFFICIENT_CARE_DAYS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 600.00,
        "currency": "MDL"
      },
      "successCode": "RESPITE_CARE_ELIGIBLE"
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
            NameRo = "Indemnizație respiro îngrijitor dizabilitate",
            NameEn = "Respite-care allowance",
            NameRu = "Пособие на передышку для ухаживающих",
            DescriptionRo =
                "Indemnizație acordată îngrijitorilor de persoane cu dizabilități după 90 " +
                "de zile de îngrijire continuă, conform Legii 60/2012.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-RESPITE-CARE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
