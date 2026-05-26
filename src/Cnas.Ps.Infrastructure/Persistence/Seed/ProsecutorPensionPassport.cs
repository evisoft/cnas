using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.7-C — Pensie pentru procurori (Prosecutor pension) seed
/// row. Eligibility requires confirmed past prosecutor status and more than 19
/// years of prosecutor service; the benefit is 80% of the last prosecutor salary.
/// </summary>
/// <remarks>
/// <para>TOR §3.7-C. Bază normativă: Legea 3/2016 cu privire la Procuratură.
/// Procentul (80%) este o valoare provizorie — de actualizat după publicare
/// HG/Lege.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the reference
/// fact to be a <c>Money</c> value; <c>lastProsecutorSalaryMdl</c> is therefore
/// supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = ProsecutorPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ProsecutorPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.7-C-PROSECUTOR-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru procurori",
      "type": "object",
      "required": ["wasProsecutor", "prosecutorServiceYears", "lastProsecutorSalaryMdl", "claimantIdnp"],
      "properties": {
        "wasProsecutor":             { "type": "boolean" },
        "prosecutorServiceYears":    { "type": "integer", "minimum": 0 },
        "lastProsecutorSalaryMdl":   { "type": "number",  "minimum": 0 },
        "claimantIdnp":              { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// past prosecutor status and a prosecutor service stage of more than 19 years;
    /// the benefit is 80% of the last prosecutor salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "PROSECUTOR_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasProsecutor", "value": true,
          "failCode": "PROSECUTOR_PENSION_INELIGIBLE_NOT_PROSECUTOR" },
        { "rule": "fact-greater-than", "fact": "prosecutorServiceYears", "value": 19,
          "failCode": "PROSECUTOR_PENSION_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 80,
        "referenceFact": "lastProsecutorSalaryMdl"
      },
      "successCode": "PROSECUTOR_PENSION_ELIGIBLE"
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
            NameRo = "Pensie pentru procurori",
            NameEn = "Prosecutor pension",
            NameRu = "Пенсия для прокуроров",
            DescriptionRo =
                "Pensie specială acordată procurorilor cu stagiu de cel puțin 20 de ani, " +
                "calculată ca 80% din ultima indemnizație lunară, conform Legii 3/2016.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-PROSECUTOR-PENSION-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
