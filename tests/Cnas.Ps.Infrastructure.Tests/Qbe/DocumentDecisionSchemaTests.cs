using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Infrastructure.Qbe;

namespace Cnas.Ps.Infrastructure.Tests.Qbe;

/// <summary>
/// R0671 continuation — verifies <see cref="QbeRegistrySchemaProvider"/> exposes the
/// newly added Document and Decision registry schemas so the QBE converter can bind.
/// </summary>
public sealed class DocumentDecisionSchemaTests
{
    /// <summary>Field names asserted to exist on the Document schema.</summary>
    private static readonly string[] ExpectedDocumentFields =
        new[] { "Id", "DossierId", "Kind", "MimeType", "SizeBytes", "CreatedAtUtc" };

    /// <summary>Field names asserted to exist on the Decision schema.</summary>
    private static readonly string[] ExpectedDecisionFields =
        new[] { "Id", "ApplicationId", "CreatedAtUtc", "ClosedAtUtc" };

    /// <summary>The provider returns a Document schema with the documented fields.</summary>
    [Fact]
    public void GetForRegistry_Document_ReturnsSchemaWithCanonicalFields()
    {
        var sut = new QbeRegistrySchemaProvider();

        var schema = sut.GetForRegistry(QueryBudgetRegistries.Document);

        schema.Should().NotBeNull();
        var fieldNames = schema!.Fields.Select(f => f.FieldName).ToList();
        fieldNames.Should().Contain(ExpectedDocumentFields);
    }

    /// <summary>The provider returns a Decision schema with the documented fields.</summary>
    [Fact]
    public void GetForRegistry_Decision_ReturnsSchemaWithCanonicalFields()
    {
        var sut = new QbeRegistrySchemaProvider();

        var schema = sut.GetForRegistry(QueryBudgetRegistries.Decision);

        schema.Should().NotBeNull();
        var fieldNames = schema!.Fields.Select(f => f.FieldName).ToList();
        fieldNames.Should().Contain(ExpectedDecisionFields);
    }

    /// <summary>QueryBudgetRegistries exposes the Document + Decision constants.</summary>
    [Fact]
    public void QueryBudgetRegistries_ExposesDocumentAndDecisionConstants()
    {
        QueryBudgetRegistries.Document.Should().Be("Document");
        QueryBudgetRegistries.Decision.Should().Be("Decision");
        QueryBudgetRegistries.All.Should().Contain("Document");
        QueryBudgetRegistries.All.Should().Contain("Decision");
    }
}
