using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Reporting;

namespace Cnas.Ps.Infrastructure.Tests.Reporting;

/// <summary>
/// R1906 / TOR Annex 6 — tests for <see cref="ReportRecipientResolver"/>.
/// Verifies the User-kind happy path emits one row, the Group-kind path
/// fans out across direct memberships, and the EmailAddress path is a
/// pass-through.
/// </summary>
public sealed class ReportRecipientResolverTests
{
    [Fact]
    public async Task EmailAddressKind_PassesThroughAsSingleRecipient()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        var sqids = ReportDistributionTestHelpers.NewSqidMock();
        var resolver = new ReportRecipientResolver(db, sqids);

        var rule = new ReportDistributionRule
        {
            ReportCode = "X.Y",
            Channel = ReportDistributionChannel.Email,
            RecipientKind = ReportRecipientKind.EmailAddress,
            RecipientCode = "alpha@example.org",
            Format = ReportDeliveryFormat.Pdf,
            Priority = ReportDeliveryPriority.Normal,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
        };

        var result = await resolver.ResolveAsync(rule);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Address.Should().Be("alpha@example.org");
        result.Value[0].Kind.Should().Be(ReportRecipientKind.EmailAddress);
    }

    [Fact]
    public async Task UserKind_ResolvesToOneRow()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        var user = new UserProfile
        {
            DisplayName = "Alice Operator",
            Email = "alice@example.org",
            LocalLogin = "alice",
            CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
        };
        db.UserProfiles.Add(user);
        await db.SaveChangesAsync();

        var sqids = ReportDistributionTestHelpers.NewSqidMock();
        var resolver = new ReportRecipientResolver(db, sqids);

        var rule = new ReportDistributionRule
        {
            ReportCode = "X.Y",
            Channel = ReportDistributionChannel.InSystem,
            RecipientKind = ReportRecipientKind.User,
            RecipientCode = $"SQID-{user.Id}",
            Format = ReportDeliveryFormat.LinkOnly,
            Priority = ReportDeliveryPriority.Normal,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
        };

        var result = await resolver.ResolveAsync(rule);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Address.Should().Be("alice@example.org");
    }

    [Fact]
    public async Task GroupKind_FansOutAcrossMembers()
    {
        using var db = ReportDistributionTestHelpers.CreateContext();
        var group = new UserGroup
        {
            Code = "OPS_TEAM",
            DisplayName = "Operations Team",
            CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
        };
        var member1 = new UserProfile
        {
            DisplayName = "Member 1", Email = "m1@example.org", LocalLogin = "m1",
            CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
        };
        var member2 = new UserProfile
        {
            DisplayName = "Member 2", Email = "m2@example.org", LocalLogin = "m2",
            CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
        };
        db.UserGroups.Add(group);
        db.UserProfiles.AddRange(member1, member2);
        await db.SaveChangesAsync();
        db.UserGroupMemberships.AddRange(
            new UserGroupMembership
            {
                UserGroupId = group.Id, UserProfileId = member1.Id, CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
            },
            new UserGroupMembership
            {
                UserGroupId = group.Id, UserProfileId = member2.Id, CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
            });
        await db.SaveChangesAsync();

        var sqids = ReportDistributionTestHelpers.NewSqidMock();
        var resolver = new ReportRecipientResolver(db, sqids);

        var rule = new ReportDistributionRule
        {
            ReportCode = "X.Y",
            Channel = ReportDistributionChannel.InSystem,
            RecipientKind = ReportRecipientKind.Group,
            RecipientCode = "OPS_TEAM",
            Format = ReportDeliveryFormat.LinkOnly,
            Priority = ReportDeliveryPriority.Normal,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            CreatedAtUtc = ReportDistributionTestHelpers.ClockNow,
        };

        var result = await resolver.ResolveAsync(rule);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Select(r => r.Address).Should().BeEquivalentTo("m1@example.org", "m2@example.org");
    }
}
