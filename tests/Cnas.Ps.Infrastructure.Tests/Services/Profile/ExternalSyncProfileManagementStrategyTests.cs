using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Profile;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Profile;

namespace Cnas.Ps.Infrastructure.Tests.Services.Profile;

/// <summary>
/// R0622 / TOR CF 13.03 — tests for
/// <see cref="ExternalSyncProfileManagementStrategy"/>. The stub
/// implementation refuses every call with
/// <see cref="ErrorCodes.ProfileExternalSyncNotConfigured"/> while
/// MConnect remains externally gated (EGOV-INTEGRATION-GAP §MConnect),
/// but still surfaces precise <see cref="ErrorCodes.ValidationFailed"/>
/// when the envelope is malformed so dashboards can distinguish "gated
/// adapter attempted" from "bad caller payload".
/// </summary>
public sealed class ExternalSyncProfileManagementStrategyTests
{
    [Fact]
    public async Task ApplyAsync_WellFormedEnvelope_ReturnsGatedRefusalCode()
    {
        // Arrange
        var sut = new ExternalSyncProfileManagementStrategy();
        var input = new ProfileManagementInput(
            ExternalSync: new ExternalProfileSyncInput("MConnect", "<envelope/>"));

        // Act
        var result = await sut.ApplyAsync(
            profileSqid: "ignored", input, CancellationToken.None);

        // Assert — strategy key + stable gated refusal code surfaced verbatim.
        sut.StrategyKey.Should().Be(ProfileManagementStrategyKeys.ExternalSync);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ProfileExternalSyncNotConfigured);
        result.ErrorMessage.Should().Contain("MConnect",
            "the refusal message must name the requested source so ops can locate the caller.");
    }

    [Fact]
    public async Task ApplyAsync_MissingEnvelope_ReturnsValidationFailed()
    {
        // Arrange — caller forgot to populate the envelope altogether.
        var sut = new ExternalSyncProfileManagementStrategy();
        var input = new ProfileManagementInput(); // ExternalSync = null

        // Act
        var result = await sut.ApplyAsync(
            profileSqid: "u1", input, CancellationToken.None);

        // Assert — precise validation code distinguishes "bad caller" from "gated".
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task ApplyAsync_BlankSourceSystem_ReturnsValidationFailed()
    {
        // Arrange — envelope present but source-system blank.
        var sut = new ExternalSyncProfileManagementStrategy();
        var input = new ProfileManagementInput(
            ExternalSync: new ExternalProfileSyncInput(SourceSystem: "  ", Payload: "<p/>"));

        // Act
        var result = await sut.ApplyAsync(
            profileSqid: "u1", input, CancellationToken.None);

        // Assert — precise validation code rather than the gated refusal.
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }
}
