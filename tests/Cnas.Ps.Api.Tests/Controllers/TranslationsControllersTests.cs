using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Localization;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0210 — controller tests for <see cref="TranslationsAdminController"/> and
/// <see cref="TranslationsPublicController"/>. Direct-construction pattern matching
/// the rest of the suite.
/// </summary>
public sealed class TranslationsControllersTests
{
    [Fact]
    public async Task UpsertValue_ReturnsOk_WithSqidRoundTrip()
    {
        const string sqid = "SQID-99";
        const string lang = "ro";
        var dto = new TranslationValueDto(Id: "SQID-77", Language: lang, Text: "Lista cererilor", IsApproved: false, TranslatorNote: null);

        var keys = Substitute.For<ITranslationKeyService>();
        var values = Substitute.For<ITranslationValueService>();
        values.UpsertAsync(sqid, lang, Arg.Any<TranslationValueUpsertDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<TranslationValueDto>.Success(dto));

        var controller = new TranslationsAdminController(keys, values);
        var result = await controller.UpsertValueAsync(
            sqid, lang,
            new TranslationValueUpsertDto("Lista cererilor", null),
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<TranslationValueDto>().Subject;
        body.Id.Should().Be("SQID-77");
        body.Language.Should().Be(lang);
        body.Text.Should().Be("Lista cererilor");
    }

    [Fact]
    public void Public_ResolveTranslation_ReturnsOkWithText()
    {
        var resolver = Substitute.For<ITranslationResolver>();
        resolver.Resolve("pages.x", "ro", Arg.Any<string?>()).Returns("Hello");

        var controller = new TranslationsPublicController(resolver);
        var result = controller.ResolveAsync("pages.x", "ro");

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();
        ok.Value!.GetType().GetProperty("text")!.GetValue(ok.Value).Should().Be("Hello");
    }
}
