using System;
using System.IO;
using System.Threading.Tasks;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="LocalDiskBlobStorage"/> — the local-disk implementation of
/// <see cref="Cnas.Ps.Application.Attachments.IBlobStorage"/> used in dev / staging.
/// </summary>
/// <remarks>
/// <para>
/// The most security-sensitive surface here is the path-traversal guard inside
/// <c>ResolveSafe</c>: every key the caller passes must resolve to a path under the
/// configured root. A naive <c>StartsWith(root)</c> check is insufficient because it
/// accepts sibling directories whose names share the root's prefix (e.g.
/// root="C:\storage\blobs" admits "C:\storage\blobsX\..."). The fix wedges
/// <see cref="System.IO.Path.DirectorySeparatorChar"/> between the root and the rest,
/// and the test below pins down that wedge.
/// </para>
/// </remarks>
public class LocalDiskBlobStorageTests : IDisposable
{
    /// <summary>Disposable temp directory holding the storage root for the current test.</summary>
    private readonly string _rootDir;
    /// <summary>Whether <see cref="Dispose"/> has been invoked (idempotency guard).</summary>
    private bool _disposed;

    /// <summary>Creates a fresh temp root per test so the suite stays parallel-safe.</summary>
    public LocalDiskBlobStorageTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "cnas-blob-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Directory.Delete(_rootDir, recursive: true); }
        catch (IOException) { /* best effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds the SUT against the per-test temp root.</summary>
    private LocalDiskBlobStorage BuildSut(string? rootOverride = null)
    {
        var options = Options.Create(new AttachmentOptions
        {
            RootPath = rootOverride ?? _rootDir,
        });
        return new LocalDiskBlobStorage(options);
    }

    /// <summary>
    /// Happy-path sanity check: a benign key resolves and round-trips bytes through
    /// disk. Lays a baseline so the negative tests below clearly isolate the guard.
    /// </summary>
    [Fact]
    public async Task PutAndGet_BenignKey_RoundTripsBytes()
    {
        var sut = BuildSut();
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        await sut.PutAsync("folder/blob.bin", payload);
        var actual = await sut.GetAsync("folder/blob.bin");

        actual.Should().Equal(payload);
    }

    /// <summary>
    /// Traversal via a relative "<c>..</c>" segment must be rejected before any I/O.
    /// This was already enforced by the explicit <c>Contains("..")</c> check and is
    /// re-pinned here so a future refactor cannot drop it.
    /// </summary>
    [Fact]
    public void PutAsync_DotDotSegment_ThrowsUnauthorized()
    {
        var sut = BuildSut();

        var act = async () => await sut.PutAsync("../escape.bin", new byte[] { 0 });

        act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    /// <summary>
    /// Sibling-directory escape: a key that prefixes the root's last segment must NOT
    /// be accepted by the guard. Reproduces the pre-fix bug where
    /// <c>StartsWith(root)</c> matched a sibling whose name began with the root's name
    /// (e.g. root="C:\storage\blobs" was matched by "C:\storage\blobsX\..."). We craft
    /// the key by reaching up to the parent and dropping back into a sibling that
    /// shares the root's prefix.
    /// </summary>
    [Fact]
    public async Task PutAsync_SiblingDirectoryEscape_ThrowsUnauthorized()
    {
        // Build a root whose name has a sibling sharing its prefix on disk.
        // e.g. root = ".../cnas-blob-test-<guid>/blobs"
        //      sibling = ".../cnas-blob-test-<guid>/blobsX/..."
        var root = Path.Combine(_rootDir, "blobs");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(_rootDir, "blobsX"));

        var sut = BuildSut(rootOverride: root);

        // Cross-platform "absolute" sibling escape: feed the *combined* absolute path
        // of the sibling directly. The adapter rejects "..\\..\\" via the explicit
        // dot-dot check, so we use an absolute key form on Windows. On Linux Path.Combine
        // with an absolute right-hand side resolves to that absolute path. Either way
        // the candidate after GetFullPath is "<temp>/blobsX/escape.bin", which prefixes
        // the root "<temp>/blobs" — exactly the case the prefix-only check accepted.
        var siblingAbsolute = Path.Combine(_rootDir, "blobsX", "escape.bin");

        var act = async () => await sut.PutAsync(siblingAbsolute, new byte[] { 0 });

        // The guard must refuse: candidate ("...blobsX/escape.bin") does NOT start with
        // root ("...blobs") + DirectorySeparatorChar, even though it textually starts
        // with the unwedged root string.
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .Where(e => e.Message.Contains("escapes the configured root", StringComparison.Ordinal));
    }
}
