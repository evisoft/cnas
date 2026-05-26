namespace Cnas.Ps.Contracts;

/// <summary>Classifier row DTO (UC17).</summary>
public sealed record ClassifierRow(
    string Kind,
    string Code,
    string LabelRo,
    string? LabelEn,
    string? LabelRu,
    string? ParentCode,
    string Source);
