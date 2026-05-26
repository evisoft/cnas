using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abstractions;

/// <summary>
/// MCabinet — the Moldovan government's unified citizen dashboard
/// (<c>mcabinet.gov.md</c>). CNAS publishes "dossier event" cards so that a citizen who
/// signs into MCabinet sees their pension-application status, document requests and
/// decision results in one place, without ever opening the CNAS-specific UI.
/// </summary>
/// <remarks>
/// <para>
/// The CNAS-side integration is a thin <b>outbound publisher</b>: when a dossier
/// transitions state (submitted, accepted-for-examination, draft-generated, approved,
/// rejected, closed) the application's dossier state machine posts a card-update to
/// MCabinet's REST API. MCabinet de-duplicates by the tuple
/// <c>(systemCode, externalId)</c>, so re-publishing the same dossier card is safe and
/// idempotent — this lets retries be a no-op upstream (CLAUDE.md cross-cutting:
/// Idempotent Callbacks).
/// </para>
/// <para>
/// <see cref="MCabinetCard.ExternalId"/> MUST be the Sqid of the dossier — never the
/// raw database key — per CLAUDE.md RULE 3 ("Sqids for All External IDs"). Internal
/// callers that hold a <see cref="long"/> dossier id must encode it through
/// <see cref="ISqidService"/> before invoking the publisher.
/// </para>
/// </remarks>
public interface IMCabinetPublisher
{
    /// <summary>
    /// Publishes (creates or updates) a dossier card on MCabinet. Idempotent on
    /// <c>(systemCode, externalId)</c>: republishing the same card replaces the previous
    /// revision rather than producing a duplicate.
    /// </summary>
    /// <param name="card">The card revision to publish.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on any 2xx upstream response;
    /// <see cref="ErrorCodes.MCabinetPublishFailed"/> on transport failure, non-2xx
    /// response, or when the MCabinet base URL is not configured.
    /// </returns>
    Task<Result> PublishCardAsync(MCabinetCard card, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires a previously-published dossier card from MCabinet so the citizen no
    /// longer sees it in their dashboard. Used when a dossier is administratively
    /// withdrawn or its retention window has elapsed.
    /// </summary>
    /// <param name="externalId">
    /// The Sqid of the dossier whose card should be removed. Must match the
    /// <see cref="MCabinetCard.ExternalId"/> used when the card was originally published.
    /// </param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success"/> on a 2xx response (including 204 No Content);
    /// <see cref="ErrorCodes.MCabinetPublishFailed"/> on transport failure, non-2xx
    /// response, or when the MCabinet base URL is not configured.
    /// </returns>
    Task<Result> RetireCardAsync(string externalId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A single dossier-card revision pushed to MCabinet. Every field is forwarded verbatim
/// in the outbound JSON body; the publisher does not transform values beyond ISO-8601
/// serialisation of <see cref="EventUtc"/>.
/// </summary>
/// <param name="ExternalId">
/// Sqid of the underlying dossier. Stable across the dossier's lifetime — successive
/// card revisions for the same dossier share this id so MCabinet treats them as updates
/// of one logical card rather than a new card per state transition.
/// </param>
/// <param name="CitizenIdnp">
/// IDNP of the dossier owner (the citizen who will see the card in their MCabinet
/// dashboard). MCabinet uses this to route the card to the correct citizen account.
/// </param>
/// <param name="ServiceCode">
/// Stable CNAS service-passport code identifying which benefit / process the dossier
/// belongs to (e.g. <c>UC03.OldAgePension</c>). Matches
/// <see cref="Cnas.Ps.Core.Domain.ServicePassport.Code"/>.
/// </param>
/// <param name="Status">Current dossier status; rendered in the citizen-facing card.</param>
/// <param name="TitleRo">Short Romanian title shown as the card heading (e.g. the benefit name).</param>
/// <param name="SubtitleRo">
/// Optional Romanian subtitle shown below the title (e.g. dossier short reference such
/// as <c>D-2026-ABCD</c>). Null when no secondary line is desired.
/// </param>
/// <param name="EventUtc">
/// UTC timestamp at which this card revision was produced. Used by MCabinet to order
/// the citizen's dashboard cards and to detect stale revisions.
/// </param>
/// <param name="DeepLink">
/// Optional deep link the citizen can click to "view full" details in the CNAS UI.
/// Null when no public CNAS-side view exists yet.
/// </param>
public sealed record MCabinetCard(
    string ExternalId,
    string CitizenIdnp,
    string ServiceCode,
    MCabinetStatus Status,
    string TitleRo,
    string? SubtitleRo,
    DateTime EventUtc,
    Uri? DeepLink);

/// <summary>
/// Citizen-facing dossier status rendered on the MCabinet card. The enum value is
/// serialised by name (e.g. <c>"InExamination"</c>) in the outbound JSON so that
/// MCabinet can present an i18n-able label without coupling to CNAS internal codes.
/// </summary>
public enum MCabinetStatus
{
    /// <summary>The citizen has submitted an application; intake has not started yet.</summary>
    Submitted,

    /// <summary>The dossier has been accepted for examination by a CNAS clerk.</summary>
    InExamination,

    /// <summary>A draft decision has been generated and is awaiting approval.</summary>
    DraftReady,

    /// <summary>The dossier has been approved (benefit will be granted).</summary>
    Approved,

    /// <summary>The dossier has been rejected (benefit will not be granted).</summary>
    Rejected,

    /// <summary>The dossier is closed (final state after Approved / Rejected and post-processing).</summary>
    Closed,
}
