using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.ValueObjects;

namespace Cnas.Ps.Application.Tests.Decisions;

/// <summary>
/// Behaviour tests for <see cref="DecisionFacts"/> — the strongly-typed fact bag consumed
/// by the generic decision engine. The engine itself never throws on missing/wrong-type
/// facts; failures are returned as <c>Result</c> values per CLAUDE.md §2.1.
/// </summary>
public class DecisionFactsTests
{
    [Fact]
    public void Constructor_NullDictionary_Throws()
    {
        Action act = () => _ = new DecisionFacts(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Require_MissingKey_ReturnsMissingFact()
    {
        var facts = new DecisionFacts(new Dictionary<string, object?>());

        var result = facts.Require<int>("birthOrder");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MissingFact);
        result.ErrorMessage.Should().Contain("birthOrder");
    }

    [Fact]
    public void Require_WrongType_ReturnsBadRule()
    {
        // The caller asked for an int but the bag holds a string.
        var facts = new DecisionFacts(new Dictionary<string, object?>
        {
            ["birthOrder"] = "1",
        });

        var result = facts.Require<int>("birthOrder");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.BadRule);
    }

    [Fact]
    public void Require_PresentKey_ReturnsValue()
    {
        var facts = new DecisionFacts(new Dictionary<string, object?>
        {
            ["birthOrder"] = 2,
        });

        var result = facts.Require<int>("birthOrder");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);
    }

    [Fact]
    public void Require_NullValueForReferenceType_ReturnsMissingFact()
    {
        // A null value is treated as "fact not supplied" — facts must be non-null
        // for the engine to make a decision.
        var facts = new DecisionFacts(new Dictionary<string, object?>
        {
            ["parentIdnp"] = null,
        });

        var result = facts.Require<string>("parentIdnp");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MissingFact);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var facts = new DecisionFacts(new Dictionary<string, object?>());

        bool present = facts.TryGet<bool>("isInsured", out var value);

        present.Should().BeFalse();
        value.Should().Be(default);
    }

    [Fact]
    public void TryGet_PresentKey_ReturnsValueAndTrue()
    {
        var facts = new DecisionFacts(new Dictionary<string, object?>
        {
            ["isInsured"] = true,
        });

        bool present = facts.TryGet<bool>("isInsured", out var value);

        present.Should().BeTrue();
        value.Should().BeTrue();
    }

    [Fact]
    public void TryGet_WrongType_ReturnsFalse()
    {
        var facts = new DecisionFacts(new Dictionary<string, object?>
        {
            ["isInsured"] = "yes",
        });

        bool present = facts.TryGet<bool>("isInsured", out _);

        present.Should().BeFalse();
    }

    [Fact]
    public void Require_MoneyValue_RoundTripsCorrectly()
    {
        // Money is one of the supported domain value-objects; the engine
        // must accept it verbatim from the fact bag.
        var amount = Money.Mdl(11000m);
        var facts = new DecisionFacts(new Dictionary<string, object?>
        {
            ["salary"] = amount,
        });

        var result = facts.Require<Money>("salary");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(amount);
    }
}
