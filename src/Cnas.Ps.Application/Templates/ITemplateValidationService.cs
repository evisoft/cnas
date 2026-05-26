using System.Collections.Generic;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Templates;

/// <summary>
/// R0131 / CF 17.15 — metadata-driven per-template validation port. Reads the
/// <c>DocumentTemplate.ValidationRulesJson</c> column for the addressed template code,
/// applies each rule (one at a time) against the supplied form values, and returns the
/// FIRST failure as a <see cref="Result"/> failure with stable code
/// <see cref="ErrorCodes.TemplateValidationFailed"/>. Templates without a rule-set
/// (legacy rows with <c>ValidationRulesJson = null</c>) pass the gate unconditionally.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why first-failure-wins.</b> The metadata service is the wire-level gate consumed
/// by interactive UIs; surfacing every rule violation at once requires a richer error
/// envelope (per-field map) that the rest of the codebase does not yet model. The
/// first-fail contract is documented and stable so a future "report-all" variant lives
/// alongside it without breaking callers.
/// </para>
/// <para>
/// <b>Unknown rule kinds.</b> Rules whose <c>RuleKind</c> string does not match any
/// member of <see cref="Cnas.Ps.Core.ValueObjects.TemplateValidationRuleKind"/> are
/// SILENTLY IGNORED — this keeps a forward-deployed admin upload from breaking a
/// running back-end while a feature flag rolls. The deserialiser logs each ignore at
/// Debug so operators can chart configuration drift.
/// </para>
/// </remarks>
public interface ITemplateValidationService
{
    /// <summary>
    /// Validates the supplied form values against the rule-set persisted on the
    /// <c>DocumentTemplate</c> row whose <c>Code</c> equals
    /// <paramref name="templateCode"/>. Returns <see cref="Result.Success()"/> when the
    /// template carries no rule-set, when every rule passes, or when the only failing
    /// rules are of kind <see cref="Cnas.Ps.Core.ValueObjects.TemplateValidationRuleKind.Custom"/>
    /// (which the metadata service ignores).
    /// </summary>
    /// <param name="templateCode">
    /// Case-insensitive template code (matches <c>DocumentTemplate.Code</c>). An unknown
    /// code is treated as a no-op pass — see remarks on
    /// <see cref="ITemplateValidationService"/>.
    /// </param>
    /// <param name="formValues">
    /// Dictionary of field name → raw string value (the same envelope the renderer
    /// receives). Null values mean "field not supplied".
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the DB lookup.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when every active rule passes; failure with
    /// <see cref="ErrorCodes.TemplateValidationFailed"/> on the first violation. The
    /// failure message identifies the field name + rule kind so the UI can highlight
    /// the offending input.
    /// </returns>
    System.Threading.Tasks.Task<Result> ValidateAsync(
        string templateCode,
        IReadOnlyDictionary<string, string?> formValues,
        System.Threading.CancellationToken cancellationToken = default);
}
