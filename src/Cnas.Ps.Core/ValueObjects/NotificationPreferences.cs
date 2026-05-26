using System.Text.Json;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// Per-user opt-in/opt-out flags for notification channels. Persisted as JSON on
/// <c>UserProfile.NotificationPreferences</c>. CF 22.02 / CF 04.08 (R0171).
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispatch contract.</b> The top-level per-channel flags
/// (<see cref="Email"/>, <see cref="Sms"/>, <see cref="InApp"/>) are honoured by
/// <c>NotificationService.EnqueueAsync</c> at dispatch time: when the channel is
/// opted out, the row is still persisted (so the citizen has a record) with
/// <see cref="NotificationDeliveryStatus.Suppressed"/> and NO actual send occurs.
/// </para>
/// <para>
/// <b>Categories are reserved for R0173.</b> The <see cref="Categories"/> dictionary
/// is persisted and accepted on writes but NOT consulted at dispatch in R0171 —
/// per-workflow notification strategy is the scope of the follow-up batch. The
/// dictionary is case-insensitive on keys; unknown keys are accepted.
/// </para>
/// <para>
/// <b>Default is opt-in.</b> <see cref="Default"/> opts every channel IN; new users
/// start with this. The default is computed in code (not in the DB column) so the
/// column can be nullable JSONB. Existing rows with NULL therefore behave exactly
/// like new ones — back-fill is implicit.
/// </para>
/// <para>
/// <b>Fail-open on unknown channels.</b> <see cref="IsAllowed(NotificationChannel)"/>
/// returns <c>true</c> for any future <see cref="NotificationChannel"/> value not
/// covered by the switch. The dispatcher must NEVER silently drop a notification
/// just because the preference schema is older than the channel enum.
/// </para>
/// </remarks>
public sealed record NotificationPreferences
{
    /// <summary>Opt-in flag for the Email channel (default <c>true</c>).</summary>
    public bool Email { get; init; } = true;

    /// <summary>Opt-in flag for the SMS channel (default <c>true</c>).</summary>
    public bool Sms { get; init; } = true;

    /// <summary>Opt-in flag for the in-app inbox channel (default <c>true</c>).</summary>
    public bool InApp { get; init; } = true;

    /// <summary>
    /// Per-category opt-in flags reserved for R0173 (per-workflow notification strategy).
    /// Keys are case-insensitive; values map a category code (e.g. <c>applicationStatus</c>,
    /// <c>marketing</c>) to a boolean. NOT consulted at dispatch in R0171.
    /// </summary>
    public Dictionary<string, bool> Categories { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a fresh fully-opted-in preference instance. New users start with this
    /// (so the system errs on the side of communicating); the citizen explicitly opts
    /// out by issuing a <c>PUT /api/profile/notification-preferences</c>.
    /// </summary>
    public static NotificationPreferences Default => new();

    /// <summary>
    /// Decides whether the supplied <paramref name="channel"/> is currently allowed
    /// for this user. Unknown channels fail OPEN — see the type remarks.
    /// </summary>
    /// <param name="channel">Channel the dispatcher is about to use.</param>
    /// <returns>
    /// <c>true</c> when the per-channel flag is set; <c>true</c> for any channel not
    /// covered by the switch (fail-open default). The dispatcher must persist a
    /// <see cref="NotificationDeliveryStatus.Suppressed"/> row when this returns
    /// <c>false</c> and skip the actual channel send.
    /// </returns>
    public bool IsAllowed(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => Email,
        NotificationChannel.Sms => Sms,
        NotificationChannel.InApp => InApp,
        _ => true, // fail-open for unknown channels — see type remarks.
    };
}

/// <summary>
/// Serialisation helpers for <see cref="NotificationPreferences"/>. Kept beside the
/// value object so callers have ONE import for the whole preference subsystem.
/// </summary>
/// <remarks>
/// <para>
/// The helpers use a <see cref="JsonSerializerOptions"/> instance with
/// <c>PropertyNamingPolicy = CamelCase</c> so the JSON on disk matches the wire
/// shape consumed by the React/Blazor front-end without an extra naming policy on
/// the API serializer.
/// </para>
/// <para>
/// <b>Fail-open parse.</b> <see cref="Parse(string)"/> returns
/// <see cref="NotificationPreferences.Default"/> for null/empty/malformed input
/// rather than throwing. The dispatcher contract is "send by default" — a parse
/// error must NEVER silently drop notifications. The malformed JSON is therefore
/// equivalent to "no preferences set yet" (which itself means default-opt-in).
/// </para>
/// </remarks>
public static class NotificationPreferencesJson
{
    /// <summary>Shared serializer options — camelCase property names, compact output.</summary>
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Parses the persisted JSON column into a <see cref="NotificationPreferences"/>
    /// instance. NULL, empty, whitespace or malformed input returns
    /// <see cref="NotificationPreferences.Default"/> (fail-open contract).
    /// </summary>
    /// <param name="json">Raw JSON column value, or <c>null</c>.</param>
    /// <returns>Parsed preferences, or the fully-opted-in default on any failure.</returns>
    public static NotificationPreferences Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return NotificationPreferences.Default;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<NotificationPreferences>(json, Opts);
            return parsed ?? NotificationPreferences.Default;
        }
        catch (JsonException)
        {
            // Fail-open per the type remarks — malformed JSON must NOT prevent dispatch.
            return NotificationPreferences.Default;
        }
    }

    /// <summary>
    /// Serialises a <see cref="NotificationPreferences"/> instance to its canonical
    /// camelCase JSON form for persistence on the <c>UserProfile</c> column.
    /// </summary>
    /// <param name="prefs">Preferences to serialise (must be non-null).</param>
    /// <returns>Compact UTF-16 JSON string.</returns>
    public static string Serialize(NotificationPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        return JsonSerializer.Serialize(prefs, Opts);
    }
}
