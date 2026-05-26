using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.8-G — Alocație pentru transport pentru persoanele cu
/// dizabilități (Transport allowance for disabled persons) seed row. Eligibility
/// requires a severe or accentuated disability degree and a positive medical
/// recommendation for assisted transport; the benefit is a fixed 800 MDL transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.8-G. Bază normativă: Legea 60/2012 privind incluziunea socială a
/// persoanelor cu dizabilități. Suma de 800 MDL este o valoare provizorie —
/// de actualizat după publicare HG/Lege.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = TransportAllowanceDisabledPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class TransportAllowanceDisabledPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.8-G-TRANSPORT-ALLOWANCE-DISABLED";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Alocație pentru transport pentru persoanele cu dizabilități",
      "type": "object",
      "required": ["disabilityDegree", "medicalRecommendation", "claimantIdnp"],
      "properties": {
        "disabilityDegree":      { "type": "string",  "enum": ["severe", "accentuated", "medium"] },
        "medicalRecommendation": { "type": "boolean" },
        "claimantIdnp":          { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// a severe or accentuated disability degree and a positive medical
    /// recommendation; the benefit is a flat 800 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "TRANSPORT_ALLOW_DISABLED",
      "eligibility": [
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated"],
          "failCode": "TRANSPORT_ALLOW_DISABLED_INELIGIBLE_DEGREE" },
        { "rule": "fact-equals", "fact": "medicalRecommendation", "value": true,
          "failCode": "TRANSPORT_ALLOW_DISABLED_INELIGIBLE_NO_RECOMMENDATION" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 800.00,
        "currency": "MDL"
      },
      "successCode": "TRANSPORT_ALLOW_DISABLED_ELIGIBLE"
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
            NameRo = "Alocație pentru transport pentru persoanele cu dizabilități",
            NameEn = "Transport allowance for disabled persons",
            NameRu = "Транспортное пособие для инвалидов",
            DescriptionRo =
                "Alocație lunară pentru cheltuielile de transport ale persoanelor cu " +
                "dizabilitate severă sau accentuată, conform Legii 60/2012.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-TRANSPORT-ALLOW-DISABLED-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
