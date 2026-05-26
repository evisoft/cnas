using System.Linq;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Migration;

namespace Cnas.Ps.Infrastructure.Tests.Migration;

/// <summary>
/// R2431 / TOR M4 — tests for <see cref="IdentityMigrationRecordMapper"/>.
/// </summary>
public sealed class IdentityMigrationRecordMapperTests
{
    [Fact]
    public async Task MapAsync_PassesFieldsThrough()
    {
        var mapper = new IdentityMigrationRecordMapper();
        var record = MigrationTestHelpers.NewRecord("fp-1", ("name", "X"), ("value", 7));
        var plan = new MigrationPlan { TargetEntityName = "Pension" };

        var result = await mapper.MapAsync(record, plan);

        result.IsSuccess.Should().BeTrue();
        result.Value.TargetEntityKey.Should().Be("fp-1");
        result.Value.FieldsJson.Should().Contain("\"name\"");
        result.Value.FieldsJson.Should().Contain("\"value\"");
    }

    [Fact]
    public async Task MapAsync_EmitsUncustomisedFinding()
    {
        var mapper = new IdentityMigrationRecordMapper();
        var record = MigrationTestHelpers.NewRecord("fp-2");
        var plan = new MigrationPlan { TargetEntityName = "Pension" };

        var result = await mapper.MapAsync(record, plan);

        result.IsSuccess.Should().BeTrue();
        result.Value.Findings.Should().HaveCount(1);
        var finding = result.Value.Findings[0];
        finding.Severity.Should().Be(MigrationFindingSeverity.Info);
        finding.FindingCode.Should().Be(IdentityMigrationRecordMapper.UncustomisedFindingCode);
    }
}
