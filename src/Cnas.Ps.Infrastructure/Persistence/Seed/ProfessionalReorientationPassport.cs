using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.6-B — Indemnizație pentru reorientare profesională
/// (Professional reorientation allowance) seed row. Eligibility requires the
/// claimant to be registered with SI AISSS and to have a positive commission
/// approval for the reorientation plan; the benefit is a fixed 1 500 MDL.
/// </summary>
/// <remarks>
/// <para>TOR §3.6-B. Bază normativă: Legea 105/2018 — art. 22 (măsuri active de
/// ocupare prin formare profesională).</para>
/// <para>The 1 500 MDL fixed value is a reasonable Moldovan default — valoare
/// provizorie, de tunat după publicarea HG aferente.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = ProfessionalReorientationPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ProfessionalReorientationPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.6-B-PROFESSIONAL-REORIENTATION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație pentru reorientare profesională",
      "type": "object",
      "required": ["registeredAtSiAisss", "reorientationApprovedByCommission", "claimantIdnp"],
      "properties": {
        "registeredAtSiAisss":               { "type": "boolean" },
        "reorientationApprovedByCommission": { "type": "boolean" },
        "claimantIdnp":                      { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// registration at SI AISSS and a positive reorientation commission approval;
    /// the benefit is a fixed 1 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "PROFESSIONAL_REORIENTATION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "registeredAtSiAisss", "value": true,
          "failCode": "PROFESSIONAL_REORIENTATION_INELIGIBLE_NOT_REGISTERED" },
        { "rule": "fact-equals", "fact": "reorientationApprovedByCommission", "value": true,
          "failCode": "PROFESSIONAL_REORIENTATION_INELIGIBLE_NOT_APPROVED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1500.00,
        "currency": "MDL"
      },
      "successCode": "PROFESSIONAL_REORIENTATION_ELIGIBLE"
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
            NameRo = "Indemnizație pentru reorientare profesională",
            NameEn = "Professional reorientation allowance",
            NameRu = "Пособие на профессиональную переориентацию",
            DescriptionRo =
                "Indemnizație acordată șomerilor care urmează un program de reorientare " +
                "profesională aprobat de comisia ANOFM, conform Legii 105/2018.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-PROFESSIONAL-REORIENTATION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
