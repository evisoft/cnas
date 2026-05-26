using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.7-B — Pensie pentru judecători (Judge pension) seed
/// row. Eligibility requires confirmed past judicial status and more than 19 years
/// of judicial service; the benefit is 80% of the last judge salary.
/// </summary>
/// <remarks>
/// <para>TOR §3.7-B. Bază normativă: Legea 544/1995 cu privire la statutul
/// judecătorului. Procentul (80%) este o valoare provizorie — de actualizat
/// după publicare HG/Lege.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the reference
/// fact to be a <c>Money</c> value; <c>lastJudgeSalaryMdl</c> is therefore supplied
/// as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = JudgePensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class JudgePensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.7-B-JUDGE-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru judecători",
      "type": "object",
      "required": ["wasJudge", "judicialServiceYears", "lastJudgeSalaryMdl", "claimantIdnp"],
      "properties": {
        "wasJudge":             { "type": "boolean" },
        "judicialServiceYears": { "type": "integer", "minimum": 0 },
        "lastJudgeSalaryMdl":   { "type": "number",  "minimum": 0 },
        "claimantIdnp":         { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// past judicial status and a judicial service stage of more than 19 years;
    /// the benefit is 80% of the last judge salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "JUDGE_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasJudge", "value": true,
          "failCode": "JUDGE_PENSION_INELIGIBLE_NOT_JUDGE" },
        { "rule": "fact-greater-than", "fact": "judicialServiceYears", "value": 19,
          "failCode": "JUDGE_PENSION_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 80,
        "referenceFact": "lastJudgeSalaryMdl"
      },
      "successCode": "JUDGE_PENSION_ELIGIBLE"
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
            NameRo = "Pensie pentru judecători",
            NameEn = "Judge pension",
            NameRu = "Пенсия для судей",
            DescriptionRo =
                "Pensie specială acordată judecătorilor cu stagiu judiciar de cel puțin 20 de ani, " +
                "calculată ca 80% din ultima indemnizație lunară, conform Legii 544/1995.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-JUDGE-PENSION-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
