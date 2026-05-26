using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Infrastructure.Storage;

/// <summary>
/// Sentinel <see cref="IFileStorage"/> registered when the deployment did not
/// supply MinIO credentials. Every method throws <see cref="InvalidOperationException"/>
/// on first use so the failure is loud at the point of impact (the first
/// attempt to upload, download, presign, or delete) rather than at DI
/// activation time, where it would have crashed every controller that depends
/// on the storage chain — even those whose endpoints never touch object
/// storage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sentinel instead of letting MinioClient.Build() throw.</b> The
/// upstream <c>MinioClient.Build()</c> raises
/// "User Access Credentials not initialized" the moment an empty AccessKey or
/// SecretKey is observed. Because the MinIO client is wired into every
/// <c>IDocumentService</c> consumer, that exception surfaced at controller
/// activation time and broke unrelated requests (most visibly during E2E
/// fixture boot). Mirroring the
/// <see cref="Cnas.Ps.Infrastructure.Security.MissingKeyFieldEncryptor"/> and
/// <see cref="Cnas.Ps.Infrastructure.Security.MissingSaltHmacHasher"/>
/// fail-loud pattern: construction succeeds, the first real call throws with
/// a clear "this is unconfigured" diagnostic that points at the offending
/// settings keys.
/// </para>
/// <para>
/// Tests and local-dev environments that legitimately do not exercise object
/// storage are unaffected; they only meet the sentinel if a code path actually
/// touches an <see cref="IFileStorage"/> method.
/// </para>
/// </remarks>
internal sealed class MissingMinioFileStorage : IFileStorage
{
    /// <summary>
    /// Diagnostic message used by every throw — kept consistent for log greps and
    /// for the SRE runbook's "MinIO unconfigured" entry. Mentions both offending
    /// configuration keys explicitly so operators can resolve the issue without
    /// reading source.
    /// </summary>
    private const string Message =
        "MinIO not configured — set Minio:AccessKey and Minio:SecretKey " +
        "(env vars Minio__AccessKey / Minio__SecretKey) before reading or writing " +
        "any object. Credentials are sourced from the secrets manager per CLAUDE.md §1.8.";

    /// <inheritdoc />
    public Task<Result<StoredObject>> PutAsync(
        string bucket,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<Result<Stream>> GetAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<Result<Uri>> PresignDownloadAsync(
        string bucket,
        string objectKey,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);
}
