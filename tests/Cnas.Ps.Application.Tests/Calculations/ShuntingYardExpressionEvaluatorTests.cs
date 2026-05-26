using System.Collections.Generic;
using Cnas.Ps.Application.Calculations;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Tests.Calculations;

/// <summary>
/// R0143 / CF 17.19 — TDD coverage of <see cref="ShuntingYardExpressionEvaluator"/>.
/// Each supported operator + precedence rule + error path is covered.
/// </summary>
public sealed class ShuntingYardExpressionEvaluatorTests
{
    private static readonly IExpressionEvaluator Eval = new ShuntingYardExpressionEvaluator();

    [Fact]
    public void Addition_TwoLiterals_Sums()
    {
        var result = Eval.Evaluate("1 + 2", new Dictionary<string, decimal>());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3m);
    }

    [Fact]
    public void Subtraction_TwoLiterals_Diffs()
    {
        var result = Eval.Evaluate("10 - 4", new Dictionary<string, decimal>());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(6m);
    }

    [Fact]
    public void Multiplication_TwoLiterals_Multiplies()
    {
        var result = Eval.Evaluate("3 * 4", new Dictionary<string, decimal>());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(12m);
    }

    [Fact]
    public void Division_TwoLiterals_Divides()
    {
        var result = Eval.Evaluate("20 / 4", new Dictionary<string, decimal>());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(5m);
    }

    [Fact]
    public void Precedence_MultiplicationBeforeAddition()
    {
        // 2 + 3 * 4 = 14 (multiplication binds tighter)
        var result = Eval.Evaluate("2 + 3 * 4", new Dictionary<string, decimal>());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(14m);
    }

    [Fact]
    public void Parentheses_OverridePrecedence()
    {
        // (2 + 3) * 4 = 20
        var result = Eval.Evaluate("(2 + 3) * 4", new Dictionary<string, decimal>());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(20m);
    }

    [Fact]
    public void UndefinedInput_ReturnsExpressionUnknownInput()
    {
        var result = Eval.Evaluate("base + bonus", new Dictionary<string, decimal>
        {
            ["base"] = 100m,
        });

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ExpressionUnknownInput);
        result.ErrorMessage.Should().Contain("bonus");
    }

    [Fact]
    public void DivideByZero_ReturnsExpressionDivideByZero()
    {
        var result = Eval.Evaluate("10 / 0", new Dictionary<string, decimal>());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ExpressionDivideByZero);
    }

    [Fact]
    public void ComplexExpression_NamedInputsAndPrecedence()
    {
        // monthlyBenefit = base + bonus * 0.1 - deductible
        var result = Eval.Evaluate("base + bonus * 0.1 - deductible",
            new Dictionary<string, decimal>
            {
                ["base"] = 1000m,
                ["bonus"] = 500m,
                ["deductible"] = 50m,
            });

        result.IsSuccess.Should().BeTrue();
        // 1000 + (500 * 0.1) - 50 = 1000 + 50 - 50 = 1000
        result.Value.Should().Be(1000m);
    }

    [Fact]
    public void MalformedExpression_ReturnsExpressionInvalid()
    {
        // Unbalanced parenthesis
        var result = Eval.Evaluate("(1 + 2", new Dictionary<string, decimal>());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ExpressionInvalid);
    }

    [Fact]
    public void EmptyExpression_ReturnsExpressionInvalid()
    {
        var result = Eval.Evaluate("", new Dictionary<string, decimal>());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ExpressionInvalid);
    }
}
