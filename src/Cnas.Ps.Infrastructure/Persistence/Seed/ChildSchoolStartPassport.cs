using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.12-E — Ajutor de început de an școlar
/// (Child school-start allowance) seed row. Eligibility requires the child to be
/// older than 5 years, enrolled in school, and from a household certified as
/// vulnerable; the benefit is a fixed seasonal amount.
/// </summary>
/// <remarks>
/// <para>TOR §3.12-E. Bază normativă: HG anuală privind acordarea ajutorului
/// social la începutul anului școlar. The 500 MDL fixed amount is a Moldovan
/// default — valoare provizorie, de actualizat anual.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = ChildSchoolStartPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ChildSchoolStartPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.12-E-CHILD-SCHOOL-START";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Ajutor de început de an școlar",
      "type": "object",
      "required": ["childAgeYears", "childEnrolledInSchool", "householdCertifiedVulnerable", "claimantIdnp"],
      "properties": {
        "childAgeYears":                { "type": "integer", "minimum": 0 },
        "childEnrolledInSchool":        { "type": "boolean" },
        "householdCertifiedVulnerable": { "type": "boolean" },
        "claimantIdnp":                 { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the child age &gt; 5, school enrolment, and household vulnerability; the
    /// benefit is a fixed 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "CHILD_SCHOOL_START",
      "eligibility": [
        { "rule": "fact-greater-than", "fact": "childAgeYears", "value": 5,
          "failCode": "CHILD_SCHOOL_START_INELIGIBLE_TOO_YOUNG" },
        { "rule": "fact-equals", "fact": "childEnrolledInSchool", "value": true,
          "failCode": "CHILD_SCHOOL_START_INELIGIBLE_NOT_ENROLLED" },
        { "rule": "fact-equals", "fact": "householdCertifiedVulnerable", "value": true,
          "failCode": "CHILD_SCHOOL_START_INELIGIBLE_NOT_VULNERABLE" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 500.00,
        "currency": "MDL"
      },
      "successCode": "CHILD_SCHOOL_START_ELIGIBLE"
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
            NameRo = "Ajutor de început de an școlar",
            NameEn = "Child school-start allowance",
            NameRu = "Помощь к началу учебного года",
            DescriptionRo =
                "Ajutor sezonier acordat familiilor vulnerabile cu copii înscriși în " +
                "instituții de învățământ, la începutul anului școlar.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-CHILD-SCHOOL-START-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
