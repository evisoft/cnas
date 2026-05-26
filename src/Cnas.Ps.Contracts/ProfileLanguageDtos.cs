namespace Cnas.Ps.Contracts;

/// <summary>
/// R0211 / TOR UI 003 — input shape accepted by <c>PUT /api/profile/language</c>.
/// Carries the canonical two-letter ISO language code the citizen selected in the
/// localisation switcher. The endpoint deliberately accepts a thin payload — a
/// single language field — instead of reusing the broader <see cref="ProfileUpdateInput"/>
/// because the language preference is mutated frequently (every locale toggle)
/// whereas the broader profile update is rare and clears unspecified fields on
/// PUT semantics.
/// </summary>
/// <remarks>
/// <para>
/// The validator enforces the allow-list <c>"ro"</c> / <c>"en"</c> / <c>"ru"</c>
/// case-insensitively; the service normalises to lowercase before persisting.
/// </para>
/// </remarks>
/// <param name="Language">
/// Two-letter ISO language code. Must be one of <c>"ro"</c>, <c>"en"</c>, <c>"ru"</c>
/// (case-insensitive at the boundary; persisted as lowercase).
/// </param>
public sealed record ProfileLanguageInputDto(string Language);
