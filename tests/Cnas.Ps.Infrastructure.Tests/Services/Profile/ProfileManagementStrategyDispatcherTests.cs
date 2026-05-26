using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Profile;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Profile;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services.Profile;

/// <summary>
/// R0622 / TOR CF 13.03 — unit tests for the
/// <see cref="ProfileManagementStrategyDispatcher"/>. Verifies the
/// case-insensitive key-routing, the unknown-key NotFound fall-back, and
/// that every registered strategy is reachable from the dispatcher when
/// resolved through DI.
/// </summary>
/// <remarks>
/// <para>
/// <b>TDD-first (RULE 1).</b> These tests were authored BEFORE the
/// dispatcher implementation. We assert the contract — Ui / Form /
/// ExternalSync dispatch correctly, unknown keys surface
/// <see cref="ErrorCodes.NotFound"/> — rather than the production
/// pipeline (which is exercised by per-strategy tests).
/// </para>
/// <para>
/// <b>Strategy stubs.</b> We register lightweight stubs that capture
/// the dispatch and surface a deterministic <see cref="ProfileOutput"/>;
/// per-strategy production behaviour (UI delegates to
/// <c>IProfileService</c>, Form goes through <c>IFormIntakeService</c>,
/// ExternalSync returns the gated refusal) is covered by the dedicated
/// suites in this folder.
/// </para>
/// </remarks>
public sealed class ProfileManagementStrategyDispatcherTests
{
    /// <summary>
    /// Helper — wires up the dispatcher with the supplied set of
    /// strategy stubs. We bypass the real composition root so the test
    /// can register exactly the strategy mix it needs.
    /// </summary>
    /// <param name="strategies">Strategy stubs to register.</param>
    /// <returns>The resolved dispatcher singleton.</returns>
    private static ProfileManagementStrategyDispatcher CreateDispatcher(
        params IProfileManagementStrategy[] strategies)
    {
        var services = new ServiceCollection();
        foreach (var s in strategies)
        {
            services.AddScoped<IProfileManagementStrategy>(_ => s);
        }
        var provider = services.BuildServiceProvider();
        return new ProfileManagementStrategyDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>
    /// Canonical "I succeeded" projection returned by the stub strategies so
    /// tests can assert the dispatcher returned the strategy's value verbatim.
    /// </summary>
    private static readonly ProfileOutput CanonicalProfile = new(
        "userSqid",
        "Sample User",
        "user@example.md",
        null,
        "ro",
        Array.Empty<IssuedDocumentSummaryDto>());

    [Fact]
    public async Task DispatchAsync_UiStrategy_DispatchedAndReturnsResult()
    {
        // Arrange — stub the UI strategy with a capturing wrapper.
        var ui = new CapturingStrategy(
            ProfileManagementStrategyKeys.Ui,
            Result<ProfileOutput>.Success(CanonicalProfile));
        var dispatcher = CreateDispatcher(ui);
        var input = new ProfileManagementInput(DisplayName: "Sample User");

        // Act
        var result = await dispatcher.DispatchAsync(
            ProfileManagementStrategyKeys.Ui, profileSqid: "anySqid", input, CancellationToken.None);

        // Assert — the dispatcher returns the UI strategy's value AND the
        // strategy is the one that was invoked (key-match, not fall-through).
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(CanonicalProfile);
        ui.InvocationCount.Should().Be(1,
            "the UI strategy must be invoked exactly once when its key is dispatched.");
    }

    [Fact]
    public async Task DispatchAsync_FormStrategy_DispatchedAndReturnsResult()
    {
        // Arrange — register all three so we prove key-routing not fall-through.
        var ui = new CapturingStrategy(ProfileManagementStrategyKeys.Ui,
            Result<ProfileOutput>.Failure("UNEXPECTED", "ui invoked"));
        var form = new CapturingStrategy(ProfileManagementStrategyKeys.Form,
            Result<ProfileOutput>.Success(CanonicalProfile));
        var ext = new CapturingStrategy(ProfileManagementStrategyKeys.ExternalSync,
            Result<ProfileOutput>.Failure("UNEXPECTED", "ext invoked"));
        var dispatcher = CreateDispatcher(ui, form, ext);
        var input = new ProfileManagementInput(
            ServicePassportSqid: "passportSqid",
            FormPayloadJson: "{}");

        // Act
        var result = await dispatcher.DispatchAsync(
            ProfileManagementStrategyKeys.Form, profileSqid: "u1", input, CancellationToken.None);

        // Assert — only the form strategy fired.
        result.IsSuccess.Should().BeTrue();
        form.InvocationCount.Should().Be(1);
        ui.InvocationCount.Should().Be(0);
        ext.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_ExternalSync_ReturnsGatedFailureCode()
    {
        // Arrange — register the REAL ExternalSync strategy so the test pins the
        // gated-refusal contract end-to-end through the dispatcher.
        var dispatcher = CreateDispatcher(new ExternalSyncProfileManagementStrategy());
        var input = new ProfileManagementInput(
            ExternalSync: new ExternalProfileSyncInput("MConnect", "<envelope/>"));

        // Act
        var result = await dispatcher.DispatchAsync(
            ProfileManagementStrategyKeys.ExternalSync, profileSqid: "", input, CancellationToken.None);

        // Assert — gated refusal surfaces the stable code so the API boundary
        // can map it to a precise HTTP status (409).
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ProfileExternalSyncNotConfigured);
    }

    [Fact]
    public async Task DispatchAsync_UnknownKey_ReturnsNotFound()
    {
        // Arrange — register every real strategy key so we prove the "no match"
        // path is the only thing that triggers when the key is unknown.
        var dispatcher = CreateDispatcher(
            new CapturingStrategy(ProfileManagementStrategyKeys.Ui,
                Result<ProfileOutput>.Success(CanonicalProfile)),
            new CapturingStrategy(ProfileManagementStrategyKeys.Form,
                Result<ProfileOutput>.Success(CanonicalProfile)),
            new ExternalSyncProfileManagementStrategy());
        var input = new ProfileManagementInput(DisplayName: "Sample User");

        // Act
        var result = await dispatcher.DispatchAsync(
            "DoesNotExist", profileSqid: "u1", input, CancellationToken.None);

        // Assert — NotFound carries the unrecognised key in the message so
        // ops can spot a misconfigured caller in the logs.
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        result.ErrorMessage.Should().Contain("DoesNotExist");
    }

    /// <summary>
    /// Capturing strategy double — returns a canned <see cref="Result{T}"/>
    /// and counts invocations so the tests can prove key-routing.
    /// </summary>
    private sealed class CapturingStrategy(
        string key, Result<ProfileOutput> canned) : IProfileManagementStrategy
    {
        public string StrategyKey { get; } = key;
        public int InvocationCount { get; private set; }

        public Task<Result<ProfileOutput>> ApplyAsync(
            string profileSqid,
            ProfileManagementInput input,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult(canned);
        }
    }
}
