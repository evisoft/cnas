using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.7-E — Pensie pentru funcționari publici (Public-servant
/// pension) seed row. Eligibility requires confirmed past civil-servant status and
/// more than 24 years of public service; the benefit is 65% of the last public
/// salary.
/// </summary>
/// <remarks>
/// <para>TOR §3.7-E. Bază normativă: Legea 158/2008 cu privire la funcția publică
/// și statutul funcționarului public. Procentul (65%) este o valoare provizorie —
/// de actualizat după publicare HG/Lege.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the reference
/// fact to be a <c>Money</c> value; <c>lastPublicSalaryMdl</c> is therefore
/// supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = PublicServantPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class PublicServantPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.7-E-PUBLIC-SERVANT-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru funcționari publici",
      "type": "object",
      "required": ["wasCivilServant", "publicServiceYears", "lastPublicSalaryMdl", "claimantIdnp"],
      "properties": {
        "wasCivilServant":      { "type": "boolean" },
        "publicServiceYears":   { "type": "integer", "minimum": 0 },
        "lastPublicSalaryMdl":  { "type": "number",  "minimum": 0 },
        "claimantIdnp":         { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// past civil-servant status and a public-service stage of more than 24 years;
    /// the benefit is 65% of the last public salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "PUBLIC_SERVANT_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasCivilServant", "value": true,
          "failCode": "PUBLIC_SERVANT_PENSION_INELIGIBLE_NOT_SERVANT" },
        { "rule": "fact-greater-than", "fact": "publicServiceYears", "value": 24,
          "failCode": "PUBLIC_SERVANT_PENSION_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 65,
        "referenceFact": "lastPublicSalaryMdl"
      },
      "successCode": "PUBLIC_SERVANT_PENSION_ELIGIBLE"
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
            NameRo = "Pensie pentru funcționari publici",
            NameEn = "Public-servant pension",
            NameRu = "Пенсия для государственных служащих",
            DescriptionRo =
                "Pensie specială acordată funcționarilor publici cu cel puțin 25 de ani de " +
                "stagiu în funcție publică, calculată ca 65% din ultima indemnizație lunară.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-PUBLIC-SERVANT-PENSION-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
