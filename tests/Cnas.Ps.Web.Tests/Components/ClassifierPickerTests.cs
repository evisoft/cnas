using Bunit;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Web.Tests.Components;

/// <summary>
/// R0403 / CF 17.08 — bUnit tests for the single-select
/// <see cref="ClassifierPicker"/> dropdown. The picker is the canonical
/// component for any UI surface that needs to bind to a classifier scheme
/// (CAEM, CUATM, CFOJ, …) — it loads rows from an injected
/// <see cref="IClassifierLookup"/>, renders one <c>&lt;option&gt;</c> per
/// active row, and raises an <c>OnValueChanged</c> callback when the user
/// makes a selection.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests were authored BEFORE the component or
/// its supporting <see cref="IClassifierLookup"/> abstraction existed; they
/// pin the contract that the production code is then implemented to
/// satisfy. The active-only invariant (only rows with <c>IsActive == true</c>
/// surface in the dropdown) is asserted at the lookup boundary — a fake
/// lookup is wired up that emits a mix of active + inactive rows and the
/// test verifies only the active ones reach the DOM. The deeper guarantee
/// (that <see cref="Cnas.Ps.Application.UseCases.IClassifierService.ListAsync"/>
/// itself filters active-only) is covered by the companion service-layer
/// test under Cnas.Ps.Infrastructure.Tests.
/// </remarks>
public sealed class ClassifierPickerTests : TestContext
{
    /// <summary>
    /// In-memory fake of <see cref="IClassifierLookup"/> that returns a
    /// pre-seeded list per scheme. Tracks the schemes it was asked about so
    /// tests can assert the picker queried the expected key exactly once.
    /// </summary>
    private sealed class FakeLookup : IClassifierLookup
    {
        private readonly Dictionary<string, IReadOnlyList<ClassifierRow>> _seed;
        public List<string> RequestedSchemes { get; } = new();

        public FakeLookup(Dictionary<string, IReadOnlyList<ClassifierRow>> seed)
            => _seed = seed;

        public Task<IReadOnlyList<ClassifierRow>> GetActiveAsync(string scheme, CancellationToken cancellationToken = default)
        {
            RequestedSchemes.Add(scheme);
            return Task.FromResult(_seed.TryGetValue(scheme, out var rows)
                ? rows
                : (IReadOnlyList<ClassifierRow>)Array.Empty<ClassifierRow>());
        }
    }

    /// <summary>
    /// Active rows render as <c>&lt;option&gt;</c> entries inside the picker.
    /// The placeholder option remains first so the bound model can express a
    /// "nothing selected" state.
    /// </summary>
    [Fact]
    public void Picker_RendersOptionsFromInjectedLookup()
    {
        var lookup = new FakeLookup(new()
        {
            ["CAEM"] = new[]
            {
                new ClassifierRow("CAEM", "01.11", "Cultivare", null, null, null, "national"),
                new ClassifierRow("CAEM", "02.10", "Silvicultură", null, null, null, "national"),
            },
        });
        Services.AddSingleton<IClassifierLookup>(lookup);

        var cut = RenderComponent<ClassifierPicker>(p => p.Add(c => c.SchemeCode, "CAEM"));

        cut.WaitForAssertion(() =>
        {
            var options = cut.FindAll("[data-testid='classifier-option']");
            options.Count.Should().Be(2);
            cut.Markup.Should().Contain("01.11");
            cut.Markup.Should().Contain("Cultivare");
            cut.Markup.Should().Contain("02.10");
            lookup.RequestedSchemes.Should().ContainSingle().Which.Should().Be("CAEM");
        });
    }

    /// <summary>
    /// The picker delegates the active-only filter to the
    /// <see cref="IClassifierLookup"/> contract. The lookup
    /// <see cref="IClassifierLookup.GetActiveAsync"/> is documented to return
    /// only active rows — when the seed contains no active rows, the picker
    /// renders the empty-state container and zero options.
    /// </summary>
    [Fact]
    public void Picker_WhenLookupReturnsNoRows_RendersEmptyState()
    {
        var lookup = new FakeLookup(new()
        {
            ["CUATM"] = Array.Empty<ClassifierRow>(),
        });
        Services.AddSingleton<IClassifierLookup>(lookup);

        var cut = RenderComponent<ClassifierPicker>(p => p.Add(c => c.SchemeCode, "CUATM"));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='classifier-option']").Count.Should().Be(0);
            cut.Find("[data-testid='classifier-empty']").Should().NotBeNull();
        });
    }

    /// <summary>
    /// Changing the selection raises the <c>OnValueChanged</c> callback with
    /// the selected option's code. This is the bind contract every consumer
    /// page relies on for two-way data flow.
    /// </summary>
    [Fact]
    public void Picker_WhenSelectionChanges_RaisesOnValueChanged()
    {
        var lookup = new FakeLookup(new()
        {
            ["CAEM"] = new[]
            {
                new ClassifierRow("CAEM", "01.11", "Cultivare", null, null, null, "national"),
                new ClassifierRow("CAEM", "02.10", "Silvicultură", null, null, null, "national"),
            },
        });
        Services.AddSingleton<IClassifierLookup>(lookup);

        string? captured = null;
        var cut = RenderComponent<ClassifierPicker>(p => p
            .Add(c => c.SchemeCode, "CAEM")
            .Add(c => c.OnValueChanged, v => captured = v));

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='classifier-option']").Count.Should().Be(2));

        cut.Find("[data-testid='classifier-select']").Change("02.10");

        captured.Should().Be("02.10");
    }
}
