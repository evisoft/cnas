using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using FluentAssertions;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0103 / TOR CF 14.02 — unit tests for the integration-event deduper input
/// validators. Pinned by RULE 1 (tests first) before the implementation
/// shipped.
/// </summary>
public class IntegrationEventDedupValidatorTests
{
    private readonly IntegrationEventDedupClaimArgsValidator _claim = new();
    private readonly IntegrationEventDedupMarkFailedArgsValidator _markFailed = new();

    [Fact]
    public void ClaimValidator_Accepts_TypicalEnvelope()
    {
        var args = new IntegrationEventDedupClaimArgs(
            MessageId: "msg-001",
            Source: "cnas-ps",
            Type: "md.cnas.ps.decision.issued.v1");

        var result = _claim.Validate(args);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ClaimValidator_Rejects_EmptyMessageId()
    {
        var args = new IntegrationEventDedupClaimArgs(string.Empty, "cnas-ps", "md.cnas.ps.test.v1");

        var result = _claim.Validate(args);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == ErrorCodes.ValidationFailed
            && e.PropertyName == nameof(IntegrationEventDedupClaimArgs.MessageId));
    }

    [Fact]
    public void ClaimValidator_Rejects_MessageId_Longer_Than_128_Chars()
    {
        var oversized = new string('x', 129);
        var args = new IntegrationEventDedupClaimArgs(oversized, "cnas-ps", "md.cnas.ps.test.v1");

        var result = _claim.Validate(args);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == ErrorCodes.ValidationFailed
            && e.PropertyName == nameof(IntegrationEventDedupClaimArgs.MessageId));
    }

    [Fact]
    public void ClaimValidator_Rejects_Source_Longer_Than_256_Chars()
    {
        var oversized = new string('s', 257);
        var args = new IntegrationEventDedupClaimArgs("msg-001", oversized, "md.cnas.ps.test.v1");

        var result = _claim.Validate(args);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IntegrationEventDedupClaimArgs.Source));
    }

    [Fact]
    public void ClaimValidator_Rejects_Type_Longer_Than_256_Chars()
    {
        var oversized = new string('t', 257);
        var args = new IntegrationEventDedupClaimArgs("msg-001", "cnas-ps", oversized);

        var result = _claim.Validate(args);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IntegrationEventDedupClaimArgs.Type));
    }

    [Fact]
    public void MarkFailedValidator_Accepts_TypicalReason()
    {
        var args = new IntegrationEventDedupMarkFailedArgs("msg-001", "downstream handler threw");

        var result = _markFailed.Validate(args);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void MarkFailedValidator_Rejects_EmptyFailureReason()
    {
        var args = new IntegrationEventDedupMarkFailedArgs("msg-001", string.Empty);

        var result = _markFailed.Validate(args);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IntegrationEventDedupMarkFailedArgs.FailureReason));
    }

    [Fact]
    public void MarkFailedValidator_Rejects_FailureReason_Longer_Than_1000_Chars()
    {
        var oversized = new string('r', 1001);
        var args = new IntegrationEventDedupMarkFailedArgs("msg-001", oversized);

        var result = _markFailed.Validate(args);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IntegrationEventDedupMarkFailedArgs.FailureReason));
    }
}
