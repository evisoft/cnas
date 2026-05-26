using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// Input DTO wrapping a plaintext password for policy validation
/// (CLAUDE.md §5.3 / TOR SEC 014 / R0052). Carried only across the validation
/// boundary — it MUST NEVER be persisted, logged, or serialized to the wire.
/// </summary>
/// <remarks>
/// <para>
/// The plaintext is the value the user just typed; downstream code is expected to
/// hash it via <c>IPasswordHasher.Hash</c> before storage. The validator is the
/// single source of truth for the password policy — controllers and services must
/// NEVER reinvent length/complexity checks at call sites.
/// </para>
/// <para>
/// Policy (enforced by <c>PasswordPolicyValidator</c>):
/// </para>
/// <list type="bullet">
///   <item>Length: 8 minimum, 128 maximum.</item>
///   <item>Composition: at least one lowercase, one uppercase, one digit, one symbol.</item>
/// </list>
/// </remarks>
/// <param name="Plaintext">The candidate password as typed by the user. Validated, never stored.</param>
public sealed record PasswordInput(
    [property: SensitivityClassification(SensitivityLabel.Restricted,
        Reason = "Plaintext password — must never appear in logs.")]
    string Plaintext);
