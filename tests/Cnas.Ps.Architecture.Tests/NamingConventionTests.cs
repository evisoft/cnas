using System.Reflection;
using NetArchTest.Rules;

namespace Cnas.Ps.Architecture.Tests;

/// <summary>
/// Naming-convention guardrails. CLAUDE.md §1.5 mandates that style be enforced
/// during build, not just in IDE. These tests enforce two house rules that the
/// Roslyn analyzers don't cover out-of-the-box:
///   1. Public Task/ValueTask-returning methods end with the <c>Async</c> suffix.
///   2. Public interfaces start with <c>I</c> followed by an uppercase letter.
/// </summary>
public class NamingConventionTests
{
    /// <summary>
    /// The six SRC assemblies under analysis. We pin each one via a known marker type so
    /// the test compiles against the project references and not via file-system probing.
    /// </summary>
    private static readonly Assembly[] SrcAssemblies =
    [
        typeof(Cnas.Ps.Core.Common.Result).Assembly,
        typeof(Cnas.Ps.Contracts.PageRequest).Assembly,
        typeof(Cnas.Ps.Application.ApplicationAssemblyMarker).Assembly,
        typeof(Cnas.Ps.Infrastructure.InfrastructureServiceCollectionExtensions).Assembly,
        typeof(Cnas.Ps.Api.Controllers.ApplicationsController).Assembly,
    ];

    /// <summary>
    /// RATCHET — methods that satisfy a third-party interface contract whose name we cannot
    /// rename (e.g. Quartz <c>IJob.Execute</c>). Each entry is "AssemblyQualifiedTypeFullName.MethodName".
    /// Keep this list as small as possible and document the upstream contract.
    /// </summary>
    private static readonly HashSet<string> AsyncSuffixRatchet = new(StringComparer.Ordinal)
    {
        // RATCHET: Quartz IJob.Execute is declared by Quartz.IJob and cannot be renamed.
        "Cnas.Ps.Infrastructure.Jobs.DossierSlaMonitorJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.MPayDispatcherJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.MConnectSyncJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.MakerCheckerExpirySweeper.Execute",
        "Cnas.Ps.Infrastructure.Jobs.AuditArchiveReplayJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.MissingDocsSlaJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.UnclaimedTaskEscalationJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.SiemForwarderJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.SecurityAlertEvaluatorJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.BulkSelectionCleanupJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.UserAbsenceLifecycleJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.ProfileRefreshScheduledJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.KpiSnapshotJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.ContributorPeriodProjectionJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.ReportJobBackgroundJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.ReportJobOverrunMonitorJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.TreasuryDistributionJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.PenaltyRepaymentDefaultDetectionJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.DailyBassReceiptsSummaryJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.SessionAutoLockJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.IntegrityCheckJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.MassRecalculationApplyJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.OfflineBatchProcessingJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.TreasuryFeedImportJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.SensitiveAdminActionExpirySweepJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.ClassificationCatalogSnapshotJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.TemplateLanguageCoverageScanJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.MigrationDryRunJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.BackupExecutionJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.BackupRetentionSweepJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.SupportTicketSlaEvaluationJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.SystemUpdateNotificationJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.QualityRiskReviewSweepJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.RspIngestionJob.Execute",
        "Cnas.Ps.Infrastructure.Jobs.RecurrentPaymentJob.Execute",
        // RATCHET: Quartz IJobListener method names are dictated by the upstream interface
        // (Quartz.IJobListener) — they must match exactly for the listener manager to
        // dispatch lifecycle callbacks. See FailedJobListener (DLQ for Quartz failures).
        "Cnas.Ps.Infrastructure.Jobs.FailedJobListener.JobToBeExecuted",
        "Cnas.Ps.Infrastructure.Jobs.FailedJobListener.JobExecutionVetoed",
        "Cnas.Ps.Infrastructure.Jobs.FailedJobListener.JobWasExecuted",
    };

    [Fact]
    public void Async_Methods_End_With_AsyncSuffix()
    {
        // NetArchTest does not expose method-return-type filtering, so we fall back to reflection.
        // Walk every public instance/static method on every public type and assert the suffix
        // whenever the return type is Task / Task<T> / ValueTask / ValueTask<T>.
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();

        foreach (var assembly in SrcAssemblies)
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (!type.IsPublic && !type.IsNestedPublic)
                {
                    continue;
                }
                if (type.IsCompilerGenerated())
                {
                    continue;
                }

                foreach (var method in type.GetMethods(Flags))
                {
                    if (method.IsSpecialName) // property/event accessors, op_*, etc.
                    {
                        continue;
                    }
                    if (!ReturnsTaskLike(method.ReturnType))
                    {
                        continue;
                    }
                    if (method.Name.EndsWith("Async", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var key = $"{type.FullName}.{method.Name}";
                    if (AsyncSuffixRatchet.Contains(key))
                    {
                        continue;
                    }

                    offenders.Add(key);
                }
            }
        }

        offenders.Should().BeEmpty(
            "public Task/ValueTask-returning methods must end with the Async suffix (house style). " +
            "If a method satisfies a third-party interface, add it to AsyncSuffixRatchet with a justification comment.");
    }

    [Fact]
    public void Interfaces_Start_With_I()
    {
        // Walk every SRC assembly and assert that public interfaces follow the I<Uppercase> pattern.
        var offenders = new List<string>();

        foreach (var assembly in SrcAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .That()
                .AreInterfaces()
                .And().ArePublic()
                .Should()
                .HaveNameStartingWith("I")
                .GetResult();

            if (!result.IsSuccessful && result.FailingTypeNames is not null)
            {
                offenders.AddRange(result.FailingTypeNames);
            }

            // Additionally enforce the second character is uppercase (e.g. forbid `Iservice`).
            foreach (var type in SafeGetTypes(assembly))
            {
                if (!type.IsInterface || !type.IsPublic)
                {
                    continue;
                }
                var name = type.Name;
                if (name.Length < 2 || name[0] != 'I' || !char.IsUpper(name[1]))
                {
                    offenders.Add(type.FullName ?? name);
                }
            }
        }

        offenders.Distinct().Should().BeEmpty(
            "public interfaces must be named I<Uppercase>... (CLAUDE.md §1.5). Offenders listed above.");
    }

    /// <summary>
    /// Reflection helper that tolerates missing-dependency exceptions surfaced by
    /// <see cref="Assembly.GetTypes"/> on assemblies that lazy-load satellite references.
    /// </summary>
    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
    }

    /// <summary>True when the return type is <see cref="Task"/>, <see cref="Task{T}"/>, or any <c>ValueTask</c> variant.</summary>
    private static bool ReturnsTaskLike(Type returnType)
    {
        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return true;
        }
        if (!returnType.IsGenericType)
        {
            return false;
        }
        var def = returnType.GetGenericTypeDefinition();
        return def == typeof(Task<>) || def == typeof(ValueTask<>);
    }
}

/// <summary>Tiny reflection helpers used by the architecture suite.</summary>
internal static class ReflectionGuards
{
    /// <summary>True when the type was emitted by the compiler (lambdas, iterators, async state machines).</summary>
    public static bool IsCompilerGenerated(this Type type)
        => type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null
           || (type.Name.Contains('<', StringComparison.Ordinal) && type.Name.Contains('>', StringComparison.Ordinal));
}
