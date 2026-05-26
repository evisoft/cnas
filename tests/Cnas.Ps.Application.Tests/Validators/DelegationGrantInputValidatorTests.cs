using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — pins the validator contract on
/// <see cref="DelegationGrantInputValidator"/> and
/// <see cref="DelegationGrantRevokeInputValidator"/>. Each test isolates one branch so
/// a regression in field bounds or the 90-day window cap fails its dedicated row.
/// </summary>
public sealed class DelegationGrantInputValidatorTests
{
    /// <summary>Stable anchor so the test bounds are independent of wall-clock time.</summary>
    private static readonly DateTime From = new(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Returns a well-formed input — 30-day window with a non-empty scope.</summary>
    private static DelegationGrantInputDto Good() => new(
        DelegateeSqid: "SQID-DELEGATEE",
        ValidFromUtc: From,
        ValidToUtc: From.AddDays(30),
        SuspendsGrantorRights: false,
        Scope: "approve.executory_documents");

    [Fact]
    public void HappyPath_Accepted()
    {
        var v = new DelegationGrantInputValidator();
        v.Validate(Good()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyDelegateeSqid_Rejected()
    {
        var v = new DelegationGrantInputValidator();
        v.Validate(Good() with { DelegateeSqid = string.Empty })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void EmptyScope_Rejected()
    {
        var v = new DelegationGrantInputValidator();
        v.Validate(Good() with { Scope = string.Empty }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void InvertedWindow_Rejected_WithExplicitMessage()
    {
        var v = new DelegationGrantInputValidator();
        var result = v.Validate(Good() with { ValidToUtc = From.AddDays(-1) });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("greater than", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WindowExceeds90Days_Rejected()
    {
        var v = new DelegationGrantInputValidator();
        var result = v.Validate(Good() with { ValidToUtc = From.AddDays(91) });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("90", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WindowEqualToBound_Rejected()
    {
        // Zero-length window has no business meaning.
        var v = new DelegationGrantInputValidator();
        v.Validate(Good() with { ValidToUtc = From }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void RevokeReasonValidator_HappyPath_Accepted()
    {
        var v = new DelegationGrantRevokeInputValidator();
        v.Validate(new DelegationGrantRevokeInputDto("Project handed back to grantor."))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void RevokeReasonValidator_TooShort_Rejected()
    {
        var v = new DelegationGrantRevokeInputValidator();
        v.Validate(new DelegationGrantRevokeInputDto("x")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void RevokeReasonValidator_Empty_Rejected()
    {
        var v = new DelegationGrantRevokeInputValidator();
        v.Validate(new DelegationGrantRevokeInputDto(string.Empty)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void RevokeReasonValidator_TooLong_Rejected()
    {
        var v = new DelegationGrantRevokeInputValidator();
        v.Validate(new DelegationGrantRevokeInputDto(new string('x', 501)))
            .IsValid.Should().BeFalse();
    }
}
