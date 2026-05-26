using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Infrastructure.Qbe;
using Cnas.Ps.Infrastructure.Search;

namespace Cnas.Ps.Infrastructure.Tests.Search;

/// <summary>
/// R0525 / TOR CF 03.08 — heuristic <see cref="SearchSuggestionService"/> tests.
/// When the candidate registry's discriminator field is missing from the supplied QBE
/// filter AND the current row count is over the threshold the service must emit a
/// stable structured suggestion; otherwise it returns an empty list.
/// </summary>
public sealed class SearchSuggestionServiceTests
{
    /// <summary>Builds the SUT against the production schema provider.</summary>
    private static SearchSuggestionService NewService() => new(new QbeRegistrySchemaProvider());

    [Fact]
    public async Task SuggestRefinementsAsync_OverThreshold_AndMissingDiscriminator_EmitsAddStatusFilter()
    {
        var sut = NewService();
        var emptyFilter = new QbeFilter("AND", Array.Empty<QbeCondition>());

        var result = await sut.SuggestRefinementsAsync(
            "Solicitant", emptyFilter, currentRowCount: 1_000, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        result.Value.Should().Contain(s =>
            s.Code == "AddStatusFilter"
            && s.FieldName == "IsActive"
            && s.ReasonCode == "TooBroad");
    }

    [Fact]
    public async Task SuggestRefinementsAsync_UnderThreshold_ReturnsEmpty()
    {
        var sut = NewService();
        var emptyFilter = new QbeFilter("AND", Array.Empty<QbeCondition>());

        var result = await sut.SuggestRefinementsAsync(
            "Solicitant", emptyFilter, currentRowCount: 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task SuggestRefinementsAsync_FilterAlreadyHasDiscriminator_DoesNotSuggestIt()
    {
        var sut = NewService();
        var filterWithStatus = new QbeFilter("AND", new[]
        {
            new QbeCondition("IsActive", QbeOperator.Equals, "true"),
        });

        var result = await sut.SuggestRefinementsAsync(
            "Solicitant", filterWithStatus, currentRowCount: 5_000, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotContain(s => s.FieldName == "IsActive");
    }
}
