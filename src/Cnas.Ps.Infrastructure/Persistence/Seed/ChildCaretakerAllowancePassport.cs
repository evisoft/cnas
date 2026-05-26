using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.8-B — Indemnizație pentru îngrijitorul copilului cu
/// dizabilitate (Child-caretaker allowance) seed row. Eligibility requires the
/// claimant to be the recognized caretaker of a disabled child and to be insured;
/// the benefit is a fixed 2 000 MDL transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.8-B. Bază normativă: Legea 499/1999 și HG-urile anuale de
/// indexare. Suma de 2 000 MDL este o valoare provizorie — de actualizat după
/// publicare HG/Lege.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = ChildCaretakerAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ChildCaretakerAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.8-B-CHILD-CARETAKER-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație pentru îngrijitorul copilului cu dizabilitate",
      "type": "object",
      "required": ["caretakerForDisabledChild", "caretakerInsured", "claimantIdnp"],
      "properties": {
        "caretakerForDisabledChild": { "type": "boolean" },
        "caretakerInsured":          { "type": "boolean" },
        "claimantIdnp":              { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// recognized caretaker status and active insurance; the benefit is a flat
    /// 2 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "CHILD_CARETAKER",
      "eligibility": [
        { "rule": "fact-equals", "fact": "caretakerForDisabledChild", "value": true,
          "failCode": "CHILD_CARETAKER_INELIGIBLE_NOT_CARETAKER" },
        { "rule": "fact-equals", "fact": "caretakerInsured", "value": true,
          "failCode": "CHILD_CARETAKER_INELIGIBLE_NOT_INSURED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2000.00,
        "currency": "MDL"
      },
      "successCode": "CHILD_CARETAKER_ELIGIBLE"
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
            NameRo = "Indemnizație pentru îngrijitorul copilului cu dizabilitate",
            NameEn = "Child-caretaker allowance",
            NameRu = "Пособие лицу, ухаживающему за ребёнком-инвалидом",
            DescriptionRo =
                "Indemnizație lunară acordată îngrijitorului asigurat al unui copil cu " +
                "dizabilitate, conform Legii 499/1999.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-CHILD-CARETAKER-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
