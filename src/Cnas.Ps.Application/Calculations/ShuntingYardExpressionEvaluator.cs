using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Calculations;

/// <summary>
/// R0143 / CF 17.19 — hand-rolled Shunting-yard implementation of
/// <see cref="IExpressionEvaluator"/>. Tokenises the input, converts it to RPN
/// (reverse Polish notation) via Dijkstra's algorithm, then evaluates the RPN with a
/// stack. Tiny by design: no dynamic-code surface, no third-party expression library.
/// </summary>
/// <remarks>
/// <para>
/// Stateless and thread-safe; register as a singleton.
/// </para>
/// </remarks>
public sealed class ShuntingYardExpressionEvaluator : IExpressionEvaluator
{
    /// <summary>
    /// Token discriminator emitted by <see cref="Tokenize"/>.
    /// </summary>
    private enum TokenKind
    {
        /// <summary>A decimal numeric literal (e.g. <c>3.14</c>).</summary>
        Number,

        /// <summary>A named-input identifier (e.g. <c>base</c>).</summary>
        Identifier,

        /// <summary>One of <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>.</summary>
        Operator,

        /// <summary>Unary minus — disambiguated from binary minus at tokenisation time.</summary>
        UnaryMinus,

        /// <summary>Open parenthesis <c>(</c>.</summary>
        OpenParen,

        /// <summary>Close parenthesis <c>)</c>.</summary>
        CloseParen,
    }

    /// <summary>
    /// One lexed token. <see cref="Text"/> carries the operator character or the
    /// literal/identifier spelling.
    /// </summary>
    /// <param name="Kind">Discriminator.</param>
    /// <param name="Text">Raw text the token covers in the source expression.</param>
    private readonly record struct Token(TokenKind Kind, string Text);

    /// <inheritdoc />
    public Result<decimal> Evaluate(string expression, IReadOnlyDictionary<string, decimal> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (string.IsNullOrWhiteSpace(expression))
        {
            return Result<decimal>.Failure(
                ErrorCodes.ExpressionInvalid,
                "Expression is empty.");
        }

        var tokens = Tokenize(expression);
        if (tokens is null)
        {
            return Result<decimal>.Failure(
                ErrorCodes.ExpressionInvalid,
                "Expression contains an invalid character or malformed token.");
        }

        var rpn = ToRpn(tokens);
        if (rpn is null)
        {
            return Result<decimal>.Failure(
                ErrorCodes.ExpressionInvalid,
                "Expression has unbalanced parentheses or misplaced operators.");
        }

        return EvaluateRpn(rpn, inputs);
    }

    /// <summary>
    /// Lexes the source string into a token list. Returns <see langword="null"/> when
    /// an invalid character is encountered (so the caller can surface
    /// <see cref="ErrorCodes.ExpressionInvalid"/>).
    /// </summary>
    /// <param name="expression">The raw expression.</param>
    /// <returns>Token list on success; <see langword="null"/> on malformed input.</returns>
    private static List<Token>? Tokenize(string expression)
    {
        var tokens = new List<Token>();
        var sb = new StringBuilder();
        var i = 0;
        while (i < expression.Length)
        {
            var c = expression[i];

            // Whitespace separates tokens but otherwise has no meaning.
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Number — decimal literal (optionally with a single '.' decimal point).
            if (char.IsDigit(c) || (c == '.' && i + 1 < expression.Length && char.IsDigit(expression[i + 1])))
            {
                sb.Clear();
                bool sawDot = false;
                while (i < expression.Length && (char.IsDigit(expression[i]) || (expression[i] == '.' && !sawDot)))
                {
                    if (expression[i] == '.') sawDot = true;
                    sb.Append(expression[i]);
                    i++;
                }
                tokens.Add(new Token(TokenKind.Number, sb.ToString()));
                continue;
            }

            // Identifier — letters / underscore continuing with letters / digits / underscore.
            if (char.IsLetter(c) || c == '_')
            {
                sb.Clear();
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
                {
                    sb.Append(expression[i]);
                    i++;
                }
                tokens.Add(new Token(TokenKind.Identifier, sb.ToString()));
                continue;
            }

            // Operators / parens.
            switch (c)
            {
                case '+':
                case '*':
                case '/':
                    tokens.Add(new Token(TokenKind.Operator, c.ToString()));
                    i++;
                    continue;
                case '-':
                    // Unary minus when at expression start OR immediately after another operator / open paren.
                    if (tokens.Count == 0
                        || tokens[^1].Kind == TokenKind.Operator
                        || tokens[^1].Kind == TokenKind.UnaryMinus
                        || tokens[^1].Kind == TokenKind.OpenParen)
                    {
                        tokens.Add(new Token(TokenKind.UnaryMinus, "-"));
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Operator, "-"));
                    }
                    i++;
                    continue;
                case '(':
                    tokens.Add(new Token(TokenKind.OpenParen, "("));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.CloseParen, ")"));
                    i++;
                    continue;
                default:
                    // Unknown character — tokenisation fails.
                    return null;
            }
        }

        return tokens;
    }

    /// <summary>
    /// Converts the infix token list into reverse Polish notation via Dijkstra's
    /// Shunting-yard. Returns <see langword="null"/> on unbalanced parentheses or
    /// other structural error so the caller can surface
    /// <see cref="ErrorCodes.ExpressionInvalid"/>.
    /// </summary>
    /// <param name="tokens">Lexed token list.</param>
    /// <returns>Token list in RPN order; <see langword="null"/> on parse error.</returns>
    private static List<Token>? ToRpn(List<Token> tokens)
    {
        var output = new List<Token>();
        var operators = new Stack<Token>();

        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Number:
                case TokenKind.Identifier:
                    output.Add(token);
                    break;

                case TokenKind.Operator:
                case TokenKind.UnaryMinus:
                    while (operators.Count > 0
                        && operators.Peek().Kind != TokenKind.OpenParen
                        && HasHigherOrEqualPrecedence(operators.Peek(), token))
                    {
                        output.Add(operators.Pop());
                    }
                    operators.Push(token);
                    break;

                case TokenKind.OpenParen:
                    operators.Push(token);
                    break;

                case TokenKind.CloseParen:
                    while (operators.Count > 0 && operators.Peek().Kind != TokenKind.OpenParen)
                    {
                        output.Add(operators.Pop());
                    }
                    if (operators.Count == 0)
                    {
                        // Unbalanced parens — close without matching open.
                        return null;
                    }
                    operators.Pop(); // discard the open paren
                    break;
            }
        }

        while (operators.Count > 0)
        {
            var top = operators.Pop();
            if (top.Kind == TokenKind.OpenParen)
            {
                // Unbalanced parens — open without matching close.
                return null;
            }
            output.Add(top);
        }

        return output;
    }

    /// <summary>
    /// Operator-precedence comparison: standard left-to-right precedence for binary
    /// operators (+ and - tied lowest, * and / tied higher). Unary minus binds higher
    /// than any binary operator.
    /// </summary>
    /// <param name="onStack">The operator currently on top of the stack.</param>
    /// <param name="incoming">The incoming operator being placed.</param>
    /// <returns><see langword="true"/> when <paramref name="onStack"/> should be popped first.</returns>
    private static bool HasHigherOrEqualPrecedence(Token onStack, Token incoming)
    {
        var stackP = Precedence(onStack);
        var incomingP = Precedence(incoming);
        // Unary minus is right-associative; binary operators are left-associative.
        // For left-associative incoming we pop on >=; for right-associative on >.
        if (incoming.Kind == TokenKind.UnaryMinus)
        {
            return stackP > incomingP;
        }
        return stackP >= incomingP;
    }

    /// <summary>
    /// Numeric precedence of an operator token. Higher values bind tighter.
    /// </summary>
    /// <param name="token">An operator or unary-minus token.</param>
    /// <returns>1 for <c>+</c>/<c>-</c>; 2 for <c>*</c>/<c>/</c>; 3 for unary minus.</returns>
    private static int Precedence(Token token)
    {
        if (token.Kind == TokenKind.UnaryMinus) return 3;
        return token.Text switch
        {
            "+" or "-" => 1,
            "*" or "/" => 2,
            _ => 0,
        };
    }

    /// <summary>
    /// Evaluates the RPN token list, resolving identifiers against
    /// <paramref name="inputs"/>. Returns the stable error codes documented on
    /// <see cref="IExpressionEvaluator"/>.
    /// </summary>
    /// <param name="rpn">RPN token list.</param>
    /// <param name="inputs">Named-input bindings.</param>
    /// <returns>Success with the result, or failure carrying the appropriate code.</returns>
    private static Result<decimal> EvaluateRpn(List<Token> rpn, IReadOnlyDictionary<string, decimal> inputs)
    {
        var stack = new Stack<decimal>();
        foreach (var token in rpn)
        {
            switch (token.Kind)
            {
                case TokenKind.Number:
                    if (!decimal.TryParse(token.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                    {
                        return Result<decimal>.Failure(
                            ErrorCodes.ExpressionInvalid,
                            $"Invalid numeric literal '{token.Text}'.");
                    }
                    stack.Push(n);
                    break;

                case TokenKind.Identifier:
                    if (!inputs.TryGetValue(token.Text, out var bound))
                    {
                        return Result<decimal>.Failure(
                            ErrorCodes.ExpressionUnknownInput,
                            $"Expression references unknown input '{token.Text}'.");
                    }
                    stack.Push(bound);
                    break;

                case TokenKind.UnaryMinus:
                    if (stack.Count < 1)
                    {
                        return Result<decimal>.Failure(
                            ErrorCodes.ExpressionInvalid,
                            "Unary minus has no operand.");
                    }
                    stack.Push(-stack.Pop());
                    break;

                case TokenKind.Operator:
                    if (stack.Count < 2)
                    {
                        return Result<decimal>.Failure(
                            ErrorCodes.ExpressionInvalid,
                            $"Operator '{token.Text}' is missing an operand.");
                    }
                    var right = stack.Pop();
                    var left = stack.Pop();
                    switch (token.Text)
                    {
                        case "+":
                            stack.Push(left + right);
                            break;
                        case "-":
                            stack.Push(left - right);
                            break;
                        case "*":
                            stack.Push(left * right);
                            break;
                        case "/":
                            if (right == 0m)
                            {
                                return Result<decimal>.Failure(
                                    ErrorCodes.ExpressionDivideByZero,
                                    "Division by zero.");
                            }
                            stack.Push(left / right);
                            break;
                        default:
                            return Result<decimal>.Failure(
                                ErrorCodes.ExpressionInvalid,
                                $"Unsupported operator '{token.Text}'.");
                    }
                    break;

                default:
                    return Result<decimal>.Failure(
                        ErrorCodes.ExpressionInvalid,
                        $"Unexpected token '{token.Text}'.");
            }
        }

        if (stack.Count != 1)
        {
            return Result<decimal>.Failure(
                ErrorCodes.ExpressionInvalid,
                "Expression did not reduce to a single value.");
        }

        return Result<decimal>.Success(stack.Pop());
    }
}
