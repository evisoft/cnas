namespace Cnas.Ps.Contracts;

/// <summary>Public content card surfaced on UC01 — opaque Id, no PII.</summary>
/// <param name="Id">Sqid-encoded identifier.</param>
/// <param name="Title">Localised title.</param>
/// <param name="Summary">Short summary (≤ 240 chars).</param>
/// <param name="Category">Domain category (services, registries, reports).</param>
/// <param name="UpdatedAtUtc">UTC date of the last update.</param>
public sealed record PublicContentCard(
    string Id,
    string Title,
    string Summary,
    string Category,
    DateTime UpdatedAtUtc);
