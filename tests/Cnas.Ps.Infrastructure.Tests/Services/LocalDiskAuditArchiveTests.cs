using System.Text.Json;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="LocalDiskAuditArchive"/> — the on-disk spill area for
/// audit batches whose primary flush failed (R0188). Each test runs against a
/// throw-away temp directory which is cleaned up via <see cref="IDisposable"/>.
/// </summary>
public class LocalDiskAuditArchiveTests : IDisposable
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _root;
    private readonly LocalDiskAuditArchive _archive;

    public LocalDiskAuditArchiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cnas-audit-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        var options = Options.Create(new AuditArchiveOptions { LocalPath = _root });
        _archive = new LocalDiskAuditArchive(options, NullLogger<LocalDiskAuditArchive>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup — leave any stragglers to the OS temp sweeper.
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ArchiveAsync_EmptyBatch_DoesNotWriteFile()
    {
        await _archive.ArchiveAsync(Array.Empty<AuditEventRecord>());

        Directory.GetFiles(_root, "audit-*.json").Should().BeEmpty();
    }

    [Fact]
    public async Task ArchiveAsync_WritesValidJsonFile()
    {
        var batch = new[] { NewRecord("c1"), NewRecord("c2") };

        await _archive.ArchiveAsync(batch);

        var files = Directory.GetFiles(_root, "audit-*.json");
        files.Should().HaveCount(1);

        var json = await File.ReadAllTextAsync(files[0]);
        var roundtrip = JsonSerializer.Deserialize<List<AuditEventRecord>>(json);
        roundtrip.Should().NotBeNull();
        roundtrip!.Select(r => r.CorrelationId).Should().Equal("c1", "c2");
    }

    [Fact]
    public async Task ListPendingAsync_ReturnsArchivedFiles()
    {
        await _archive.ArchiveAsync(new[] { NewRecord("a") });
        await _archive.ArchiveAsync(new[] { NewRecord("b") });

        var pending = await _archive.ListPendingAsync();

        pending.Should().HaveCount(2);
        pending.Select(p => Path.GetFileName(p.Id))
            .Should().AllSatisfy(n => n.Should().StartWith("audit-").And.EndWith(".json"));
    }

    [Fact]
    public async Task ReadAsync_ReturnsArchivedRecords()
    {
        var original = new[] { NewRecord("c1", "EVT.A"), NewRecord("c2", "EVT.B") };
        await _archive.ArchiveAsync(original);

        var pending = await _archive.ListPendingAsync();
        var loaded = await _archive.ReadAsync(pending.Single().Id);

        loaded.Should().HaveCount(2);
        loaded.Select(r => (r.EventCode, r.CorrelationId))
            .Should().Equal(("EVT.A", "c1"), ("EVT.B", "c2"));
    }

    [Fact]
    public async Task ReadAsync_OnMalformedFile_QuarantinesWithCorruptSuffix()
    {
        var corruptPath = Path.Combine(_root, $"audit-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(corruptPath, "{not-valid-json");

        var loaded = await _archive.ReadAsync(corruptPath);

        loaded.Should().BeEmpty();
        File.Exists(corruptPath).Should().BeFalse();
        File.Exists(corruptPath + ".corrupt").Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        await _archive.ArchiveAsync(new[] { NewRecord("x") });
        var pending = await _archive.ListPendingAsync();
        var id = pending.Single().Id;

        await _archive.DeleteAsync(id);

        File.Exists(id).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_OnMissingFile_DoesNotThrow()
    {
        var ghost = Path.Combine(_root, "audit-ghost.json");

        var act = async () => await _archive.DeleteAsync(ghost);

        await act.Should().NotThrowAsync();
    }

    private static AuditEventRecord NewRecord(string correlationId, string eventCode = "TEST.EVT")
        => new(
            EventCode: eventCode,
            Severity: AuditSeverity.Information,
            ActorId: "actor",
            TargetEntity: "Entity",
            TargetEntityId: 1L,
            DetailsJson: "{}",
            SourceIp: "127.0.0.1",
            CorrelationId: correlationId,
            EventAtUtc: ClockNow);
}
