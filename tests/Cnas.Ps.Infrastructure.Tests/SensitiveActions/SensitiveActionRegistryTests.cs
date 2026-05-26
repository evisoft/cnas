using Cnas.Ps.Application.SensitiveActions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.SensitiveActions;

namespace Cnas.Ps.Infrastructure.Tests.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — tests for <see cref="SensitiveActionRegistry"/>. Verifies the
/// sorted Describe output and the IsKnown lookup.
/// </summary>
public sealed class SensitiveActionRegistryTests
{
    [Fact]
    public void Describe_OrdersEntriesByActionCode()
    {
        var policies = new ISensitiveActionPolicy[]
        {
            new InlinePolicy("ZED.LAST"),
            new InlinePolicy("ALPHA.FIRST"),
            new InlinePolicy("MIDDLE.MIDDLE"),
        };
        var registry = new SensitiveActionRegistry(policies);

        var entries = registry.Describe().ToList();

        entries.Should().HaveCount(3);
        entries.Select(e => e.ActionCode).Should().ContainInOrder(
            "ALPHA.FIRST", "MIDDLE.MIDDLE", "ZED.LAST");
    }

    [Fact]
    public void IsKnown_ReturnsFalseForUnregisteredCode()
    {
        var registry = new SensitiveActionRegistry(new[] { (ISensitiveActionPolicy)new InlinePolicy("KNOWN.OP") });

        registry.IsKnown("KNOWN.OP").Should().BeTrue();
        registry.IsKnown("UNKNOWN.OP").Should().BeFalse();
        registry.IsKnown("").Should().BeFalse();
    }

    /// <summary>Inline test-only policy returning canned values.</summary>
    private sealed class InlinePolicy(string actionCode) : ISensitiveActionPolicy
    {
        public string ActionCode { get; } = actionCode;
        public string DisplayLabel => $"Label for {ActionCode}";
        public TimeSpan? ExpirationOverride => null;
        public Task<Result> ValidatePayloadAsync(string payloadJson, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }
}
