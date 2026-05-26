using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2307 / TOR SEC 060 — unit tests for the backup-input validators.
/// Exercises the policy-code regex, cron well-formedness, retention bounds,
/// and the reason / filter envelopes.
/// </summary>
public sealed class BackupInputValidatorTests
{
    private static BackupPolicyCreateInputDto GoodCreate(
        string? policyCode = null,
        string? cron = null,
        int retention = 30) => new(
            PolicyCode: policyCode ?? "DB_FULL",
            DisplayName: "Daily full DB backup",
            Description: "Nightly full pg_dump",
            Scope: "PrimaryDatabase",
            Strategy: "Full",
            CronSchedule: cron ?? "0 0 2 * * ?",
            RetentionDays: retention,
            TargetKind: "InMemoryTest",
            TargetReference: "bucket/db-full");

    [Fact]
    public void CreatePolicy_HappyPath_Accepted()
    {
        var v = new BackupPolicyCreateInputValidator();
        v.Validate(GoodCreate()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreatePolicy_BadPolicyCode_Rejected()
    {
        var v = new BackupPolicyCreateInputValidator();
        v.Validate(GoodCreate(policyCode: "lower_case")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreatePolicy_RetentionOutOfRange_Rejected()
    {
        var v = new BackupPolicyCreateInputValidator();
        v.Validate(GoodCreate(retention: 0)).IsValid.Should().BeFalse();
        v.Validate(GoodCreate(retention: 9999)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreatePolicy_InvalidCron_Rejected()
    {
        var v = new BackupPolicyCreateInputValidator();
        v.Validate(GoodCreate(cron: "not a cron")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreatePolicy_BadScopeEnum_Rejected()
    {
        var v = new BackupPolicyCreateInputValidator();
        var bad = GoodCreate() with { Scope = "PlanetaryDatabase" };
        v.Validate(bad).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ModifyPolicy_RequiresChangeReason()
    {
        var v = new BackupPolicyModifyInputValidator();
        var bad = new BackupPolicyModifyInputDto(null, null, null, 7, null, ChangeReason: "");
        v.Validate(bad).IsValid.Should().BeFalse();

        var good = new BackupPolicyModifyInputDto(null, null, null, 7, null, ChangeReason: "ops");
        v.Validate(good).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RunFilter_Take_Bounded()
    {
        var v = new BackupRunFilterValidator();
        v.Validate(new BackupRunFilterDto(Take: 200)).IsValid.Should().BeFalse();
        v.Validate(new BackupRunFilterDto(Take: 50)).IsValid.Should().BeTrue();
    }
}
