using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Single source of truth for converting an in-memory <see cref="AuditEventRecord"/>
/// to the two persistent shapes the audit pipeline ultimately writes: an
/// <see cref="AuditLog"/> EF entity and an <see cref="MLogEntry"/> for the MLog
/// mirror. R0188 extracted this helper so that the live drainer
/// (<see cref="AuditDrainer"/>) and the replay job (<c>AuditArchiveReplayJob</c>)
/// share an identical projection — drift between the two would be a silent bug.
/// </summary>
/// <remarks>
/// <para>
/// The projection is intentionally a pure function over the record. No clocks,
/// no DI, no side effects. <see cref="AuditEventRecord.EventAtUtc"/> is preserved
/// verbatim into both <see cref="AuditableEntity.CreatedAtUtc"/> and
/// <see cref="AuditLog.EventAtUtc"/> so the eventual row reflects the true
/// business-event instant, not the (potentially much later) flush instant.
/// </para>
/// </remarks>
internal static class AuditFlushProjector
{
    /// <summary>
    /// Projects an <see cref="AuditEventRecord"/> onto a new <see cref="AuditLog"/>
    /// entity ready to be inserted via <see cref="ICnasDbContext.AuditLogs"/>.
    /// </summary>
    /// <remarks>
    /// The returned entity's <see cref="AuditLog.PrevHash"/> and
    /// <see cref="AuditLog.RowHash"/> are initialised to placeholders — every
    /// caller (drainer, replay) MUST overwrite them with the chained values
    /// from <see cref="ComputeRowHash"/> before <c>SaveChangesAsync</c>.
    /// Leaving them at placeholder would corrupt the hash chain on the very
    /// next verifier run. We keep the placeholders here (rather than on the
    /// entity defaults) so a forgotten chain step surfaces loudly when the
    /// verifier rejects the row, not silently as a "valid" GENESIS-shaped
    /// initial value.
    /// </remarks>
    /// <param name="record">The queued (or archived) record to project.</param>
    /// <returns>A fresh, unattached <see cref="AuditLog"/> instance.</returns>
    public static AuditLog ToAuditLog(AuditEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new AuditLog
        {
            CreatedAtUtc = record.EventAtUtc,
            EventAtUtc = record.EventAtUtc,
            Severity = record.Severity,
            EventCode = record.EventCode,
            ActorId = record.ActorId,
            TargetEntity = record.TargetEntity,
            TargetEntityId = record.TargetEntityId,
            SourceIp = record.SourceIp,
            CorrelationId = record.CorrelationId,
            DetailsJson = record.DetailsJson,
            // R0194 / SEC 047 — placeholder hash-chain values; the chaining
            // caller (drainer / replay) overwrites them with the real values
            // before SaveChangesAsync. See remarks above.
            PrevHash = string.Empty,
            RowHash = string.Empty,
        };
    }

    /// <summary>
    /// Projects an <see cref="AuditEventRecord"/> onto an <see cref="MLogEntry"/>
    /// value ready to forward via <see cref="IMLogClient.AppendAsync(MLogEntry, CancellationToken)"/>.
    /// </summary>
    /// <param name="record">The queued (or archived) record to project.</param>
    /// <returns>A canonical <see cref="MLogEntry"/> value.</returns>
    public static MLogEntry ToMLogEntry(AuditEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new MLogEntry(
            record.EventCode,
            record.ActorId,
            record.TargetEntity,
            record.TargetEntityId,
            record.DetailsJson);
    }

    /// <summary>
    /// R0194 / SEC 047 — computes the SHA-256 row hash for an
    /// <see cref="AuditEventRecord"/> chained from the supplied
    /// <paramref name="prevHash"/>. Returns the 64-char lowercase hex digest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Canonical form: pipe-joined fields in a fixed order, encoded UTF-8.
    /// Nullable string fields stringify as the literal <c>"null"</c> (NOT the
    /// empty string) so a tampered <c>null → ""</c> swap is detectable.
    /// <see cref="AuditEventRecord.EventAtUtc"/> uses ISO-8601 round-trip
    /// (<c>"O"</c>) so the textual representation is bit-stable across machines
    /// and locales. <see cref="AuditEventRecord.TargetEntityId"/> stringifies in
    /// invariant culture (so <c>1234</c> never becomes <c>1,234</c> on a
    /// decimal-comma locale).
    /// </para>
    /// <para>
    /// The first row in an empty chain uses <c>prevHash = "GENESIS"</c> — the
    /// chain is self-anchoring, there is no separate "first row" rule. Both
    /// <see cref="AuditDrainer"/> and <c>AuditArchiveReplayJob</c> call into
    /// this same helper so the two write paths cannot drift.
    /// </para>
    /// <para>
    /// The companion verifier (<c>IAuditChainVerifier</c>) recomputes the
    /// expected digest at every row using this same method, so any change to
    /// the canonical recipe MUST also be coordinated with the migration that
    /// back-fills existing rows — otherwise the verifier will false-alarm on
    /// the seeded data.
    /// </para>
    /// </remarks>
    /// <param name="record">The audit-event record to hash.</param>
    /// <param name="prevHash">
    /// Previous row's <see cref="AuditLog.RowHash"/>, or the literal
    /// <c>"GENESIS"</c> when chaining from an empty table.
    /// </param>
    /// <returns>The 64-character lowercase hex SHA-256 digest of the canonical form.</returns>
    public static string ComputeRowHash(AuditEventRecord record, string prevHash)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(prevHash);

        var canonical = string.Join("|", new[]
        {
            prevHash,
            record.EventAtUtc.ToString("O", CultureInfo.InvariantCulture),
            record.Severity.ToString(),
            record.EventCode,
            record.ActorId,
            record.TargetEntity ?? "null",
            record.TargetEntityId?.ToString(CultureInfo.InvariantCulture) ?? "null",
            record.SourceIp ?? "null",
            record.CorrelationId ?? "null",
            record.DetailsJson,
        });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
