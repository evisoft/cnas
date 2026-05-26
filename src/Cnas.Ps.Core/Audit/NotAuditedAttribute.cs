namespace Cnas.Ps.Core.Audit;

/// <summary>
/// R0184 / TOR SEC 042 / CLAUDE.md §5.6 — marks a property on an
/// <see cref="AutoAuditAttribute"/>-annotated entity so the universal
/// <c>AuditingInterceptor</c> EXCLUDES the property's old/new value from the
/// diff JSON payload it emits.
/// </summary>
/// <remarks>
/// <para>
/// Use this on plaintext-PII columns (IDNP, IDNO, IBAN, raw e-mail, phone),
/// password / token / hash columns, and any EncryptedString-mapped column.
/// The interceptor also maintains a hardcoded backstop list of property
/// names (NationalId, LocalPasswordHash, Idnp, Idno, BankIban, Iban,
/// ClientIpAddress, AccessTokenHash, RefreshTokenHash, ...) so that even
/// entities the developer forgot to annotate stay safe.
/// </para>
/// <para>
/// Place this attribute on the property itself; the interceptor uses
/// reflection to discover it from the EF Core change-tracker entry.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class NotAuditedAttribute : Attribute
{
}
