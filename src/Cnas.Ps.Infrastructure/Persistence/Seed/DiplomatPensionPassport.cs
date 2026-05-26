using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.7-D — Pensie pentru diplomați (Diplomat pension) seed
/// row. Eligibility requires confirmed past diplomatic status and more than 14
/// years of diplomatic service; the benefit is 75% of the last diplomat salary.
/// </summary>
/// <remarks>
/// <para>TOR §3.7-D. Bază normativă: Legea 761/2001 cu privire la serviciul
/// diplomatic. Procentul (75%) este o valoare provizorie — de actualizat după
/// publicare HG/Lege.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the reference
/// fact to be a <c>Money</c> value; <c>lastDiplomatSalaryMdl</c> is therefore
/// supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = DiplomatPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DiplomatPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.7-D-DIPLOMAT-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru diplomați",
      "type": "object",
      "required": ["wasDiplomat", "diplomaticServiceYears", "lastDiplomatSalaryMdl", "claimantIdnp"],
      "properties": {
        "wasDiplomat":             { "type": "boolean" },
        "diplomaticServiceYears":  { "type": "integer", "minimum": 0 },
        "lastDiplomatSalaryMdl":   { "type": "number",  "minimum": 0 },
        "claimantIdnp":            { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// past diplomatic status and a diplomatic service stage of more than 14 years;
    /// the benefit is 75% of the last diplomat salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DIPLOMAT_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasDiplomat", "value": true,
          "failCode": "DIPLOMAT_PENSION_INELIGIBLE_NOT_DIPLOMAT" },
        { "rule": "fact-greater-than", "fact": "diplomaticServiceYears", "value": 14,
          "failCode": "DIPLOMAT_PENSION_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 75,
        "referenceFact": "lastDiplomatSalaryMdl"
      },
      "successCode": "DIPLOMAT_PENSION_ELIGIBLE"
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
            NameRo = "Pensie pentru diplomați",
            NameEn = "Diplomat pension",
            NameRu = "Пенсия для дипломатов",
            DescriptionRo =
                "Pensie specială acordată diplomaților cu stagiu diplomatic de cel puțin 15 ani, " +
                "calculată ca 75% din ultima indemnizație lunară, conform Legii 761/2001.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DIPLOMAT-PENSION-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
