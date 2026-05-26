using Cnas.Ps.Application.QueryBudget;

namespace Cnas.Ps.Application.Tests.QueryBudget;

/// <summary>
/// R0167 / CF 01.06 — unit tests for the fluent <see cref="QueryBudgetPolicyBuilder"/>.
/// These tests exercise the builder's pure-data contract (no DI, no DB) so they live
/// in the Application test assembly.
/// </summary>
public class QueryBudgetPolicyBuilderTests
{
    [Fact]
    public void For_BuildsPolicyWithExpectedRegistryAndDefaultBudget()
    {
        var policy = QueryBudgetPolicyBuilder.For("Solicitant").Build();

        policy.Registry.Should().Be("Solicitant");
        policy.Budget.Should().Be(QueryBudgetPolicy.DefaultBudget);
        policy.Rules.Should().BeEmpty();
    }

    [Fact]
    public void WithBudget_SetsExpectedBudget()
    {
        var policy = QueryBudgetPolicyBuilder
            .For("Solicitant")
            .WithBudget(2500)
            .Build();

        policy.Budget.Should().Be(2500);
    }

    [Fact]
    public void Build_PreservesRequireAndSuggestInDeclarationOrder()
    {
        var policy = QueryBudgetPolicyBuilder
            .For("Solicitant")
            .WithBudget(5000)
            .Require("Q", RefinementHintReasons.AddFreeTextFilter)
            .Suggest("CreatedFromUtc", RefinementHintReasons.AddDateFilter)
            .Build();

        policy.Rules.Should().HaveCount(2);
        policy.Rules[0].FieldName.Should().Be("Q");
        policy.Rules[0].Severity.Should().Be(RefinementHintSeverity.Required);
        policy.Rules[0].Reason.Should().Be(RefinementHintReasons.AddFreeTextFilter);
        policy.Rules[1].FieldName.Should().Be("CreatedFromUtc");
        policy.Rules[1].Severity.Should().Be(RefinementHintSeverity.Suggested);
        policy.Rules[1].Reason.Should().Be(RefinementHintReasons.AddDateFilter);
    }

    [Fact]
    public void Require_DefaultPredicate_FiresWhenFieldMissing()
    {
        var policy = QueryBudgetPolicyBuilder
            .For("Solicitant")
            .Require("Q", RefinementHintReasons.AddFreeTextFilter)
            .Build();

        var empty = new QueryFilterContext();
        policy.Rules[0].AppliesWhen(empty).Should().BeTrue();

        var withQ = new QueryFilterContext().With("Q", "Popescu");
        policy.Rules[0].AppliesWhen(withQ).Should().BeFalse();
    }

    [Fact]
    public void Suggest_DefaultPredicate_FiresWhenFieldMissing()
    {
        var policy = QueryBudgetPolicyBuilder
            .For("Cerere")
            .Suggest("CreatedFromUtc", RefinementHintReasons.AddDateFilter)
            .Build();

        var empty = new QueryFilterContext();
        policy.Rules[0].AppliesWhen(empty).Should().BeTrue();

        var withDate = new QueryFilterContext().With("CreatedFromUtc", DateTime.UtcNow);
        policy.Rules[0].AppliesWhen(withDate).Should().BeFalse();
    }

    [Fact]
    public void RequireWhen_AppliesCustomPredicate()
    {
        var policy = QueryBudgetPolicyBuilder
            .For("AuditLog")
            .RequireWhen(
                "EventCode",
                RefinementHintReasons.AddIdentifierFilter,
                ctx => !ctx.Has("EventCode") && !ctx.Has("ActorUserId"))
            .Build();

        // Neither field set → fires.
        var none = new QueryFilterContext();
        policy.Rules[0].AppliesWhen(none).Should().BeTrue();

        // ActorUserId set → does NOT fire (the predicate is the disjunction's complement).
        var withActor = new QueryFilterContext().With("ActorUserId", "SQID-x");
        policy.Rules[0].AppliesWhen(withActor).Should().BeFalse();
    }

    [Fact]
    public void WithBudget_NonPositive_Throws()
    {
        var builder = QueryBudgetPolicyBuilder.For("Solicitant");

        Action act = () => builder.WithBudget(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Require_NullFieldName_Throws()
    {
        var builder = QueryBudgetPolicyBuilder.For("Solicitant");

        Action act = () => builder.Require(string.Empty);

        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// R0167 — unit tests for the lightweight filter envelope. Mirrors the Has() /
/// "non-default" contract documented on <see cref="QueryFilterContext"/>.
/// </summary>
public class QueryFilterContextTests
{
    [Fact]
    public void Empty_HasReturnsFalseForAnyField()
    {
        var ctx = new QueryFilterContext();

        ctx.Has("Q").Should().BeFalse();
        ctx.Has("CreatedFromUtc").Should().BeFalse();
        ctx.ProvidedFilters.Should().BeEmpty();
    }

    [Fact]
    public void With_NonNullValue_AddsField()
    {
        var ctx = new QueryFilterContext().With("Q", "Popescu");

        ctx.Has("Q").Should().BeTrue();
        ctx.ProvidedFilters.Should().ContainKey("Q");
        ctx.ProvidedFilters["Q"].Should().Be("Popescu");
    }

    [Fact]
    public void With_EmptyOrWhitespaceString_IsTreatedAsAbsent()
    {
        var ctx = new QueryFilterContext()
            .With("Q", string.Empty)
            .With("Status", "   ");

        ctx.Has("Q").Should().BeFalse();
        ctx.Has("Status").Should().BeFalse();
        ctx.ProvidedFilters.Should().BeEmpty();
    }

    [Fact]
    public void With_IsImmutable_DoesNotMutateOriginal()
    {
        var original = new QueryFilterContext();
        var derived = original.With("Q", "Popescu");

        original.Has("Q").Should().BeFalse();
        derived.Has("Q").Should().BeTrue();
    }
}
