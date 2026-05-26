using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — tests for
/// <see cref="SupportTicketSlaEvaluationJob"/>.
/// </summary>
public sealed class SupportTicketSlaEvaluationJobTests
{
    private static IJobExecutionContext NewExecCtx()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task Execute_HappyPath_Invokes_Evaluator()
    {
        var evaluator = Substitute.For<ISupportTicketSlaEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<int>.Success(3)));

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ISupportTicketSlaEvaluator)).Returns(evaluator);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);

        var job = new SupportTicketSlaEvaluationJob(factory, NullLogger<SupportTicketSlaEvaluationJob>.Instance);
        await job.Execute(NewExecCtx());

        await evaluator.Received(1).EvaluateAsync(Arg.Any<CancellationToken>());
    }
}
