using System.Collections.Frozen;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Notifications;

/// <summary>
/// R0172 / TOR CF 22.05 — default implementation of
/// <see cref="INotificationDeepLinkResolver"/>. Holds a frozen type→route
/// dictionary keyed by the closed vocabulary in
/// <see cref="NotificationRelatedEntityTypes"/>; the route template is a
/// printf-style format string with one <c>{0}</c> slot for the Sqid-encoded
/// id. The resolver Sqid-encodes the id once (using the injected
/// <see cref="ISqidService"/>) and substitutes the encoded suffix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default lifetime: singleton.</b> The frozen dictionary is built once at
/// construction and never mutated; the Sqid service is itself thread-safe so
/// the resolver is safe to share across requests.
/// </para>
/// <para>
/// <b>Forward compatibility.</b> Unknown types yield <c>null</c> rather than
/// throwing — a new type that has not yet shipped through the resolver simply
/// renders as plain text in the inbox until the next deploy.
/// </para>
/// </remarks>
public sealed class NotificationDeepLinkResolver : INotificationDeepLinkResolver
{
    /// <summary>
    /// Frozen <c>OrdinalIgnoreCase</c> map of entity-type string → route
    /// template. Each template contains exactly one <c>{0}</c> placeholder
    /// the resolver substitutes with the Sqid-encoded id.
    /// </summary>
    private static readonly FrozenDictionary<string, string> RouteTemplates =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NotificationRelatedEntityTypes.Application] = "/applications/{0}",
            [NotificationRelatedEntityTypes.Contributor] = "/contributors/{0}",
            [NotificationRelatedEntityTypes.InsuredPerson] = "/insured-persons/{0}",
            [NotificationRelatedEntityTypes.Dossier] = "/dossiers/{0}",
            [NotificationRelatedEntityTypes.WorkflowTask] = "/tasks/{0}",
            [NotificationRelatedEntityTypes.ReportRun] = "/reports/runs/{0}",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly ISqidService _sqids;

    /// <summary>
    /// Constructs the resolver with the central Sqid encoder.
    /// </summary>
    /// <param name="sqids">Shared Sqid encoder/decoder per CLAUDE.md RULE 3.</param>
    public NotificationDeepLinkResolver(ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(sqids);
        _sqids = sqids;
    }

    /// <inheritdoc />
    public string? Resolve(string? entityType, long? entityId)
    {
        // Defensive null/whitespace handling first — these inputs are stored
        // freely on the notification row and we must not throw if the
        // dispatcher omitted them.
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return null;
        }
        if (entityId is not long id || id <= 0L)
        {
            return null;
        }

        if (!RouteTemplates.TryGetValue(entityType, out var template))
        {
            return null;
        }

        var sqid = _sqids.Encode(id);
        // The template is a constant string with exactly one substitution slot,
        // so the format call cannot throw and we don't need invariant culture
        // (the input is a Sqid string, not a culture-sensitive value).
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, sqid);
    }
}
