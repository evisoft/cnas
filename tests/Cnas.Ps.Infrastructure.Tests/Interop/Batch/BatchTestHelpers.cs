using System.Security.Cryptography;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Interop;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts.Interop;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Interop.Batch;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — shared helpers for the offline-batch test suite.
/// </summary>
internal static class BatchTestHelpers
{
    /// <summary>Canonical "now" used across the batch tests.</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 4, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-offline-batch-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Test-only fixed UTC clock.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }

    /// <summary>Returns a Sqid mock that round-trips "SQID-{id}".</summary>
    public static ISqidService NewSqidMock()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    /// <summary>Returns an audit mock that captures every event code.</summary>
    public static IAuditService NewAuditCapturing(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => { list.Add(c.ArgAt<string>(0)); return Task.FromResult(Result.Success()); });
        return a;
    }

    /// <summary>Returns a caller-context mock.</summary>
    public static ICallerContext NewCaller(string? subject = null)
    {
        var c = Substitute.For<ICallerContext>();
        c.UserSqid.Returns(subject);
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-batch");
        return c;
    }

    /// <summary>Returns a signer wired with a fixed dev key.</summary>
    public static IBatchResponseSigner NewSigner()
    {
        var opts = Options.Create(new BatchResponseSigningOptions
        {
            HmacKeyBase64 = Convert.ToBase64String(new byte[] { 0xA, 0xB, 0xC, 0xD, 0xE, 0xF }),
        });
        return new HmacSha256BatchResponseSigner(opts);
    }

    /// <summary>Builds the submission service ready for tests.</summary>
    public static OfflineBatchSubmissionService NewService(
        CnasDbContext db,
        IAuditService audit,
        IOfflineBatchBlobStore blobs,
        IOfflineBatchRequestParser parser)
        => new(
            db: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            blobs: blobs,
            parser: parser,
            submitValidator: new OfflineBatchSubmissionInputValidator(),
            reasonValidator: new OfflineBatchReasonInputValidator(),
            listFilterValidator: new OfflineBatchSubmissionFilterValidator(),
            rowFilterValidator: new OfflineBatchRowFilterValidator());

    /// <summary>Builds the processor ready for tests with a stub interop API.</summary>
    public static OfflineBatchProcessor NewProcessor(
        CnasDbContext db,
        IAuditService audit,
        IOfflineBatchBlobStore blobs,
        IBatchResponseSigner signer,
        IInteropApi interop)
        => new(
            db: db,
            clock: new StubClock(ClockNow),
            sqids: NewSqidMock(),
            caller: NewCaller(),
            audit: audit,
            blobs: blobs,
            schemas: new OfflineBatchOpSchemaRegistry(),
            signer: signer,
            interop: interop,
            logger: NullLogger<OfflineBatchProcessor>.Instance);

    /// <summary>Computes the SHA-256 hex digest of the supplied bytes (lower-case).</summary>
    public static string Sha256Hex(byte[] bytes)
    {
        var d = SHA256.HashData(bytes);
        var sb = new System.Text.StringBuilder(d.Length * 2);
        for (int i = 0; i < d.Length; i++)
        {
            sb.Append(d[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Builds a tiny GetInsuredPersonStatus CSV with the supplied IDNPs.</summary>
    public static byte[] BuildGetInsuredPersonStatusCsv(params string[] idnps)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Idnp");
        foreach (var i in idnps) { sb.AppendLine(i); }
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Builds an InteropApi mock that always returns a NotFound for GetInsuredPersonStatus.</summary>
    public static IInteropApi NewNotFoundInteropApi()
    {
        var api = Substitute.For<IInteropApi>();
        api.GetInsuredPersonStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<InsuredPersonStatusDto>.Failure(ErrorCodes.NotFound, "not on file")));
        return api;
    }

    /// <summary>Builds an InteropApi mock that returns a successful DTO for every IDNP.</summary>
    public static IInteropApi NewSuccessInteropApi()
    {
        var api = Substitute.For<IInteropApi>();
        var dto = new InsuredPersonStatusDto(
            IdnpHashPrefix: "deadbeef",
            IsRegistered: true,
            AccountCode: "PA-1",
            ActiveBenefitsCount: 0,
            AsOfUtc: ClockNow);
        api.GetInsuredPersonStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<InsuredPersonStatusDto>.Success(dto)));
        return api;
    }
}
