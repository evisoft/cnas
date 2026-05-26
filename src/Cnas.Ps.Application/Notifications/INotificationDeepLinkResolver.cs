namespace Cnas.Ps.Application.Notifications;

/// <summary>
/// R0172 / TOR CF 22.05 — resolves the canonical UI deep-link route for a
/// notification's related business object. Every notification stamped with a
/// <c>RelatedEntityType</c> + <c>RelatedEntityId</c> pair flows through this
/// resolver before the inbox DTO leaves the service layer; the citizen-facing
/// UI then renders the subject as a clickable anchor pointing at the resolved
/// route. Anonymous types (unknown <c>RelatedEntityType</c>, missing id) yield
/// <c>null</c> so the UI falls back to plain text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate resolver.</b> The same notification row may be shown in
/// the Blazor inbox, the dashboard tile, the email mirror, and (eventually) a
/// mobile-app push payload. Centralising the route computation in one place
/// keeps the Sqid encoding consistent across surfaces and gives us a single
/// seam to extend when new business objects gain dedicated pages.
/// </para>
/// <para>
/// <b>Stable type vocabulary.</b> The <c>entityType</c> argument is a closed
/// vocabulary documented on <see cref="NotificationRelatedEntityTypes"/>.
/// Implementations MUST treat unknown values as <c>null</c> (forward
/// compatibility) — never throw — so a future schema with a new type does not
/// break old clients.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> Per CLAUDE.md RULE 3 the resolver Sqid-encodes the
/// raw <see cref="long"/> id before composing the URL. Routes are always
/// returned as plain relative paths (<c>/applications/k3Gq9</c>) so the
/// front-end can append the host without ambiguity.
/// </para>
/// <para>
/// <b>Pure + thread-safe.</b> Implementations are pure functions of their
/// inputs and the Sqid configuration. Default DI lifetime is singleton.
/// </para>
/// </remarks>
public interface INotificationDeepLinkResolver
{
    /// <summary>
    /// Computes the UI route for the supplied entity reference, or <c>null</c>
    /// when the type is unknown / the id is missing.
    /// </summary>
    /// <param name="entityType">
    /// Stable string vocabulary from <see cref="NotificationRelatedEntityTypes"/>.
    /// Comparison is case-insensitive. <c>null</c>, empty, or whitespace
    /// yields <c>null</c>.
    /// </param>
    /// <param name="entityId">
    /// Raw database id of the related business object. <c>null</c> or
    /// non-positive values yield <c>null</c>.
    /// </param>
    /// <returns>
    /// The resolved relative route (e.g. <c>/applications/k3Gq9</c>) or
    /// <c>null</c> when the inputs are insufficient.
    /// </returns>
    string? Resolve(string? entityType, long? entityId);
}

/// <summary>
/// R0172 — closed-vocabulary string constants for the
/// <c>Notification.RelatedEntityType</c> column. Implementations of
/// <see cref="INotificationDeepLinkResolver"/> compare against these values
/// case-insensitively; clients store any of them on the notification row.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary is intentionally small — only entities that have a citizen-
/// facing or staff-facing detail page belong here. New types must:
/// </para>
/// <list type="number">
///   <item>Add a constant here.</item>
///   <item>Add the matching route to the default resolver implementation.</item>
///   <item>Add a unit test proving the route round-trip.</item>
/// </list>
/// </remarks>
public static class NotificationRelatedEntityTypes
{
    /// <summary>Citizen-facing service application detail page (<c>/applications/{sqid}</c>).</summary>
    public const string Application = "Application";

    /// <summary>Staff-facing contributor (plătitor) detail page (<c>/contributors/{sqid}</c>).</summary>
    public const string Contributor = "Contributor";

    /// <summary>Staff-facing insured-person (persoană asigurată) detail page (<c>/insured-persons/{sqid}</c>).</summary>
    public const string InsuredPerson = "InsuredPerson";

    /// <summary>Staff-facing dossier (dosar) detail page (<c>/dossiers/{sqid}</c>).</summary>
    public const string Dossier = "Dossier";

    /// <summary>Staff-facing workflow task detail page (<c>/tasks/{sqid}</c>).</summary>
    public const string WorkflowTask = "WorkflowTask";

    /// <summary>Citizen-facing background report run detail page (<c>/reports/runs/{sqid}</c>).</summary>
    public const string ReportRun = "ReportRun";
}
