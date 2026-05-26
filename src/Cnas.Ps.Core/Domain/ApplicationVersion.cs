namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0321 / R0224 / UI 008 — one immutable snapshot of a <see cref="ServiceApplication"/>'s
/// in-flight form payload. The autosave subsystem inserts a new row on every save tick
/// (auto or manual) so the citizen can revert to any prior point in the draft history;
/// the submission ceremony inserts a final <see cref="ApplicationVersionSource.Submit"/>
/// row that captures the exact bytes that were promoted from Draft to Submitted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Versioning shape.</b> Rows are append-only per <see cref="ServiceApplicationId"/>:
/// <see cref="VersionNumber"/> starts at <c>1</c> and increments monotonically. Exactly
/// one row per application is marked <see cref="IsCurrent"/> = <c>true</c> at any given
/// instant; the rest are historical snapshots. The
/// <c>ApplicationVersionService.SaveAsync</c> implementation flips the previous current
/// row to <c>false</c> in the same transaction as the new insert so the invariant cannot
/// be broken by a partial save.
/// </para>
/// <para>
/// <b>Dedup.</b> The service performs an ordinal byte-compare of
/// <see cref="FormDataJson"/> against the current row before inserting; identical payloads
/// short-circuit (no new row, no version-number burn, dedup counter increment) so the
/// autosave tick can fire on a no-op page without bloating the history.
/// </para>
/// <para>
/// <b>Pruning policy.</b> When the count of
/// <see cref="ApplicationVersionSource.Autosave"/> rows for an application exceeds the
/// configured cap (<c>ApplicationAutosaveOptions.MaxAutosavesPerApplication</c>), the
/// service HARD-DELETES the oldest <see cref="ApplicationVersionSource.Autosave"/> row.
/// <see cref="ApplicationVersionSource.ManualSave"/>, <see cref="ApplicationVersionSource.Submit"/>,
/// and <see cref="ApplicationVersionSource.Revert"/> rows are NEVER pruned even when
/// older — they document explicit user intent.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> The numeric <see cref="AuditableEntity.Id"/> never leaves the
/// system. The marker <see cref="IExternalId"/> is applied because the
/// <c>ApplicationVersionOutputDto</c> surfaces the row's Sqid as the public identifier —
/// CLAUDE.md RULE 3 / ARH 027.
/// </para>
/// <para>
/// <b>PII handling.</b> <see cref="FormDataJson"/> can contain free-form citizen data
/// (IDNP, names, addresses) and is therefore a defined PII surface. Encryption at rest is
/// out of scope for this batch — see the batch deferral note for the
/// <c>EncryptedStringConverter</c> follow-up.
/// </para>
/// </remarks>
public sealed class ApplicationVersion : AuditableEntity, IExternalId
{
    /// <summary>
    /// FK to the owning <see cref="ServiceApplication"/>. A version cannot exist without
    /// the parent application; the unique index <c>(ServiceApplicationId, VersionNumber)</c>
    /// declared in <c>ApplicationVersionConfiguration</c> enforces the natural-key
    /// uniqueness contract.
    /// </summary>
    public long ServiceApplicationId { get; set; }

    /// <summary>
    /// 1-based monotonically increasing version number for the owning application. The
    /// first save assigns <c>1</c>; each subsequent save (whether ManualSave, Autosave,
    /// Submit, or Revert) takes <c>previous + 1</c>. Dedup short-circuits do NOT advance
    /// the counter because no new row is written.
    /// </summary>
    public int VersionNumber { get; set; }

    /// <summary>
    /// Full serialised form payload captured at save time. Round-tripped verbatim — the
    /// service does no schema validation on the JSON shape (the citizen-facing form is
    /// schema-flexible per FLEX 002). Capped at <c>MaxFormDataKb</c> KB by the
    /// FluentValidation rule on <c>ApplicationVersionSaveDto</c>.
    /// </summary>
    public required string FormDataJson { get; set; }

    /// <summary>
    /// FK to the <see cref="UserProfile"/> primary id of the user that initiated the save. For
    /// autosave ticks this is the authenticated citizen; for the
    /// <see cref="ApplicationVersionSource.Submit"/> row this is also the citizen
    /// (Draft → Submitted is a self-service transition). For
    /// <see cref="ApplicationVersionSource.Revert"/> it is whoever requested the revert.
    /// Background callers must not write here.
    /// </summary>
    public long CreatedByUserId { get; set; }

    /// <summary>
    /// Origin of the snapshot — drives the pruning policy (see class remarks) and lets
    /// the citizen-facing history UI render an icon ("auto" vs "saved" vs "submitted" vs
    /// "reverted"). See <see cref="ApplicationVersionSource"/> for the enum semantics.
    /// </summary>
    public ApplicationVersionSource Source { get; set; }

    /// <summary>
    /// Optional free-form annotation the saver supplies (e.g. "before submitting",
    /// "removed dependent"). Capped at 1000 chars by the FluentValidation rule on
    /// <c>ApplicationVersionSaveDto</c>; <c>null</c> when not supplied.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// <c>true</c> for exactly one row per <see cref="ServiceApplicationId"/> at any
    /// instant — the most-recent save. The service flips the previous current row to
    /// <c>false</c> in the same SaveChanges call that inserts the new row so the
    /// invariant holds transactionally. Filtered unique index in
    /// <c>ApplicationVersionConfiguration</c> enforces the constraint at the DB layer.
    /// </summary>
    public bool IsCurrent { get; set; }
}
