using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0117 / CF 14.11 — unit tests for the PGD publish-input validator.
/// </summary>
public sealed class PgdValidatorTests
{
    private static PgdDatasetPublishInputDto Good(
        string? datasetCode = null,
        string? title = null,
        string? description = null,
        string? payload = null,
        string? contentType = null) => new(
            DatasetCode: datasetCode ?? "stat.beneficiaries",
            Title: title ?? "Beneficiar count",
            Description: description ?? "Aggregated counts by region.",
            PayloadJson: payload ?? "{\"rows\":[]}",
            ContentType: contentType ?? "application/json");

    [Fact]
    public void HappyPath_Accepted()
    {
        var v = new PgdDatasetPublishInputDtoValidator();
        v.Validate(Good()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyDatasetCode_Rejected()
    {
        var v = new PgdDatasetPublishInputDtoValidator();
        v.Validate(Good(datasetCode: "")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void OversizePayload_Rejected()
    {
        var v = new PgdDatasetPublishInputDtoValidator();
        var bigPayload = new string('a', PgdDatasetPublishInputDtoValidator.MaxPayloadLength + 1);
        v.Validate(Good(payload: bigPayload)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void EmptyContentType_Rejected()
    {
        var v = new PgdDatasetPublishInputDtoValidator();
        v.Validate(Good(contentType: "")).IsValid.Should().BeFalse();
    }
}
