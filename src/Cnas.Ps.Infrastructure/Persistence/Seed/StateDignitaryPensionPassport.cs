using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.7-A — Pensie pentru demnitari de stat (State-dignitary
/// pension) seed row. Eligibility requires confirmed state-dignitary status and
/// more than 4 years of effective service term; the benefit is a fixed 8 000 MDL.
/// </summary>
/// <remarks>
/// <para>TOR §3.7-A. Bază normativă: Legea 1591/2002 cu privire la asigurarea
/// activității Președintelui Republicii Moldova și alte acte care reglementează
/// pensiile demnitarilor de stat. Suma de 8 000 MDL este o valoare provizorie —
/// de actualizat după publicare HG/Lege.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = StateDignitaryPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class StateDignitaryPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.7-A-STATE-DIGNITARY-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru demnitari de stat",
      "type": "object",
      "required": ["wasStateDignitary", "serviceTermYears", "claimantIdnp"],
      "properties": {
        "wasStateDignitary": { "type": "boolean" },
        "serviceTermYears":  { "type": "integer", "minimum": 0 },
        "claimantIdnp":      { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// confirmed state-dignitary status and a service term of more than 4 years;
    /// the benefit is a flat 8 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "STATE_DIGNITARY_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasStateDignitary", "value": true,
          "failCode": "STATE_DIGNITARY_PENSION_INELIGIBLE_NOT_DIGNITARY" },
        { "rule": "fact-greater-than", "fact": "serviceTermYears", "value": 4,
          "failCode": "STATE_DIGNITARY_PENSION_INELIGIBLE_SERVICE_TERM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 8000.00,
        "currency": "MDL"
      },
      "successCode": "STATE_DIGNITARY_PENSION_ELIGIBLE"
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
            NameRo = "Pensie pentru demnitari de stat",
            NameEn = "State-dignitary pension",
            NameRu = "Пенсия для государственных деятелей",
            DescriptionRo =
                "Pensie specială acordată demnitarilor de stat care au exercitat un mandat " +
                "de minimum 5 ani, conform Legii 1591/2002.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-STATE-DIGNITARY-PENSION-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
