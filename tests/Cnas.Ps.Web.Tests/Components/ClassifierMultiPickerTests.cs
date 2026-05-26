using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Web.Tests.Components;

/// <summary>
/// R0403 / CF 17.08 — bUnit tests for the multi-select
/// <see cref="ClassifierMultiPicker"/> companion to
/// <see cref="ClassifierPicker"/>. Shares the same <see cref="IClassifierLookup"/>
/// contract and active-only invariant; differs in that the rendered control is
/// a checkbox list and the bound model is a <c>HashSet&lt;string&gt;</c> of
/// codes.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests were authored BEFORE the component
/// existed.
/// </remarks>
public sealed class ClassifierMultiPickerTests : TestContext
{
    /// <summary>
    /// In-memory fake of <see cref="IClassifierLookup"/>. See
    /// <c>ClassifierPickerTests.FakeLookup</c> for the rationale — kept in two
    /// files so each suite is reaable in isolation.
    /// </summary>
    private sealed class FakeLookup : IClassifierLookup
    {
        private readonly IReadOnlyList<ClassifierRow> _rows;

        public FakeLookup(IReadOnlyList<ClassifierRow> rows) => _rows = rows;

        public Task<IReadOnlyList<ClassifierRow>> GetActiveAsync(string scheme, CancellationToken cancellationToken = default)
            => Task.FromResult(_rows);
    }

    /// <summary>
    /// The multi-picker renders one checkbox per active row supplied by the
    /// lookup; pre-checked state is driven by the initial <c>Values</c>
    /// parameter.
    /// </summary>
    [Fact]
    public void MultiPicker_RendersCheckboxesFromInjectedLookup()
    {
        var lookup = new FakeLookup(new[]
        {
            new ClassifierRow("CFOJ", "MD-CH", "Chișinău", null, null, null, "national"),
            new ClassifierRow("CFOJ", "MD-BL", "Bălți", null, null, null, "national"),
            new ClassifierRow("CFOJ", "MD-CA", "Cahul", null, null, null, "national"),
        });
        Services.AddSingleton<IClassifierLookup>(lookup);

        var cut = RenderComponent<ClassifierMultiPicker>(p => p
            .Add(c => c.SchemeCode, "CFOJ")
            .Add(c => c.Values, new HashSet<string> { "MD-CH" }));

        cut.WaitForAssertion(() =>
        {
            var checkboxes = cut.FindAll("[data-testid='classifier-checkbox']");
            checkboxes.Count.Should().Be(3);

            // The MD-CH checkbox should render in the checked state.
            var checkedItems = cut.FindAll("[data-testid='classifier-checkbox'][checked]");
            checkedItems.Count.Should().Be(1);
        });
    }

    /// <summary>
    /// Empty active set → the multi-picker renders the empty-state container
    /// and zero checkboxes. Mirrors the single-picker contract so consumer
    /// pages can share branchless markup.
    /// </summary>
    [Fact]
    public void MultiPicker_WhenLookupReturnsNoRows_RendersEmptyState()
    {
        var lookup = new FakeLookup(Array.Empty<ClassifierRow>());
        Services.AddSingleton<IClassifierLookup>(lookup);

        var cut = RenderComponent<ClassifierMultiPicker>(p => p.Add(c => c.SchemeCode, "CFOJ"));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='classifier-checkbox']").Count.Should().Be(0);
            cut.Find("[data-testid='classifier-empty']").Should().NotBeNull();
        });
    }

    /// <summary>
    /// Toggling a checkbox raises the <c>OnValuesChanged</c> callback with the
    /// updated set of selected codes. Tests the additive case — a previously
    /// empty set picks up the newly-toggled code.
    /// </summary>
    [Fact]
    public void MultiPicker_WhenCheckboxToggled_RaisesOnValuesChangedWithUpdatedSet()
    {
        var lookup = new FakeLookup(new[]
        {
            new ClassifierRow("CFOJ", "MD-CH", "Chișinău", null, null, null, "national"),
            new ClassifierRow("CFOJ", "MD-BL", "Bălți", null, null, null, "national"),
        });
        Services.AddSingleton<IClassifierLookup>(lookup);

        IReadOnlySet<string>? captured = null;
        var cut = RenderComponent<ClassifierMultiPicker>(p => p
            .Add(c => c.SchemeCode, "CFOJ")
            .Add(c => c.Values, new HashSet<string>())
            .Add(c => c.OnValuesChanged, vs => captured = vs));

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='classifier-checkbox']").Count.Should().Be(2));

        // Click the second checkbox (Bălți).
        var checkboxes = cut.FindAll("[data-testid='classifier-checkbox']");
        checkboxes[1].Change(true);

        captured.Should().NotBeNull();
        captured!.Should().Contain("MD-BL");
    }
}
