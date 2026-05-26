using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Core.Tests.ValueObjects;

/// <summary>
/// Unit tests for <see cref="NotificationPreferences"/> and its companion JSON
/// helper <see cref="NotificationPreferencesJson"/> (R0171, CF 22.02 / CF 04.08).
/// </summary>
/// <remarks>
/// <para>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the production code. They
/// pin down three invariants:
/// </para>
/// <list type="bullet">
///   <item><see cref="NotificationPreferences.Default"/> opts every channel IN (a
///   new user must receive notifications until they explicitly opt out).</item>
///   <item><see cref="NotificationPreferences.IsAllowed(NotificationChannel)"/>
///   returns the per-channel flag, and fails OPEN for any future enum value.</item>
///   <item>The JSON helper round-trips camelCase, treats null/malformed input as
///   default-opt-in (the dispatcher's fail-open contract), and never throws.</item>
/// </list>
/// </remarks>
public sealed class NotificationPreferencesTests
{
    [Fact]
    public void Default_OptsInAllChannels()
    {
        var prefs = NotificationPreferences.Default;

        prefs.Email.Should().BeTrue();
        prefs.Sms.Should().BeTrue();
        prefs.InApp.Should().BeTrue();
        prefs.Categories.Should().BeEmpty();
    }

    [Theory]
    [InlineData(NotificationChannel.Email, true, false, false, true)]
    [InlineData(NotificationChannel.Sms, false, true, false, true)]
    [InlineData(NotificationChannel.InApp, false, false, true, true)]
    [InlineData(NotificationChannel.Email, false, true, true, false)]
    [InlineData(NotificationChannel.Sms, true, false, true, false)]
    [InlineData(NotificationChannel.InApp, true, true, false, false)]
    public void IsAllowed_FollowsFlagPerChannel(
        NotificationChannel channel, bool email, bool sms, bool inApp, bool expected)
    {
        var prefs = new NotificationPreferences { Email = email, Sms = sms, InApp = inApp };

        prefs.IsAllowed(channel).Should().Be(expected);
    }

    [Fact]
    public void IsAllowed_UnknownChannel_DefaultsToTrue()
    {
        // Fail-open for any enum value not (yet) covered by the switch — a hypothetical
        // future channel must not be silently dropped.
        var prefs = new NotificationPreferences { Email = false, Sms = false, InApp = false };

        prefs.IsAllowed((NotificationChannel)999).Should().BeTrue(
            "unknown channels must fail open — never silently drop messages.");
    }

    [Fact]
    public void Json_Parse_OnNull_ReturnsDefault()
    {
        var prefs = NotificationPreferencesJson.Parse(null);

        prefs.Should().BeEquivalentTo(NotificationPreferences.Default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Json_Parse_OnEmptyOrWhitespace_ReturnsDefault(string json)
    {
        var prefs = NotificationPreferencesJson.Parse(json);

        prefs.Should().BeEquivalentTo(NotificationPreferences.Default);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("[]")] // wrong root shape — array, not object
    [InlineData("\"hello\"")] // string root
    public void Json_Parse_OnMalformed_ReturnsDefault(string json)
    {
        var prefs = NotificationPreferencesJson.Parse(json);

        // Fail-open: the dispatcher must NOT drop notifications because of bad JSON.
        prefs.Should().NotBeNull();
        prefs.Email.Should().BeTrue();
        prefs.Sms.Should().BeTrue();
        prefs.InApp.Should().BeTrue();
    }

    [Fact]
    public void Json_RoundTrips_CamelCase()
    {
        var original = new NotificationPreferences
        {
            Email = false,
            Sms = true,
            InApp = false,
            Categories = new(StringComparer.OrdinalIgnoreCase)
            {
                ["applicationStatus"] = true,
                ["marketing"] = false,
            },
        };

        var json = NotificationPreferencesJson.Serialize(original);
        var roundTripped = NotificationPreferencesJson.Parse(json);

        // Property names must be camelCase on disk so the JSON is consumable by the
        // React/Blazor front-end without an additional naming policy on its side.
        json.Should().Contain("\"email\":false");
        json.Should().Contain("\"sms\":true");
        json.Should().Contain("\"inApp\":false");
        json.Should().Contain("\"categories\":");

        roundTripped.Email.Should().Be(original.Email);
        roundTripped.Sms.Should().Be(original.Sms);
        roundTripped.InApp.Should().Be(original.InApp);
        roundTripped.Categories.Should().ContainKey("applicationStatus").WhoseValue.Should().BeTrue();
        roundTripped.Categories.Should().ContainKey("marketing").WhoseValue.Should().BeFalse();
    }

    [Fact]
    public void Categories_KeyLookup_IsCaseInsensitive()
    {
        // The dictionary is constructed with OrdinalIgnoreCase so callers don't have to
        // worry about the exact casing of category keys when reading the value object.
        var prefs = new NotificationPreferences
        {
            Categories = new(StringComparer.OrdinalIgnoreCase) { ["ApplicationStatus"] = true },
        };

        prefs.Categories.Should().ContainKey("applicationstatus");
        prefs.Categories.Should().ContainKey("APPLICATIONSTATUS");
    }
}
