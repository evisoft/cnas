using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0671 continuation — unit tests for the access-scope back-fill input
/// validators. Exercises the RegionCode / SubdivisionCode regexes, the
/// explicit-Sqid cap, and the "at least one selection mechanism" rule.
/// </summary>
public sealed class AccessScopeBackfillInputValidatorTests
{
    /// <summary>Shared empty Sqid list keeps CA1825 quiet across tests.</summary>
    private static readonly string[] EmptySqidList = Array.Empty<string>();

    /// <summary>Shared two-element Sqid list reused across happy-path assertions.</summary>
    private static readonly string[] TwoSqids = ["k3Gq9", "Xm7Yz3"];

    /// <summary>Shared one-element Sqid list reused across single-item assertions.</summary>
    private static readonly string[] OneSqid = ["k3Gq9"];

    // ─────────────────────── Solicitant validator ───────────────────────

    /// <summary>
    /// A valid input — uppercase RegionCode + explicit Sqid list of two ids —
    /// passes the validator unchanged.
    /// </summary>
    [Fact]
    public void Solicitant_ValidInput_Passes()
    {
        var v = new AccessScopeSolicitantBackfillInputValidator();
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            ExplicitSolicitantSqids: TwoSqids);

        var result = v.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// A lowercase RegionCode — <c>"chis"</c> — must be rejected so the
    /// inbound payload can never silently store a non-canonical value.
    /// </summary>
    [Fact]
    public void Solicitant_LowercaseRegionCode_Rejected()
    {
        var v = new AccessScopeSolicitantBackfillInputValidator();
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "chis",
            ExplicitSolicitantSqids: OneSqid);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(AccessScopeSolicitantBackfillInputDto.RegionCode));
    }

    /// <summary>
    /// A 24-char RegionCode exceeds the 16-char cap → rejected.
    /// </summary>
    [Fact]
    public void Solicitant_OverLongRegionCode_Rejected()
    {
        var v = new AccessScopeSolicitantBackfillInputValidator();
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "Chis-12345678901234567890",
            ExplicitSolicitantSqids: OneSqid);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(AccessScopeSolicitantBackfillInputDto.RegionCode));
    }

    /// <summary>
    /// Both <c>Filter</c> and <c>ExplicitSolicitantSqids</c> null → rejected
    /// (the validator demands at least one selection mechanism).
    /// </summary>
    [Fact]
    public void Solicitant_BothSelectionsNull_Rejected()
    {
        var v = new AccessScopeSolicitantBackfillInputValidator();
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS");

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(AccessScopeSolicitantBackfillInputDto.Filter));
    }

    /// <summary>
    /// An empty explicit list counts as "present" — the validator only demands
    /// non-null, leaving the service to deal with the empty-set edge case.
    /// </summary>
    [Fact]
    public void Solicitant_EmptyExplicitListIsAccepted()
    {
        var v = new AccessScopeSolicitantBackfillInputValidator();
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            ExplicitSolicitantSqids: EmptySqidList);

        var result = v.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// 5001 entries in the explicit list exceeds the 5000-cap → rejected.
    /// </summary>
    [Fact]
    public void Solicitant_ExplicitListAboveCap_Rejected()
    {
        var v = new AccessScopeSolicitantBackfillInputValidator();
        var sqids = Enumerable.Range(0, AccessScopeSolicitantBackfillInputValidator.MaxExplicitSqids + 1)
            .Select(i => $"X{i}").ToArray();
        var input = new AccessScopeSolicitantBackfillInputDto(
            RegionCode: "CHIS",
            ExplicitSolicitantSqids: sqids);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    // ─────────────────────── Application validator ───────────────────────

    /// <summary>Happy path — the subdivision validator accepts a canonical code.</summary>
    [Fact]
    public void Application_ValidInput_Passes()
    {
        var v = new AccessScopeApplicationBackfillInputValidator();
        var input = new AccessScopeApplicationBackfillInputDto(
            SubdivisionCode: "CHISINAU-CENTRU",
            ExplicitApplicationSqids: OneSqid);

        var result = v.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>Lowercase SubdivisionCode rejected.</summary>
    [Fact]
    public void Application_LowercaseSubdivisionCode_Rejected()
    {
        var v = new AccessScopeApplicationBackfillInputValidator();
        var input = new AccessScopeApplicationBackfillInputDto(
            SubdivisionCode: "chisinau-centru",
            ExplicitApplicationSqids: OneSqid);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(AccessScopeApplicationBackfillInputDto.SubdivisionCode));
    }
}
