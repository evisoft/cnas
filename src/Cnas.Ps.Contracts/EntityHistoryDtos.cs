using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0191 / TOR SEC 050 / TOR ARH 028 — entity-history projection DTOs. All Id
// fields are Sqid-encoded per CLAUDE.md RULE 3. Contracts MUST NOT use
// <see cref="..."/> references into Cnas.Ps.Core.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0191 — single point-in-time snapshot of a tracked entity. One row per
/// Insert / Update / Delete on a <c>IHistoryTracked</c> entity.
/// </summary>
/// <param name="Id">Sqid-encoded history-row id.</param>
/// <param name="EntityType">CLR type name of the tracked entity (e.g. <c>UserProfile</c>).</param>
/// <param name="EntitySqid">Sqid-encoded id of the tracked entity at the time of the change.</param>
/// <param name="ChangedAtUtc">UTC instant at which the entity was mutated.</param>
/// <param name="Operation">Single-character operation kind: <c>I</c> = Insert, <c>U</c> = Update, <c>D</c> = Delete.</param>
/// <param name="PayloadJson">Redacted JSON snapshot of the entity columns at the time of the change.</param>
/// <param name="ActorSqid">Sqid (or literal <c>system</c>) of the actor that performed the change.</param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record EntityHistoryRowDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string EntityType,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string EntitySqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateTime ChangedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Operation,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string PayloadJson,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ActorSqid);

/// <summary>
/// R0191 — timeline projection returned by
/// <c>GET /api/admin/history?type=…&amp;id=…</c>. Rows are ordered most-recent
/// first.
/// </summary>
/// <param name="EntityType">Echo of the requested entity type.</param>
/// <param name="EntitySqid">Echo of the requested entity Sqid.</param>
/// <param name="Rows">Snapshot rows ordered <c>ChangedAtUtc DESC</c>.</param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record EntityHistoryTimelineDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string EntityType,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string EntitySqid,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<EntityHistoryRowDto> Rows);
