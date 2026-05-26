using System.Text;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Backups;

namespace Cnas.Ps.Infrastructure.Tests.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — tests for <see cref="InMemoryBackupTarget"/>.
/// </summary>
public sealed class InMemoryBackupTargetTests
{
    private static BackupPolicy NewPolicy() => new()
    {
        Id = 1,
        PolicyCode = "DB_FULL",
        TargetKind = BackupTargetKind.InMemoryTest,
    };

    private static BackupPayloadStream NewPayload(string text = "hello")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new BackupPayloadStream(bytes, InMemoryBackupTarget.ComputeSha256Hex(bytes), bytes.LongLength);
    }

    [Fact]
    public async Task Upload_Then_Download_Returns_Same_Bytes_And_Hash()
    {
        var target = new InMemoryBackupTarget();
        var payload = NewPayload("hello-cnas");

        var upload = await target.UploadAsync(NewPolicy(), payload, CancellationToken.None);
        upload.IsSuccess.Should().BeTrue();

        var download = await target.DownloadAsync(upload.Value.StorageKey, CancellationToken.None);
        download.IsSuccess.Should().BeTrue();
        download.Value.Sha256Hex.Should().Be(payload.Sha256Hex);
        download.Value.SizeBytes.Should().Be(payload.SizeBytes);
    }

    [Fact]
    public async Task Download_Unknown_Key_Returns_NotFound()
    {
        var target = new InMemoryBackupTarget();
        var download = await target.DownloadAsync("nope/123", CancellationToken.None);

        download.IsSuccess.Should().BeFalse();
        download.ErrorCode.Should().Be(IBackupTarget.StorageKeyNotFoundCode);
    }

    [Fact]
    public async Task Delete_Removes_The_Payload()
    {
        var target = new InMemoryBackupTarget();
        var upload = await target.UploadAsync(NewPolicy(), NewPayload(), CancellationToken.None);
        upload.IsSuccess.Should().BeTrue();

        var del = await target.DeleteAsync(upload.Value.StorageKey, CancellationToken.None);
        del.IsSuccess.Should().BeTrue();

        var after = await target.DownloadAsync(upload.Value.StorageKey, CancellationToken.None);
        after.IsSuccess.Should().BeFalse();
        after.ErrorCode.Should().Be(IBackupTarget.StorageKeyNotFoundCode);
    }
}
