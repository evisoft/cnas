using System;

namespace Cnas.Ps.Application.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — opaque value object returned by
/// <c>IBackupPayloadProvider.ProducePayloadAsync</c>. Carries the in-memory
/// payload byte-array (intentionally simple — production providers stream
/// to a temp file and load by reference for very large datasets in a
/// future iteration), plus the SHA-256 lowercase-hex digest the
/// orchestrator stores alongside the run.
/// </summary>
/// <remarks>
/// <para>
/// <b>No PII in logs.</b> The orchestrator NEVER logs the payload bytes —
/// only the length + hash. The bytes themselves may carry citizen data;
/// keeping the value-object reference-typed and bounded inside the
/// orchestrator scope keeps the per-fire allocation localised.
/// </para>
/// </remarks>
/// <param name="Payload">Raw payload bytes (may carry PII; never logged).</param>
/// <param name="Sha256Hex">Lowercase-hex SHA-256 digest of <paramref name="Payload"/> (64 chars).</param>
/// <param name="SizeBytes">Length of <paramref name="Payload"/> in bytes.</param>
public sealed record BackupPayloadStream(
    ReadOnlyMemory<byte> Payload,
    string Sha256Hex,
    long SizeBytes);
