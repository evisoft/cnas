using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Integrity.Checks;

namespace Cnas.Ps.Infrastructure.Tests.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariant tests for
/// <see cref="UserProfileNationalIdHashSyncCheck"/>. Verifies that any user
/// with a NationalId also has a shadow hash.
/// </summary>
public sealed class UserProfileNationalIdHashSyncCheckTests
{
    private static UserProfile NewUser(string displayName, string? nationalId, string? hash)
        => new()
        {
            DisplayName = displayName,
            NationalId = nationalId,
            NationalIdHash = hash,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        };

    [Fact]
    public async Task RunAsync_HashPresent_NoFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        db.UserProfiles.Add(NewUser("Alice", "2000000000007", "hash-alice"));
        await db.SaveChangesAsync();

        var check = new UserProfileNationalIdHashSyncCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_HashMissing_ProducesCriticalFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var row = NewUser("Bob", "2000000000008", hash: null);
        db.UserProfiles.Add(row);
        await db.SaveChangesAsync();

        var check = new UserProfileNationalIdHashSyncCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.Findings.Should().HaveCount(1);
        result.Findings[0].Severity.Should().Be(IntegrityFindingSeverity.Critical);
        result.Findings[0].CheckCode.Should().Be("USER_PROFILE.NATIONAL_ID_HASH_MISSING");
        result.Findings[0].AggregateRowId.Should().Be(row.Id);
    }

    [Fact]
    public async Task RunAsync_NoNationalId_NoFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        db.UserProfiles.Add(NewUser("Carol", nationalId: null, hash: null));
        await db.SaveChangesAsync();

        var check = new UserProfileNationalIdHashSyncCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.Findings.Should().BeEmpty();
    }
}
