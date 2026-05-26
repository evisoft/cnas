using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0528 / TOR CF 03.13 — verifies that <see cref="ServicePassportService.ListAsync"/>
/// honours the diacritic-folded name query (mirrors R0162 Solicitant pattern). The
/// ASCII query <c>"alocatii"</c> must match a passport whose name is
/// <c>"Alocații pentru copii"</c>.
/// </summary>
public sealed class ServicePassportServiceDiacriticTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-sp-diacritic-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static ServicePassportService Build(CnasDbContext db)
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-CALLER");
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        var workflows = Substitute.For<IWorkflowConfigurationService>();
        return new ServicePassportService(db, sqids, clock, caller, audit, workflows);
    }

    [Fact]
    public async Task ListAsync_AsciiQueryMatchesDiacriticPassportName()
    {
        await using var db = CreateContext();
        db.ServicePassports.Add(new ServicePassport
        {
            Id = 1,
            Code = "SP-ALLOC",
            NameRo = "Alocații pentru copii",
            DescriptionRo = "Test passport",
            WorkflowCode = "WF-ALLOC",
            IsEnabled = true,
            IsCurrent = true,
            IsActive = true,
            CreatedAtUtc = ClockNow,
        });
        db.ServicePassports.Add(new ServicePassport
        {
            Id = 2,
            Code = "SP-PENS",
            NameRo = "Pensie pentru limita de vârstă",
            DescriptionRo = "Pension passport",
            WorkflowCode = "WF-PENS",
            IsEnabled = true,
            IsCurrent = true,
            IsActive = true,
            CreatedAtUtc = ClockNow,
        });
        await db.SaveChangesAsync();

        var svc = Build(db);

        var result = await svc.ListAsync("alocatii", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Code.Should().Be("SP-ALLOC");
    }
}
