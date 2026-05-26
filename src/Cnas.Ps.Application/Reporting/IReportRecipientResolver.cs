using System.Collections.Generic;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — expands a <see cref="ReportDistributionRule"/>'s
/// abstract recipient (user / group / role / email / MNotify category) into
/// the concrete list of addresses the channel handler will deliver to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Branching contract.</b> The resolver dispatches by
/// <see cref="ReportDistributionRule.RecipientKind"/>:
/// <list type="bullet">
///   <item><c>User</c> — loads the matching <c>UserProfile</c> and emits one row carrying the user's email (or login).</item>
///   <item><c>Group</c> — loads the group's direct memberships (R2270) and fans out to each member.</item>
///   <item><c>Role</c> — loads every user effectively holding the role (R2274) and fans out one row each.</item>
///   <item><c>EmailAddress</c> — emits ONE row carrying the verbatim email value (already decrypted).</item>
///   <item><c>MNotifyCategory</c> — emits ONE row carrying the category code; MNotify handles fan-out internally.</item>
/// </list>
/// </para>
/// <para>
/// <b>No PII in audit.</b> The resolver does NOT emit audit rows itself — it
/// is consumed by the dispatcher which writes the per-rule outcome row. The
/// individual recipient addresses returned here are PII for the email case
/// and are NEVER persisted outside the encrypted-at-rest dispatch column.
/// </para>
/// </remarks>
public interface IReportRecipientResolver
{
    /// <summary>
    /// Resolves one rule into the concrete recipient list the channel
    /// handler will iterate. Returns an empty list when the resolver finds
    /// no recipients (e.g. an empty group); this is a normal outcome that
    /// the dispatcher records as <c>Skipped</c> with reason
    /// <c>NO_RECIPIENTS_RESOLVED</c>.
    /// </summary>
    /// <param name="rule">The rule to resolve.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the list — empty when no recipients
    /// were found. The implementation does not surface lookup failures as
    /// <see cref="Result{T}.Failure"/> in this iteration; transient I/O errors are
    /// raised to the dispatcher which catches them.
    /// </returns>
    Task<Result<IReadOnlyList<ResolvedRecipientDto>>> ResolveAsync(
        ReportDistributionRule rule,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R1906 / TOR Annex 6 — one resolved recipient emitted by
/// <see cref="IReportRecipientResolver.ResolveAsync"/>.
/// </summary>
/// <param name="Kind">The original rule's recipient kind (carried through for the dispatcher).</param>
/// <param name="DisplayName">Operator-friendly name used in audit logs (never PII for email — render "(redacted)" instead).</param>
/// <param name="Address">
/// Opaque address handed to the channel handler — semantics depend on Kind
/// (email value, MNotify subject, group code, user login).
/// </param>
public sealed record ResolvedRecipientDto(
    ReportRecipientKind Kind,
    string DisplayName,
    string Address);
