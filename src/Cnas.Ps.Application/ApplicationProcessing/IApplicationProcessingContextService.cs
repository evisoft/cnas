using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ApplicationProcessing;

/// <summary>
/// R0701 / TOR CF 21.01-02 — single-payload "application processing context"
/// service. Returns everything a CNAS staff user (Utilizator CNAS) needs to
/// process one application in a single round-trip:
/// <list type="bullet">
///   <item>The applicant's profile + linked entities (current address / contact
///         / civil-status row + recent activity periods, from R0301/R0311).</item>
///   <item>Workflow tasks for this application whose status is
///         Pending / InProgress / Overdue.</item>
///   <item>Decision drafts (i.e. unsigned <c>Document</c> rows of
///         <c>Kind=Decision</c>).</item>
///   <item>Top 20 attachments owned by the application (newest first).</item>
///   <item>Last 50 audit-log rows scoped to this application.</item>
///   <item>Heuristic-derived suggested-next-action codes.</item>
///   <item>A boolean indicator that the R0552 pre-fill service has candidate
///         data the application has not yet absorbed.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Server primitive, not the UI.</b> The future CNAS staff processing UI
/// (TOR R0701) is deferred to a Blazor batch; this service is the read-only
/// server primitive the UI will eventually call. Shipping the primitive
/// independently of the UI lets the API surface land first and gives the UI
/// a stable contract to build against.
/// </para>
/// <para>
/// <b>Permission model.</b> Authorisation is multi-pronged at the service
/// layer: the caller may reach the dossier when they (a) hold the
/// <see cref="ProcessPermission"/> permission code, OR (b) are the application's
/// current assigned examiner (via the Dossier.<c>AssignedExaminerId</c> link),
/// OR (c) hold the <c>cnas-admin</c> role. Anonymous callers and citizens are
/// always rejected with <see cref="ErrorCodes.Forbidden"/>. The API-level
/// <c>[Authorize]</c> attribute on the controller gates the inbound surface;
/// this service-side check is defense-in-depth.
/// </para>
/// <para>
/// <b>Audit.</b> Every successful call writes one Sensitive
/// <c>APPLICATION.PROCESSING_CONTEXT_VIEWED</c> audit row carrying the
/// application Sqid + the list of high-level field groups loaded
/// (<c>viewedFields</c>). The whole dossier opening up = audit trail.
/// </para>
/// <para>
/// <b>What is deferred.</b> The Blazor staff processing screen itself (TOR
/// R0701 UI batch), per-page contextual quick actions, and the in-page
/// workflow-advance buttons all ship in a follow-up batch. This service
/// returns the data — the UI binds it.
/// </para>
/// </remarks>
public interface IApplicationProcessingContextService
{
    /// <summary>
    /// Stable permission code that, by itself, satisfies the service's
    /// authorisation gate. Holders of this permission are typically the
    /// <c>cnas-user</c> role with explicit processing rights granted via
    /// the user-administration UI; admins satisfy the gate transparently
    /// through the role check below.
    /// </summary>
    public const string ProcessPermission = "Application.Process";

    /// <summary>
    /// Stable audit event code emitted on every successful invocation.
    /// </summary>
    public const string AuditEventCode = "APPLICATION.PROCESSING_CONTEXT_VIEWED";

    /// <summary>
    /// Loads the processing-context payload for the supplied application.
    /// </summary>
    /// <param name="applicationId">
    /// Raw bigint id of the target <c>ServiceApplication</c>. Decoding the
    /// inbound Sqid is the controller's responsibility — by the time this
    /// method is invoked the id has already been decoded so the service can
    /// stay focused on the aggregation logic.
    /// </param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the populated <see cref="ApplicationProcessingContextDto"/>;
    /// when the caller is anonymous <see cref="ErrorCodes.Unauthorized"/>;
    /// when the application does not exist <see cref="ErrorCodes.NotFound"/>;
    /// when the caller lacks the processing permission AND is not the assigned
    /// examiner AND is not an admin <see cref="ErrorCodes.Forbidden"/>.
    /// </returns>
    Task<Result<ApplicationProcessingContextDto>> GetForCurrentUserAsync(
        long applicationId,
        CancellationToken ct = default);
}
