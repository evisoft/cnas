using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — placeholder S3-compatible <see cref="IBackupTarget"/>.
/// The orchestrator only consults this adapter when a policy's
/// <see cref="BackupPolicy.TargetKind"/> is
/// <see cref="BackupTargetKind.S3Compatible"/>. Until DevOps wires the real
/// settings the adapter returns a deterministic
/// <see cref="IBackupTarget.TargetNotConfiguredCode"/> failure on every
/// call. NEVER throws — the orchestrator surfaces the failure as a
/// Sensitive-severity audit row.
/// </summary>
/// <remarks>
/// <para>
/// <b>No SDK dependency.</b> We intentionally do NOT pull the AWS SDK at
/// this iteration — keeping the adapter as a stub means a future iteration
/// can swap to MinIO, AWS, Azure, or MCloud-flavoured implementations
/// without touching the application code.
/// </para>
/// </remarks>
public sealed class S3CompatibleBackupTarget : IBackupTarget
{
    private readonly IOptionsMonitor<BackupOptions> _options;

    /// <summary>Constructs the adapter.</summary>
    /// <param name="options">Bound options snapshot.</param>
    public S3CompatibleBackupTarget(IOptionsMonitor<BackupOptions> options)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public BackupTargetKind Kind => BackupTargetKind.S3Compatible;

    /// <inheritdoc />
    public Task<Result<BackupUploadResult>> UploadAsync(
        BackupPolicy policy,
        BackupPayloadStream payload,
        CancellationToken cancellationToken = default)
    {
        System.ArgumentNullException.ThrowIfNull(policy);
        System.ArgumentNullException.ThrowIfNull(payload);
        return Task.FromResult(Result<BackupUploadResult>.Failure(
            IBackupTarget.TargetNotConfiguredCode,
            BuildFailureMessage()));
    }

    /// <inheritdoc />
    public Task<Result<BackupPayloadStream>> DownloadAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Result<BackupPayloadStream>.Failure(
            IBackupTarget.TargetNotConfiguredCode,
            BuildFailureMessage()));

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(
            IBackupTarget.TargetNotConfiguredCode,
            BuildFailureMessage()));

    /// <summary>
    /// Builds the deterministic failure message, mentioning whether the
    /// endpoint is empty (placeholder mode) or non-empty (future iteration
    /// will plug in the real SDK call).
    /// </summary>
    /// <returns>Stable English message; safe to log.</returns>
    private string BuildFailureMessage()
    {
        var endpoint = _options.CurrentValue.S3.Endpoint;
        return string.IsNullOrWhiteSpace(endpoint)
            ? "S3-compatible backup target is not yet configured; awaiting Cnas:Backups:S3:Endpoint setting and real SDK adapter."
            : $"S3-compatible backup target endpoint '{endpoint}' is configured but the SDK adapter is not yet implemented.";
    }
}
