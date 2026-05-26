using System.Collections.Generic;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2271 / TOR SEC 025 — unit tests for the ABAC input validators. Exercises
/// the policy-name regex, the rule-expression parse pre-check, and the
/// reorder / filter envelopes.
/// </summary>
public sealed class AbacInputValidatorTests
{
    /// <summary>Stub parser that always returns success for the supplied expression.</summary>
    private sealed class AlwaysParseSucceedsParser : IAbacExpressionParser
    {
        /// <inheritdoc />
        public Result<AbacExpression> Parse(string source)
            => Result<AbacExpression>.Success(new AbacBoolExpressionStub());
    }

    /// <summary>Stub parser that always returns a fixed failure result.</summary>
    private sealed class AlwaysParseFailsParser : IAbacExpressionParser
    {
        /// <inheritdoc />
        public Result<AbacExpression> Parse(string source)
            => Result<AbacExpression>.Failure(ErrorCodes.AbacParseError, "stub: bad expression.");
    }

    /// <summary>Trivial AST node used by the stub parser.</summary>
    private sealed record AbacBoolExpressionStub : AbacExpression
    {
        /// <inheritdoc />
        public override bool Evaluate(AbacEvaluationContext context) => true;
    }

    [Fact]
    public void RuleSetCreate_HappyPath_Accepted()
    {
        var v = new AbacRuleSetCreateInputValidator();
        var input = new AbacRuleSetCreateInputDto("DOSSIER.READ", "Read dossiers", "Sample", "Deny");

        v.Validate(input).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RuleSetCreate_BadPolicyName_Rejected()
    {
        var v = new AbacRuleSetCreateInputValidator();
        var input = new AbacRuleSetCreateInputDto("badName", "Read", null, null);

        v.Validate(input).IsValid.Should().BeFalse();
    }

    [Fact]
    public void RuleInput_ParseFailure_RejectsExpression()
    {
        var v = new AbacRuleInputValidator(new AlwaysParseFailsParser());
        var input = new AbacRuleInputDto(0, "Allow", "subject.regionCode == \"MD-CH\"", null);

        var result = v.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("failed to parse"));
    }

    [Fact]
    public void RuleInput_ParserSucceeds_AndBoundsOk_Accepted()
    {
        var v = new AbacRuleInputValidator(new AlwaysParseSucceedsParser());
        var input = new AbacRuleInputDto(5, "Allow", "subject.regionCode == \"MD-CH\"", "desc");

        v.Validate(input).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ExpressionTest_HappyPath_Accepted()
    {
        var v = new AbacExpressionTestInputValidator();
        var input = new AbacExpressionTestInputDto(
            "DOSSIER.READ",
            new Dictionary<string, object?> { ["regionCode"] = "MD-CH" },
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>());

        v.Validate(input).IsValid.Should().BeTrue();
    }
}
