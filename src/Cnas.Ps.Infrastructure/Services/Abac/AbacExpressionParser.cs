using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Services.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — hand-written recursive-descent parser for the ABAC
/// safe sub-language. Stateless and thread-safe; registered as a Singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hardening.</b> Three structural limits defend against pathological
/// expressions submitted by a hostile administrator: token count ≤ 256,
/// recursion depth ≤ 16, string literal length ≤ 256. Any breach yields
/// <see cref="ErrorCodes.AbacParseError"/> with a stable detail message; no
/// expression that breaks any limit is ever persisted.
/// </para>
/// <para>
/// <b>Identifier allow-list.</b> Every identifier MUST start with one of
/// <c>subject.</c>, <c>resource.</c>, <c>environment.</c>, or <c>action.</c>.
/// This enforces the only safe entry points into the
/// <see cref="AbacEvaluationContext"/> — arbitrary state references
/// (<c>db.users.password</c>) would be a security hole even though the
/// evaluator has no way to resolve them.
/// </para>
/// </remarks>
public sealed class AbacExpressionParser : IAbacExpressionParser
{
    /// <summary>Maximum tokens accepted in a single expression (anti-DoS).</summary>
    public const int MaxTokens = 256;

    /// <summary>Maximum AST recursion depth (anti-DoS).</summary>
    public const int MaxDepth = 16;

    /// <summary>Maximum string-literal length (anti-DoS).</summary>
    public const int MaxStringLiteralLength = 256;

    /// <summary>Allowed root namespaces for identifiers.</summary>
    private static readonly string[] AllowedRoots = new[] { "subject", "resource", "environment", "action" };

    /// <inheritdoc />
    public Result<AbacExpression> Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Result<AbacExpression>.Failure(ErrorCodes.AbacParseError, "Condition expression is required.");
        }

        try
        {
            var tokens = Tokenize(source);
            if (tokens.Count > MaxTokens)
            {
                return Result<AbacExpression>.Failure(
                    ErrorCodes.AbacParseError,
                    $"Expression has {tokens.Count} tokens, exceeds limit of {MaxTokens}.");
            }
            var state = new ParserState(tokens);
            var expr = state.ParseOr(depth: 0);
            if (!state.IsAtEnd)
            {
                return Result<AbacExpression>.Failure(
                    ErrorCodes.AbacParseError,
                    $"Unexpected trailing token '{state.Peek().Text}' at position {state.Peek().Position}.");
            }
            return Result<AbacExpression>.Success(expr);
        }
        catch (AbacParseException ex)
        {
            return Result<AbacExpression>.Failure(ErrorCodes.AbacParseError, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Defensive — any other failure is treated as a parse error so a
            // malformed rule cannot crash the substrate.
            return Result<AbacExpression>.Failure(
                ErrorCodes.AbacParseError,
                $"Unexpected parse failure: {ex.GetType().Name}.");
        }
    }

    /// <summary>Tokenizes the supplied source string into a flat token list.</summary>
    /// <param name="source">The expression source.</param>
    /// <returns>The token list.</returns>
    private static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            var start = i;
            if (c == '(' || c == ')' || c == ',')
            {
                tokens.Add(new Token(c == '(' ? TokenKind.LParen : c == ')' ? TokenKind.RParen : TokenKind.Comma, c.ToString(), start));
                i++;
                continue;
            }
            if (c == '"')
            {
                // String literal — read until the next unescaped quote.
                i++;
                var sb = new StringBuilder();
                while (i < source.Length && source[i] != '"')
                {
                    if (sb.Length >= MaxStringLiteralLength)
                    {
                        throw new AbacParseException($"String literal exceeds {MaxStringLiteralLength} characters at position {start}.");
                    }
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        // Minimal escape support — \\ and \"
                        var next = source[i + 1];
                        if (next == '"' || next == '\\')
                        {
                            sb.Append(next);
                            i += 2;
                            continue;
                        }
                    }
                    sb.Append(source[i]);
                    i++;
                }
                if (i >= source.Length)
                {
                    throw new AbacParseException($"Unterminated string literal starting at position {start}.");
                }
                i++; // skip closing quote
                tokens.Add(new Token(TokenKind.StringLiteral, sb.ToString(), start));
                continue;
            }
            if (c == '=' || c == '!' || c == '<' || c == '>')
            {
                // Two-char operators (== != <= >=) or one-char (< >).
                if (i + 1 < source.Length && source[i + 1] == '=')
                {
                    var op = source.Substring(i, 2);
                    var kind = op switch
                    {
                        "==" => TokenKind.Equal,
                        "!=" => TokenKind.NotEqual,
                        "<=" => TokenKind.LessEqual,
                        ">=" => TokenKind.GreaterEqual,
                        _ => throw new AbacParseException($"Unknown operator '{op}' at position {start}."),
                    };
                    tokens.Add(new Token(kind, op, start));
                    i += 2;
                    continue;
                }
                if (c == '<')
                {
                    tokens.Add(new Token(TokenKind.Less, "<", start));
                    i++;
                    continue;
                }
                if (c == '>')
                {
                    tokens.Add(new Token(TokenKind.Greater, ">", start));
                    i++;
                    continue;
                }
                throw new AbacParseException($"Unexpected character '{c}' at position {start}.");
            }
            if (char.IsDigit(c) || (c == '-' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
            {
                // Numeric literal — possibly negative, possibly fractional.
                var sb = new StringBuilder();
                if (c == '-')
                {
                    sb.Append('-');
                    i++;
                }
                while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.'))
                {
                    sb.Append(source[i]);
                    i++;
                }
                tokens.Add(new Token(TokenKind.NumberLiteral, sb.ToString(), start));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                // Identifier OR keyword — read word.char.word.char…
                var sb = new StringBuilder();
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_' || source[i] == '.'))
                {
                    sb.Append(source[i]);
                    i++;
                }
                var word = sb.ToString();
                var kind = word switch
                {
                    "and" => TokenKind.And,
                    "or" => TokenKind.Or,
                    "not" => TokenKind.Not,
                    "true" => TokenKind.True,
                    "false" => TokenKind.False,
                    "null" => TokenKind.Null,
                    "in" => TokenKind.InCall,
                    "startsWith" => TokenKind.StartsWithCall,
                    "endsWith" => TokenKind.EndsWithCall,
                    "contains" => TokenKind.ContainsCall,
                    "has" => TokenKind.HasCall,
                    _ => TokenKind.Identifier,
                };
                tokens.Add(new Token(kind, word, start));
                continue;
            }
            throw new AbacParseException($"Unexpected character '{c}' at position {start}.");
        }
        return tokens;
    }

    /// <summary>Token kinds emitted by the lexer.</summary>
    private enum TokenKind
    {
        LParen, RParen, Comma,
        Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
        And, Or, Not,
        True, False, Null,
        InCall, StartsWithCall, EndsWithCall, ContainsCall, HasCall,
        Identifier, StringLiteral, NumberLiteral,
    }

    /// <summary>A single lexer token.</summary>
    /// <param name="Kind">Token kind.</param>
    /// <param name="Text">Source text of the token.</param>
    /// <param name="Position">Character offset in the source string.</param>
    private sealed record Token(TokenKind Kind, string Text, int Position);

    /// <summary>Sentinel exception used internally by the parser to bail with a stable message.</summary>
    private sealed class AbacParseException : Exception
    {
        public AbacParseException(string message) : base(message) { }
    }

    /// <summary>
    /// Mutable parser state — tracks the current token cursor + recursion
    /// depth. Allocated once per <see cref="AbacExpressionParser.Parse"/> call.
    /// </summary>
    private sealed class ParserState
    {
        private readonly IReadOnlyList<Token> _tokens;
        private int _cursor;

        /// <summary>Constructs the state with the supplied flat token list.</summary>
        /// <param name="tokens">The lexer output.</param>
        public ParserState(IReadOnlyList<Token> tokens) { _tokens = tokens; }

        /// <summary><c>true</c> when the cursor has run past the end of the token list.</summary>
        public bool IsAtEnd => _cursor >= _tokens.Count;

        /// <summary>Peeks at the current token without advancing the cursor.</summary>
        /// <returns>The current token.</returns>
        public Token Peek()
        {
            if (IsAtEnd)
            {
                throw new AbacParseException("Unexpected end of expression.");
            }
            return _tokens[_cursor];
        }

        /// <summary>Consumes the current token and advances the cursor.</summary>
        /// <returns>The consumed token.</returns>
        private Token Consume()
        {
            var t = Peek();
            _cursor++;
            return t;
        }

        /// <summary>Consumes a token of the requested kind; throws otherwise.</summary>
        /// <param name="expected">The expected kind.</param>
        /// <returns>The consumed token.</returns>
        private Token Expect(TokenKind expected)
        {
            var t = Peek();
            if (t.Kind != expected)
            {
                throw new AbacParseException(
                    $"Expected '{expected}' at position {t.Position}, found '{t.Text}'.");
            }
            return Consume();
        }

        /// <summary>Parses an <c>or</c> expression (lowest precedence).</summary>
        /// <param name="depth">Current recursion depth.</param>
        /// <returns>The parsed expression.</returns>
        public AbacExpression ParseOr(int depth)
        {
            CheckDepth(depth);
            var left = ParseAnd(depth + 1);
            while (!IsAtEnd && Peek().Kind == TokenKind.Or)
            {
                Consume();
                var right = ParseAnd(depth + 1);
                left = new AbacOrExpression(left, right);
            }
            return left;
        }

        /// <summary>Parses an <c>and</c> expression.</summary>
        /// <param name="depth">Current recursion depth.</param>
        /// <returns>The parsed expression.</returns>
        private AbacExpression ParseAnd(int depth)
        {
            CheckDepth(depth);
            var left = ParseNot(depth + 1);
            while (!IsAtEnd && Peek().Kind == TokenKind.And)
            {
                Consume();
                var right = ParseNot(depth + 1);
                left = new AbacAndExpression(left, right);
            }
            return left;
        }

        /// <summary>Parses an optional <c>not</c> followed by an atom.</summary>
        /// <param name="depth">Current recursion depth.</param>
        /// <returns>The parsed expression.</returns>
        private AbacExpression ParseNot(int depth)
        {
            CheckDepth(depth);
            if (!IsAtEnd && Peek().Kind == TokenKind.Not)
            {
                Consume();
                return new AbacNotExpression(ParseAtom(depth + 1));
            }
            return ParseAtom(depth + 1);
        }

        /// <summary>Parses an atom — parenthesised expression, call, or comparison.</summary>
        /// <param name="depth">Current recursion depth.</param>
        /// <returns>The parsed expression.</returns>
        private AbacExpression ParseAtom(int depth)
        {
            CheckDepth(depth);
            if (IsAtEnd)
            {
                throw new AbacParseException("Unexpected end of expression at atom position.");
            }
            var t = Peek();
            if (t.Kind == TokenKind.LParen)
            {
                Consume();
                var inner = ParseOr(depth + 1);
                Expect(TokenKind.RParen);
                return inner;
            }
            if (t.Kind == TokenKind.InCall)
            {
                return ParseInCall(depth);
            }
            if (t.Kind is TokenKind.StartsWithCall or TokenKind.EndsWithCall or TokenKind.ContainsCall)
            {
                return ParseStringCall(t.Kind, depth);
            }
            if (t.Kind == TokenKind.HasCall)
            {
                return ParseHasCall();
            }
            // Otherwise it must be a comparison: value op value.
            var left = ParseValue();
            var opToken = Consume();
            var op = opToken.Kind switch
            {
                TokenKind.Equal => AbacComparisonOperator.Equal,
                TokenKind.NotEqual => AbacComparisonOperator.NotEqual,
                TokenKind.Less => AbacComparisonOperator.Less,
                TokenKind.LessEqual => AbacComparisonOperator.LessEqual,
                TokenKind.Greater => AbacComparisonOperator.Greater,
                TokenKind.GreaterEqual => AbacComparisonOperator.GreaterEqual,
                _ => throw new AbacParseException(
                    $"Expected comparison operator at position {opToken.Position}, found '{opToken.Text}'."),
            };
            var right = ParseValue();
            return new AbacComparisonExpression(left, right, op);
        }

        /// <summary>Parses <c>in(value, value, value, …)</c>.</summary>
        /// <param name="depth">Current recursion depth.</param>
        /// <returns>The parsed expression.</returns>
        private AbacExpression ParseInCall(int depth)
        {
            CheckDepth(depth);
            Expect(TokenKind.InCall);
            Expect(TokenKind.LParen);
            var target = ParseValue();
            Expect(TokenKind.Comma);
            var candidates = new List<AbacValue> { ParseValue() };
            while (!IsAtEnd && Peek().Kind == TokenKind.Comma)
            {
                Consume();
                candidates.Add(ParseValue());
            }
            Expect(TokenKind.RParen);
            return new AbacInExpression(target, candidates);
        }

        /// <summary>Parses one of the string-predicate calls.</summary>
        /// <param name="callKind">The token kind for the call.</param>
        /// <param name="depth">Current recursion depth.</param>
        /// <returns>The parsed expression.</returns>
        private AbacExpression ParseStringCall(TokenKind callKind, int depth)
        {
            CheckDepth(depth);
            Consume();
            Expect(TokenKind.LParen);
            var target = ParseValue();
            Expect(TokenKind.Comma);
            var literalToken = Expect(TokenKind.StringLiteral);
            Expect(TokenKind.RParen);
            var kind = callKind switch
            {
                TokenKind.StartsWithCall => AbacStringCallKind.StartsWith,
                TokenKind.EndsWithCall => AbacStringCallKind.EndsWith,
                TokenKind.ContainsCall => AbacStringCallKind.Contains,
                _ => throw new AbacParseException("Unknown string call kind."),
            };
            return new AbacStringCallExpression(kind, target, literalToken.Text);
        }

        /// <summary>Parses <c>has(identifier)</c>.</summary>
        /// <returns>The parsed expression.</returns>
        private AbacExpression ParseHasCall()
        {
            Expect(TokenKind.HasCall);
            Expect(TokenKind.LParen);
            var idToken = Expect(TokenKind.Identifier);
            EnsureIdentifierAllowed(idToken);
            Expect(TokenKind.RParen);
            return new AbacHasExpression(idToken.Text);
        }

        /// <summary>Parses a single value (literal or identifier).</summary>
        /// <returns>The parsed value.</returns>
        private AbacValue ParseValue()
        {
            var t = Consume();
            switch (t.Kind)
            {
                case TokenKind.StringLiteral:
                    return new AbacStringLiteral(t.Text);
                case TokenKind.NumberLiteral:
                    if (!decimal.TryParse(t.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
                    {
                        throw new AbacParseException($"Invalid numeric literal '{t.Text}' at position {t.Position}.");
                    }
                    return new AbacNumberLiteral(dec);
                case TokenKind.True:
                    return new AbacBoolLiteral(true);
                case TokenKind.False:
                    return new AbacBoolLiteral(false);
                case TokenKind.Null:
                    return new AbacNullLiteral();
                case TokenKind.Identifier:
                    EnsureIdentifierAllowed(t);
                    return new AbacIdentifierValue(t.Text);
                default:
                    throw new AbacParseException(
                        $"Expected a value at position {t.Position}, found '{t.Text}'.");
            }
        }

        /// <summary>
        /// Asserts that <paramref name="identifier"/> is rooted in one of the
        /// four allowed namespaces. Throws otherwise so the parser fails fast.
        /// </summary>
        /// <param name="identifier">The identifier token to validate.</param>
        private static void EnsureIdentifierAllowed(Token identifier)
        {
            var text = identifier.Text;
            var dot = text.IndexOf('.');
            if (dot <= 0)
            {
                throw new AbacParseException(
                    $"Identifier '{text}' must be a dotted path under subject/resource/environment/action.");
            }
            var root = text[..dot];
            var ok = false;
            foreach (var allowed in AllowedRoots)
            {
                if (string.Equals(allowed, root, StringComparison.Ordinal))
                {
                    ok = true;
                    break;
                }
            }
            if (!ok)
            {
                throw new AbacParseException(
                    $"Identifier '{text}' has disallowed root '{root}'; expected subject/resource/environment/action.");
            }
        }

        /// <summary>Asserts that the current recursion depth has not exceeded the limit.</summary>
        /// <param name="depth">Current depth.</param>
        private static void CheckDepth(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new AbacParseException(
                    $"Expression nesting exceeds maximum depth of {MaxDepth}.");
            }
        }
    }
}
