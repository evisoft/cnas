using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Abac;

namespace Cnas.Ps.Infrastructure.Tests.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — tests for the recursive-descent ABAC expression
/// parser. Covers the canonical grammar surface (comparisons, logical
/// operators, calls, identifiers) and the hardening limits (token count,
/// nesting depth, disallowed identifier roots).
/// </summary>
public sealed class AbacExpressionParserTests
{
    private readonly AbacExpressionParser _parser = new();

    private static AbacEvaluationContext ContextWith(
        IReadOnlyDictionary<string, object?>? subject = null,
        IReadOnlyDictionary<string, object?>? resource = null,
        IReadOnlyDictionary<string, object?>? environment = null,
        IReadOnlyDictionary<string, object?>? action = null)
        => new(
            subject ?? new Dictionary<string, object?>(),
            resource ?? new Dictionary<string, object?>(),
            environment ?? new Dictionary<string, object?>(),
            action ?? new Dictionary<string, object?>());

    [Fact]
    public void Parses_SubjectStringEquality()
    {
        var result = _parser.Parse("subject.regionCode == \"MD-CH\"");

        result.IsSuccess.Should().BeTrue();
        var expr = result.Value;
        expr.Evaluate(ContextWith(
            subject: new Dictionary<string, object?> { ["regionCode"] = "MD-CH" })).Should().BeTrue();
        expr.Evaluate(ContextWith(
            subject: new Dictionary<string, object?> { ["regionCode"] = "MD-BC" })).Should().BeFalse();
    }

    [Fact]
    public void Parses_AndOrNotCombination()
    {
        var result = _parser.Parse("(subject.a == \"x\" and subject.b == \"y\") or not subject.c == \"z\"");

        result.IsSuccess.Should().BeTrue();
        // Both AND legs true → true.
        result.Value.Evaluate(ContextWith(
            subject: new Dictionary<string, object?> { ["a"] = "x", ["b"] = "y", ["c"] = "z" })).Should().BeTrue();
        // AND fails, NOT also fails → false.
        result.Value.Evaluate(ContextWith(
            subject: new Dictionary<string, object?> { ["a"] = "x", ["b"] = "n", ["c"] = "z" })).Should().BeFalse();
        // AND fails, NOT succeeds → true.
        result.Value.Evaluate(ContextWith(
            subject: new Dictionary<string, object?> { ["a"] = "x", ["b"] = "n", ["c"] = "w" })).Should().BeTrue();
    }

    [Fact]
    public void Parses_InCall_WithMultipleStringCandidates()
    {
        var result = _parser.Parse("in(subject.role, \"ADMIN\", \"OPERATOR\")");

        result.IsSuccess.Should().BeTrue();
        result.Value.Evaluate(ContextWith(
            subject: new Dictionary<string, object?> { ["role"] = "ADMIN" })).Should().BeTrue();
        result.Value.Evaluate(ContextWith(
            subject: new Dictionary<string, object?> { ["role"] = "GUEST" })).Should().BeFalse();
    }

    [Fact]
    public void Parses_NumericComparison_WithDecimalCoercion()
    {
        var result = _parser.Parse("environment.localHour >= 8 and environment.localHour < 18");

        result.IsSuccess.Should().BeTrue();
        result.Value.Evaluate(ContextWith(
            environment: new Dictionary<string, object?> { ["localHour"] = 10m })).Should().BeTrue();
        result.Value.Evaluate(ContextWith(
            environment: new Dictionary<string, object?> { ["localHour"] = 5m })).Should().BeFalse();
        result.Value.Evaluate(ContextWith(
            environment: new Dictionary<string, object?> { ["localHour"] = 18m })).Should().BeFalse();
    }

    [Fact]
    public void Parses_StartsWithCall_OnResourceIdentifier()
    {
        var result = _parser.Parse("startsWith(resource.code, \"RPT_\")");

        result.IsSuccess.Should().BeTrue();
        result.Value.Evaluate(ContextWith(
            resource: new Dictionary<string, object?> { ["code"] = "RPT_2026" })).Should().BeTrue();
        result.Value.Evaluate(ContextWith(
            resource: new Dictionary<string, object?> { ["code"] = "DOC_2026" })).Should().BeFalse();
    }

    [Fact]
    public void Rejects_DisallowedRootIdentifier()
    {
        var result = _parser.Parse("db.users.password == \"secret\"");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AbacParseError);
        result.ErrorMessage.Should().Contain("disallowed root");
    }

    [Fact]
    public void Rejects_ExpressionExceeding256Tokens()
    {
        // Build a chain of 300 OR clauses — far over the token limit.
        var sb = new StringBuilder("subject.x == \"a\"");
        for (var i = 0; i < 300; i++)
        {
            sb.Append(" or subject.x == \"a\"");
        }
        var result = _parser.Parse(sb.ToString());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AbacParseError);
        result.ErrorMessage.Should().Contain("tokens");
    }

    [Fact]
    public void Rejects_NestingDeeperThan16()
    {
        // Build 20 nested parentheses each carrying a single comparison.
        var sb = new StringBuilder();
        for (var i = 0; i < 20; i++) sb.Append('(');
        sb.Append("subject.x == \"a\"");
        for (var i = 0; i < 20; i++) sb.Append(')');
        var result = _parser.Parse(sb.ToString());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AbacParseError);
        result.ErrorMessage.Should().Contain("depth");
    }
}
