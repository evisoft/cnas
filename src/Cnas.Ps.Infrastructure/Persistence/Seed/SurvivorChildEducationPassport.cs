using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.5-D — Pensie de urmaș pentru copil în studii (Survivor
/// pension — child enrolled in education) seed row. Eligibility requires the
/// claimant to be the deceased's child, to be enrolled in education, and to be
/// under 23 years of age; the benefit is 50% of the deceased's average insured
/// income.
/// </summary>
/// <remarks>
/// <para>TOR §3.5-D. Bază normativă: Legea 156/1998 privind sistemul public de
/// pensii — art. 49 (extinderea dreptului la pensia de urmaș până la 23 de ani
/// pentru copiii înscriși la studii).</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the
/// reference fact to be a <c>Money</c> value; <c>deceasedAverageInsuredIncomeMdl</c>
/// is therefore supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = SurvivorChildEducationPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class SurvivorChildEducationPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.5-D-SURVIVOR-CHILD-EDUCATION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie de urmaș pentru copil în studii",
      "type": "object",
      "required": [
        "relationshipToDeceased", "enrolledInEducation", "survivorAgeYears",
        "deceasedAverageInsuredIncomeMdl", "claimantIdnp"
      ],
      "properties": {
        "relationshipToDeceased":           { "type": "string",  "enum": ["spouse", "child", "parent"] },
        "enrolledInEducation":              { "type": "boolean" },
        "survivorAgeYears":                 { "type": "integer", "minimum": 0, "maximum": 30 },
        "deceasedAverageInsuredIncomeMdl":  { "type": "number",  "minimum": 0 },
        "claimantIdnp":                     { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the child relationship, active enrolment and the under-23 age cap; the
    /// benefit is 50% of the deceased's average insured income.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "SURVIVOR_CHILD_EDUCATION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "relationshipToDeceased", "value": "child",
          "failCode": "SURVIVOR_CHILD_EDUCATION_INELIGIBLE_RELATIONSHIP" },
        { "rule": "fact-equals", "fact": "enrolledInEducation", "value": true,
          "failCode": "SURVIVOR_CHILD_EDUCATION_INELIGIBLE_NOT_ENROLLED" },
        { "rule": "fact-less-than", "fact": "survivorAgeYears", "value": 23,
          "failCode": "SURVIVOR_CHILD_EDUCATION_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 50,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "SURVIVOR_CHILD_EDUCATION_ELIGIBLE"
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
            NameRo = "Pensie de urmaș pentru copil în studii",
            NameEn = "Survivor pension — child in education",
            NameRu = "Пенсия по случаю потери кормильца — учащийся ребенок",
            DescriptionRo =
                "Pensie de urmaș acordată copilului asiguratului decedat, înscris în " +
                "instituții de învățământ, până la împlinirea vârstei de 23 de ani.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-SURVIVOR-CHILD-EDUCATION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
