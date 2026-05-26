namespace Cnas.Ps.Infrastructure.Common;

/// <summary>
/// Configuration for <see cref="SqidService"/>. Bound from <c>Sqids:*</c> in app settings.
/// </summary>
/// <remarks>
/// The values here form part of the public contract of the deployed system. Changing
/// <see cref="Alphabet"/> or <see cref="MinLength"/> after launch invalidates every
/// already-published external identifier (links in emails, archived PDFs, third-party
/// integrations). Treat them as immutable per environment.
/// </remarks>
public sealed class SqidOptions
{
    /// <summary>Configuration section name to bind from app settings.</summary>
    public const string SectionName = "Sqids";

    /// <summary>
    /// The alphabet used to encode Sqids. Must be URL-safe; default is a shuffled
    /// alphanumeric alphabet that avoids visually ambiguous characters. Configure
    /// once per environment.
    /// </summary>
    public string Alphabet { get; set; } =
        "FedcbHijklmnoGpqrstuvwxyZ0123456789ABCDEIJKLMNOPQRSTUVWXY";

    /// <summary>
    /// Minimum length of generated Sqid strings. Set to ≥6 so the magnitude of the
    /// underlying primary key cannot be inferred from string length.
    /// </summary>
    public int MinLength { get; set; } = 8;
}
