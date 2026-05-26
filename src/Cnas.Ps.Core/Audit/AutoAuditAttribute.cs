using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Core.Audit;

/// <summary>
/// R0184 / TOR SEC 042 — marks an EF-mapped entity for automatic audit-record
/// generation by the universal <c>AuditingInterceptor</c>
/// (an <c>ISaveChangesInterceptor</c>). Entities NOT carrying this attribute
/// are NOT auto-audited; service-level explicit <c>IAuditService.RecordAsync</c>
/// calls still cover those.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a NEW attribute and not reuse the existing <see cref="AuditableEntity"/>
/// base.</b> <see cref="AuditableEntity"/> exists as a base class to give
/// entities their <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c>,
/// <c>IsActive</c>, and <c>Xmin</c> infrastructure. ALL business entities
/// derive from it, so it cannot serve as the "audit this row" trigger — that
/// would cause the interceptor to write a row for every <c>SaveChanges</c>
/// touching any business entity, including high-volume rows
/// (<c>AuditLog</c> itself, <c>WorkflowTask</c> on every status flip,
/// <c>PersonalAccountEntry</c> on every payment, ...). This attribute is the
/// opt-in marker — only the entities explicitly annotated produce audit rows.
/// </para>
/// <para>
/// <b>Severity default.</b> Most auto-audit emissions are write events on
/// security-relevant entities (UserProfile, UserSession, ChangeRequest,
/// SensitiveAdminAction, AbacRule, AbacRuleSet);
/// <see cref="AuditSeverity.Information"/> is the right default. Callers that
/// need a louder signal can override via the <see cref="Severity"/> property
/// on the attribute.
/// </para>
/// <para>
/// <b>Event-code shape.</b> When <see cref="EventCodePrefix"/> is null the
/// interceptor uses the CLR type name; otherwise it uses the prefix. The
/// final event code is composed as <c>{prefix}.{STATE}</c> where STATE is
/// <c>CREATED</c> / <c>MODIFIED</c> / <c>DELETED</c>. Stable strings — once
/// published the audit event code is part of the public contract per
/// CLAUDE.md §2.2.
/// </para>
/// <para>
/// <b>Lives in <c>Cnas.Ps.Core.Audit</c>, not <c>Cnas.Ps.Core.Domain</c>.</b>
/// The architecture rule
/// <c>DomainEntityRulesTests.Entities_DeriveFromAuditableEntity_OrAreValueObjects</c>
/// enforces that every concrete public type directly inside
/// <c>Cnas.Ps.Core.Domain</c> must derive from <see cref="AuditableEntity"/>.
/// This attribute is metadata, not an entity, so it sits in a sibling
/// sub-namespace.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AutoAuditAttribute : Attribute
{
    /// <summary>
    /// Audit severity emitted by the interceptor for this entity's lifecycle
    /// events. Defaults to <see cref="AuditSeverity.Information"/>; override
    /// on entities where mutation is itself a security-relevant signal.
    /// </summary>
    public AuditSeverity Severity { get; init; } = AuditSeverity.Information;

    /// <summary>
    /// Optional prefix used when composing the audit event code. When null,
    /// the interceptor falls back to the entity's CLR type name uppercased
    /// (e.g. <c>UserProfile</c> → <c>USERPROFILE.MODIFIED</c>). Set this when
    /// the entity name doesn't match the existing audit-code namespace
    /// (e.g. UserSession → <c>SESSION</c>).
    /// </summary>
    public string? EventCodePrefix { get; init; }
}
