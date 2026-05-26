namespace Cnas.Ps.Web.Resources;

/// <summary>
/// Marker type used as the generic argument for <see cref="Microsoft.Extensions.Localization.IStringLocalizer{T}"/>.
/// Resolves to the <c>Pages.resx</c> bundle under <c>Resources/</c>; per-culture sibling
/// files (<c>Pages.en.resx</c>, <c>Pages.ru.resx</c>) are matched via .NET's standard
/// satellite-resource lookup. Named <see cref="PagesResource"/> rather than <c>Pages</c>
/// to avoid colliding with the <c>Cnas.Ps.Web.Pages</c> namespace.
/// </summary>
public sealed class PagesResource
{
}
