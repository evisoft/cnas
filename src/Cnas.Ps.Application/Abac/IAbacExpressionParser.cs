using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — parses a textual ABAC condition expression into an
/// immutable <see cref="AbacExpression"/> AST. Single-shot, allocation-light,
/// thread-safe (stateless): registered as a singleton in
/// <c>InfrastructureServiceCollectionExtensions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grammar.</b> The accepted sub-language is intentionally tiny — see
/// <c>docs/security.md</c> and the validator unit tests for the canonical
/// reference. The summary is:
/// <list type="bullet">
///   <item><description>Logical operators <c>or</c>, <c>and</c>, <c>not</c> (lowercase).</description></item>
///   <item><description>Comparisons <c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>.</description></item>
///   <item><description>Calls <c>in(value, list)</c>, <c>startsWith(value, literal)</c>, <c>endsWith</c>, <c>contains</c>, <c>has(identifier)</c>.</description></item>
///   <item><description>Literals — strings (<c>"…"</c>), decimals, <c>true</c>, <c>false</c>, <c>null</c>.</description></item>
///   <item><description>Identifiers must start with one of <c>subject.</c>, <c>resource.</c>, <c>environment.</c>, or <c>action.</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Hardening.</b> The parser enforces a maximum of 256 tokens per expression and a
/// maximum AST depth of 16. Both limits exist to defeat resource-exhaustion attacks
/// from an administrator submitting a pathological rule.
/// </para>
/// </remarks>
public interface IAbacExpressionParser
{
    /// <summary>
    /// Parses <paramref name="source"/> into an immutable AST.
    /// </summary>
    /// <param name="source">The textual condition expression supplied by the administrator.</param>
    /// <returns>
    /// On success a <see cref="Result{T}"/> wrapping the parsed root expression.
    /// On failure a <see cref="Result{T}"/> with
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.AbacParseError"/> and a
    /// human-readable detail message identifying the offending token/position.
    /// </returns>
    Result<AbacExpression> Parse(string source);
}
