namespace Cnas.Ps.Core.Audit;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — marker contract identifying entities
/// whose row mutations (Insert / Update / Delete) MUST be mirrored to the
/// application-level history table (<c>EntityHistoryRow</c>) so a complete
/// point-in-time timeline survives outside the live row. Tagged entities are
/// picked up by <c>HistoryTrackingInterceptor</c>; un-tagged entities skip the
/// history projection entirely (high-volume tables would explode the history
/// table size otherwise).
/// </summary>
/// <remarks>
/// <para>
/// <b>Application-level, not server-period temporal.</b> The TOR text mentions
/// PostgreSQL temporal tables; we deliberately implement the projection in
/// application code rather than rely on a server-side extension so the
/// pattern (a) ships on every supported database including the in-memory
/// test provider, (b) honours the existing PII-redaction discipline
/// (<c>PiiRedactor</c>), and (c) survives a Postgres-version bump that
/// changes the temporal-extension contract. The cost is one extra row per
/// tracked-entity mutation, paid synchronously inside the same transaction
/// (so a rolled-back business write does NOT leak a phantom history row).
/// </para>
/// <para>
/// <b>Relationship to <c>AutoAuditAttribute</c>.</b> The two markers are
/// complementary, not redundant. <c>AutoAuditAttribute</c> emits an
/// <c>AuditLog</c> ROW PER CHANGE (event-of-change projection, security
/// posture). <see cref="IHistoryTracked"/> emits an
/// <c>EntityHistoryRow</c> SNAPSHOT PER CHANGE (state-at-instant
/// projection, point-in-time queries / "rewind this row" UX). A given
/// entity can carry both, one, or neither — selection is governed by the
/// SEC 050 / ARH 028 sensitivity rules.
/// </para>
/// <para>
/// <b>PII discipline.</b> The history payload is serialised through the
/// same exclusion list the <c>AuditingInterceptor</c> consults
/// (<c>ExcludedPropertyNames</c> + <c>NotAuditedAttribute</c>) AND is
/// additionally redacted by <c>PiiRedactor</c> before persistence — defence
/// in depth.
/// </para>
/// <para>
/// <b>Marker-only.</b> The interface has no members. Its presence on a
/// class declaration is the contract. Implementing the interface costs
/// nothing at runtime; the interceptor performs a single
/// <c>is IHistoryTracked</c> check during <c>SavingChangesAsync</c>.
/// </para>
/// </remarks>
public interface IHistoryTracked
{
}
