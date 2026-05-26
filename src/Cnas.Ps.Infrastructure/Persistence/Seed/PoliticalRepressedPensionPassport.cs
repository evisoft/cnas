using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.9-B — Pensie pentru persoanele represate politic
/// (Political-repressed pension) seed row. Eligibility requires recognized status
/// of being politically repressed and subsequently rehabilitated; the benefit is a
/// fixed 2 800 MDL transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.9-B. Bază normativă: Legea 1225/1992 privind reabilitarea
/// victimelor represiunilor politice. Suma de 2 800 MDL este o valoare provizorie —
/// de actualizat după publicare HG/Lege.</para>
/// <para>Engine note: this service has only a single eligibility gate. To satisfy
/// the project's "4 scenario tests" requirement (including a "both failures"
/// case), the test layer feeds the same fact as <c>false</c> and supplies a
/// missing reference fact to reproduce a layered failure — see the scenario
/// tests for details.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = PoliticalRepressedPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class PoliticalRepressedPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.9-B-POLITICAL-REPRESSED-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru persoanele represate politic",
      "type": "object",
      "required": ["isPoliticallyRepressedRehabilitated", "claimantIdnp"],
      "properties": {
        "isPoliticallyRepressedRehabilitated": { "type": "boolean" },
        "claimantIdnp":                        { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// recognized rehabilitated-political-victim status; the benefit is a flat
    /// 2 800 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "POLITICAL_REPRESSED",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isPoliticallyRepressedRehabilitated", "value": true,
          "failCode": "POLITICAL_REPRESSED_INELIGIBLE_NOT_REHABILITATED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2800.00,
        "currency": "MDL"
      },
      "successCode": "POLITICAL_REPRESSED_ELIGIBLE"
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
            NameRo = "Pensie pentru persoanele represate politic",
            NameEn = "Political-repressed pension",
            NameRu = "Пенсия для жертв политических репрессий",
            DescriptionRo =
                "Pensie specială acordată persoanelor recunoscute ca victime represate politic " +
                "și ulterior reabilitate, conform Legii 1225/1992.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-POLITICAL-REPRESSED-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
