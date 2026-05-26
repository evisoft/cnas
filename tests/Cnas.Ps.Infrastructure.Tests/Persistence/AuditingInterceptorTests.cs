using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// R0184 / TOR SEC 042 — tests for
/// <see cref="AuditingInterceptor"/>. Verifies that Added / Modified / Deleted
/// entities tagged with <see cref="AutoAuditAttribute"/> produce audit rows,
/// that non-tagged entities are skipped, and that the hardcoded
/// <see cref="AuditingInterceptor.ExcludedPropertyNames"/> backstop redacts
/// password / national-id fields from the diff payload.
/// </summary>
public sealed class AuditingInterceptorTests
{
    /// <summary>Captured audit calls so assertions can inspect what the interceptor emitted.</summary>
    private sealed record AuditCall(
        string EventCode,
        AuditSeverity Severity,
        string ActorId,
        string? TargetEntity,
        long? TargetEntityId,
        string DetailsJson);

    private static (CnasDbContext db, List<AuditCall> calls) NewContextWithInterceptor()
    {
        var calls = new List<AuditCall>();

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                calls.Add(new AuditCall(
                    EventCode: c.ArgAt<string>(0),
                    Severity: c.ArgAt<AuditSeverity>(1),
                    ActorId: c.ArgAt<string>(2),
                    TargetEntity: c.ArgAt<string?>(3),
                    TargetEntityId: c.ArgAt<long?>(4),
                    DetailsJson: c.ArgAt<string>(5)));
                return Task.FromResult(Result.Success());
            });

        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(7L);
        caller.UserSqid.Returns("SQID-7");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-test");

        var interceptor = new AuditingInterceptor(
            NullLogger<AuditingInterceptor>.Instance, audit, caller);

        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"audit-interceptor-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor)
            .Options;
        return (new CnasDbContext(opts), calls);
    }

    [Fact]
    public async Task Added_AutoAuditEntity_EmitsCreatedRow()
    {
        var (db, calls) = NewContextWithInterceptor();

        db.UserProfiles.Add(new UserProfile
        {
            DisplayName = "Alice Admin",
            CreatedAtUtc = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().Contain(c => c.EventCode == "USERPROFILE.CREATED");
        calls.Should().Contain(c => c.TargetEntity == nameof(UserProfile));
    }

    [Fact]
    public async Task Modified_AutoAuditEntity_EmitsModifiedRow_WithDiffJson()
    {
        var (db, calls) = NewContextWithInterceptor();

        var u = new UserProfile
        {
            DisplayName = "Initial",
            CreatedAtUtc = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc),
        };
        db.UserProfiles.Add(u);
        await db.SaveChangesAsync(CancellationToken.None);
        calls.Clear();

        u.DisplayName = "Renamed";
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().Contain(c => c.EventCode == "USERPROFILE.MODIFIED");
        var modify = calls.Find(c => c.EventCode == "USERPROFILE.MODIFIED");
        modify!.DetailsJson.Should().Contain("DisplayName");
        modify.DetailsJson.Should().Contain("Renamed");
    }

    [Fact]
    public async Task Deleted_AutoAuditEntity_EmitsDeletedRow_WithSnapshot()
    {
        var (db, calls) = NewContextWithInterceptor();

        var u = new UserProfile
        {
            DisplayName = "ToDelete",
            CreatedAtUtc = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc),
        };
        db.UserProfiles.Add(u);
        await db.SaveChangesAsync(CancellationToken.None);
        calls.Clear();

        db.UserProfiles.Remove(u);
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().Contain(c => c.EventCode == "USERPROFILE.DELETED");
        var del = calls.Find(c => c.EventCode == "USERPROFILE.DELETED");
        del!.DetailsJson.Should().Contain("snapshot");
        del.DetailsJson.Should().Contain("ToDelete");
    }

    [Fact]
    public async Task NonAutoAuditEntity_Skipped()
    {
        var (db, calls) = NewContextWithInterceptor();

        // Notification is a plain AuditableEntity with no [AutoAudit] marker —
        // the interceptor must NOT emit an audit row for it.
        db.Notifications.Add(new Notification
        {
            RecipientUserId = 1L,
            Channel = NotificationChannel.InApp,
            Subject = "Test",
            Body = "Plain notification — no auto-audit.",
            CreatedAtUtc = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SensitiveFields_RedactedFromDiff_ByExcludedPropertyNames()
    {
        var (db, calls) = NewContextWithInterceptor();

        var u = new UserProfile
        {
            DisplayName = "Alice",
            LocalPasswordHash = "OLD_HASH_VALUE",
            NationalId = "OLD_NATIONAL_ID",
            Email = "alice@example.com",
            CreatedAtUtc = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc),
        };
        db.UserProfiles.Add(u);
        await db.SaveChangesAsync(CancellationToken.None);
        calls.Clear();

        u.LocalPasswordHash = "NEW_HASH_VALUE";
        u.NationalId = "NEW_NATIONAL_ID";
        u.Email = "alice@new.example.com";
        u.DisplayName = "Alice Renamed";
        await db.SaveChangesAsync(CancellationToken.None);

        var modify = calls.Find(c => c.EventCode == "USERPROFILE.MODIFIED");
        modify.Should().NotBeNull();
        modify!.DetailsJson.Should().NotContain("OLD_HASH_VALUE");
        modify.DetailsJson.Should().NotContain("NEW_HASH_VALUE");
        modify.DetailsJson.Should().NotContain("OLD_NATIONAL_ID");
        modify.DetailsJson.Should().NotContain("NEW_NATIONAL_ID");
        modify.DetailsJson.Should().NotContain("alice@example.com");
        modify.DetailsJson.Should().NotContain("alice@new.example.com");
        // DisplayName is NOT on the excluded list — must appear in the diff.
        modify.DetailsJson.Should().Contain("DisplayName");
        modify.DetailsJson.Should().Contain("Alice Renamed");
    }

    [Fact]
    public void ExcludedPropertyNames_CoversCanonicalPiiAndCredentialFields()
    {
        // Spot-check the backstop list so reviewers can see the contract.
        AuditingInterceptor.ExcludedPropertyNames.Should().Contain("LocalPasswordHash");
        AuditingInterceptor.ExcludedPropertyNames.Should().Contain("NationalId");
        AuditingInterceptor.ExcludedPropertyNames.Should().Contain("Idnp");
        AuditingInterceptor.ExcludedPropertyNames.Should().Contain("Idno");
        AuditingInterceptor.ExcludedPropertyNames.Should().Contain("BankIban");
        AuditingInterceptor.ExcludedPropertyNames.Should().Contain("IpAddress");
        AuditingInterceptor.ExcludedPropertyNames.Should().Contain("RefreshTokenHash");
    }

    /// <summary>
    /// R0057 / TOR SEC 026 — <see cref="DelegationGrant"/> is marked
    /// <c>[AutoAudit(EventCodePrefix = "DELEGATION")]</c>. Adding a row must
    /// emit a <c>DELEGATION.CREATED</c> audit event automatically.
    /// </summary>
    [Fact]
    public async Task DelegationGrant_AutoAudit_EmitsCreated()
    {
        var (db, calls) = NewContextWithInterceptor();

        db.DelegationGrants.Add(new DelegationGrant
        {
            GrantorUserId = 1L,
            DelegateeUserId = 2L,
            ValidFromUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
            ValidToUtc = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc),
            Scope = "approve.executory_documents",
            GrantedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().Contain(c => c.EventCode == "DELEGATION.CREATED");
        calls.Should().Contain(c => c.TargetEntity == nameof(DelegationGrant));
    }

    /// <summary>
    /// R1504 / TOR §3.7-E — <see cref="PaymentSuspensionRecord"/> emits
    /// <c>PAYMENT_SUSPENSION.CREATED</c> on insert.
    /// </summary>
    [Fact]
    public async Task PaymentSuspensionRecord_AutoAudit_EmitsCreated()
    {
        var (db, calls) = NewContextWithInterceptor();

        db.PaymentSuspensionRecords.Add(new PaymentSuspensionRecord
        {
            DecisionId = 1L,
            SuspendedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
            SuspendedByUserId = 7L,
            SuspensionReason = "Test suspension",
            CreatedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().Contain(c => c.EventCode == "PAYMENT_SUSPENSION.CREATED");
        calls.Should().Contain(c => c.TargetEntity == nameof(PaymentSuspensionRecord));
    }

    /// <summary>
    /// R0933 / TOR §3.6 §10.1 — <see cref="DecisionSupersession"/> emits
    /// <c>DECISION_SUPERSESSION.CREATED</c> on insert.
    /// </summary>
    [Fact]
    public async Task DecisionSupersession_AutoAudit_EmitsCreated()
    {
        var (db, calls) = NewContextWithInterceptor();

        db.DecisionSupersessions.Add(new DecisionSupersession
        {
            PreviousDecisionId = 100L,
            NewDecisionId = 200L,
            SupersededAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().Contain(c => c.EventCode == "DECISION_SUPERSESSION.CREATED");
        calls.Should().Contain(c => c.TargetEntity == nameof(DecisionSupersession));
    }

    /// <summary>
    /// R0673 / TOR CF 18.12 — <see cref="GranularPermissionAssignment"/>
    /// emits <c>GRANULAR_PERM.CREATED</c> on insert.
    /// </summary>
    [Fact]
    public async Task GranularPermissionAssignment_AutoAudit_EmitsCreated()
    {
        var (db, calls) = NewContextWithInterceptor();

        db.GranularPermissionAssignments.Add(new GranularPermissionAssignment
        {
            RoleCode = "cnas-decider",
            ResourceType = "Dossier",
            PermissionVerb = "View",
            GrantedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().Contain(c => c.EventCode == "GRANULAR_PERM.CREATED");
        calls.Should().Contain(c => c.TargetEntity == nameof(GranularPermissionAssignment));
    }

    /// <summary>
    /// R0137 — <see cref="FileImmutabilityRecord"/> emits
    /// <c>FILE_IMMUTABILITY.CREATED</c> on insert.
    /// </summary>
    [Fact]
    public async Task FileImmutabilityRecord_AutoAudit_EmitsCreated()
    {
        var (db, calls) = NewContextWithInterceptor();

        db.FileImmutabilityRecords.Add(new FileImmutabilityRecord
        {
            Bucket = "documents",
            ObjectKey = "decisions/2026/001.pdf",
            MarkedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().Contain(c => c.EventCode == "FILE_IMMUTABILITY.CREATED");
        calls.Should().Contain(c => c.TargetEntity == nameof(FileImmutabilityRecord));
    }

    /// <summary>
    /// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — <see cref="InsolvencyCase"/>
    /// emits <c>INSOLVENCY.CREATED</c> on insert.
    /// </summary>
    [Fact]
    public async Task InsolvencyCase_AutoAudit_EmitsCreated()
    {
        var (db, calls) = NewContextWithInterceptor();

        db.InsolvencyCases.Add(new InsolvencyCase
        {
            ContributorId = 1L,
            InsolvencyDate = new DateOnly(2026, 5, 20),
            Reason = "Hotărâre judecătorească nr. 1234/2026",
            OpenedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        calls.Should().Contain(c => c.EventCode == "INSOLVENCY.CREATED");
        calls.Should().Contain(c => c.TargetEntity == nameof(InsolvencyCase));
    }

    /// <summary>
    /// R0184 — concurrency invariant. The interceptor must observe one
    /// <c>SavingChangesAsync</c> at a time per Scoped instance. A second
    /// fire before the first drains throws a clear diagnostic
    /// <see cref="InvalidOperationException"/> rather than corrupting state
    /// silently. Pins the fix for the
    /// <c>Task.WhenAll(saveTasks)</c> abuse path.
    /// </summary>
    [Fact]
    public async Task ConcurrentSavingChanges_ThrowsClearDiagnostic()
    {
        // Direct interceptor unit test — we don't need a full EF pipeline.
        // The guard is in SavingChangesAsync's CAS on _saveInFlight.
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        var interceptor = new AuditingInterceptor(
            NullLogger<AuditingInterceptor>.Instance, audit, caller: null);
        // Build a real DbContext but call SavingChangesAsync directly twice
        // without an intervening SavedChangesAsync to simulate concurrent
        // SaveChanges fires.
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"audit-conc-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var ctx = new CnasDbContext(opts);
        var eventData = new Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData(
            eventDefinition: null!, messageGenerator: null!, context: ctx);

        // First SavingChanges claims the slot.
        await interceptor.SavingChangesAsync(
            eventData, default, CancellationToken.None);

        // Second SavingChanges without an intervening Saved/Failed must throw.
        var act = async () => await interceptor.SavingChangesAsync(
            eventData, default, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*concurrent SaveChanges*");
    }
}
