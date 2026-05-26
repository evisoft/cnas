using System.Collections.Generic;

namespace Cnas.Ps.Application.Migration;

/// <summary>
/// R2431 / TOR M4 — opaque per-row payload streamed by an
/// <c>IMigrationSource</c>. Carries the PII-free
/// <see cref="SourceFingerprint"/> the reconciler keys off plus the raw
/// field bag the mapper interprets.
/// </summary>
/// <param name="SourceFingerprint">Stable PII-free row hash (≤ 128 chars).</param>
/// <param name="Fields">Raw column → value bag exactly as the source produced it.</param>
public sealed record MigrationSourceRecord(
    string SourceFingerprint,
    IReadOnlyDictionary<string, object?> Fields);
