using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — input-validation rules for
/// <see cref="ReportJobEnqueueDtoValidator"/>. Each test exercises one branch
/// of the rule set: template Sqid required, format must parse to
/// <see cref="ExportFormat"/>.
/// </summary>
public sealed class ReportJobEnqueueDtoValidatorTests
{
    /// <summary>Canonical valid baseline that tests tweak per scenario.</summary>
    private static ReportJobEnqueueDto Valid() => new(
        ReportTemplateSqid: "SQID-42",
        Format: ExportFormat.Csv.ToString());

    [Fact]
    public void Validate_Baseline_Succeeds()
    {
        var sut = new ReportJobEnqueueDtoValidator();

        var result = sut.Validate(Valid());

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_MissingSqid_Rejects()
    {
        var sut = new ReportJobEnqueueDtoValidator();
        var dto = Valid() with { ReportTemplateSqid = "" };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportJobEnqueueDto.ReportTemplateSqid));
    }

    [Fact]
    public void Validate_UnknownFormat_Rejects()
    {
        var sut = new ReportJobEnqueueDtoValidator();
        var dto = Valid() with { Format = "invalid" };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportJobEnqueueDto.Format));
    }
}
