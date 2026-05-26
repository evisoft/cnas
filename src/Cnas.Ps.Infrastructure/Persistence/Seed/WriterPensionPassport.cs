using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.15-B — Pensie pentru scriitor profesionist
/// (Professional-writer pension) seed row. Eligibility requires the claimant to
/// be a recognized professional writer and to be acknowledged by the Writers'
/// Union; the benefit is a fixed monthly amount.
/// </summary>
/// <remarks>
/// <para>TOR §3.15-B. Bază normativă: Legea 156/1998 art. 47 (privind pensiile
/// pentru anumite categorii). The 3 200 MDL fixed amount is a Moldovan default
/// — valoare provizorie, de actualizat la indexarea anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = WriterPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class WriterPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.15-B-WRITER-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie scriitor profesionist",
      "type": "object",
      "required": ["wasProfessionalWriter", "recognitionByUnion", "claimantIdnp"],
      "properties": {
        "wasProfessionalWriter": { "type": "boolean" },
        "recognitionByUnion":    { "type": "boolean" },
        "claimantIdnp":          { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// professional-writer status and Writers' Union recognition; the benefit is
    /// a fixed 3 200 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "WRITER_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasProfessionalWriter", "value": true,
          "failCode": "WRITER_PENSION_INELIGIBLE_NOT_WRITER" },
        { "rule": "fact-equals", "fact": "recognitionByUnion", "value": true,
          "failCode": "WRITER_PENSION_INELIGIBLE_NOT_RECOGNIZED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 3200.00,
        "currency": "MDL"
      },
      "successCode": "WRITER_PENSION_ELIGIBLE"
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
            NameRo = "Pensie scriitor profesionist",
            NameEn = "Professional-writer pension",
            NameRu = "Пенсия профессиональному писателю",
            DescriptionRo =
                "Pensie lunară acordată scriitorilor profesioniști recunoscuți de Uniunea " +
                "Scriitorilor, conform Legii 156/1998 art. 47.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-WRITER-PENSION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
