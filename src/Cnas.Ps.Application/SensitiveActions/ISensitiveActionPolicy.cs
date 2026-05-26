using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — per-action policy hook plugged into the generic 4-eyes
/// substrate. One implementation per concrete sensitive action (USER.ROLE_GRANT,
/// EXECUTORY_DOC.CANCEL, …). The substrate consults it at request time to validate the
/// payload shape and to tune the expiration window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Policies are stateless; they may inject dependencies for cross-payload
/// validation (e.g. checking that a referenced user exists). They MUST NOT mutate
/// domain state — that is the handler's responsibility.
/// </para>
/// <para>
/// <b>Registration.</b> Use the <c>AddSensitiveActionPolicy&lt;TPolicy&gt;()</c>
/// extension. The registry returns every registered policy in <c>Describe()</c>; the
/// service consults <c>ValidatePayloadAsync</c> on every request.
/// </para>
/// </remarks>
public interface ISensitiveActionPolicy
{
    /// <summary>Stable SCREAMING_SNAKE_CASE action code this policy handles.</summary>
    string ActionCode { get; }

    /// <summary>Short human-readable label suitable for picker UI / audit details.</summary>
    string DisplayLabel { get; }

    /// <summary>
    /// Optional per-action expiration window overriding the substrate default (72 h).
    /// <c>null</c> means "use the default".
    /// </summary>
    TimeSpan? ExpirationOverride { get; }

    /// <summary>
    /// Inspects the proposed payload JSON and either accepts it (returning
    /// <see cref="Result.Success"/>) or rejects it with a structured failure. The
    /// substrate calls this from <c>ISensitiveAdminActionService.RequestAsync</c> before
    /// persisting the row.
    /// </summary>
    /// <param name="payloadJson">Raw JSON supplied by the requester.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Success when the payload matches the action's schema; failure otherwise.</returns>
    Task<Result> ValidatePayloadAsync(string payloadJson, CancellationToken ct = default);
}
