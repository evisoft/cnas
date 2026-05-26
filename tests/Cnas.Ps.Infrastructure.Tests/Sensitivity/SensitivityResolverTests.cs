using System.Collections.Generic;
using Cnas.Ps.Application.Sensitivity;
using Cnas.Ps.Contracts.Security;
using Cnas.Ps.Infrastructure.Security;

namespace Cnas.Ps.Infrastructure.Tests.Sensitivity;

/// <summary>
/// R0228 / TOR SEC 033 — exercises the <see cref="SensitivityResolver"/> implementation
/// against a representative mix of annotated and unannotated DTOs.
/// </summary>
/// <remarks>
/// The resolver is reflection-based and caches its per-type lookups. The tests cover both
/// the public contract (every <see cref="ISensitivityResolver"/> method) and the caching
/// invariant (same dictionary instance returned on repeat lookups).
/// </remarks>
public sealed class SensitivityResolverTests
{
    /// <summary>Unannotated POCO — resolver must fall back to <see cref="SensitivityLabel.Internal"/>.</summary>
    private sealed class UnannotatedDto
    {
        /// <summary>Property without a sensitivity attribute.</summary>
        public string? Name { get; set; }

        /// <summary>Property without a sensitivity attribute.</summary>
        public int Age { get; set; }
    }

    /// <summary>DTO with a property-level annotation — resolver must surface it.</summary>
    private sealed class PartiallyAnnotatedDto
    {
        /// <summary>Confidential per the attribute.</summary>
        [SensitivityClassification(SensitivityLabel.Confidential)]
        public string? FullName { get; set; }

        /// <summary>Unannotated.</summary>
        public int Age { get; set; }
    }

    /// <summary>DTO whose highest property label is <see cref="SensitivityLabel.Restricted"/>.</summary>
    private sealed class MixedDto
    {
        /// <summary>Public id.</summary>
        [SensitivityClassification(SensitivityLabel.Public)]
        public string? Id { get; set; }

        /// <summary>Confidential name.</summary>
        [SensitivityClassification(SensitivityLabel.Confidential)]
        public string? Name { get; set; }

        /// <summary>Restricted national id.</summary>
        [SensitivityClassification(SensitivityLabel.Restricted)]
        public string? Idnp { get; set; }
    }

    /// <summary>Type-level floor — every property reads at least Confidential.</summary>
    [SensitivityClassification(SensitivityLabel.Confidential)]
    private class TypeFloorBaseDto
    {
        /// <summary>Unannotated property — the type floor wins.</summary>
        public string? Note { get; set; }
    }

    /// <summary>Derived class — should inherit the base's class-level annotation.</summary>
    private sealed class DerivedTypeFloorDto : TypeFloorBaseDto
    {
        /// <summary>Unannotated property — the inherited floor wins.</summary>
        public string? OtherNote { get; set; }
    }

    [Fact]
    public void Resolve_TypeWithoutAnnotations_ReturnsInternal()
    {
        var sut = new SensitivityResolver();

        var label = sut.Resolve(typeof(UnannotatedDto));

        label.Should().Be(SensitivityLabel.Internal);
    }

    [Fact]
    public void Resolve_KnownAnnotatedProperty_ReturnsItsLabel()
    {
        var sut = new SensitivityResolver();

        var label = sut.Resolve(typeof(PartiallyAnnotatedDto), nameof(PartiallyAnnotatedDto.FullName));

        label.Should().Be(SensitivityLabel.Confidential);
    }

    [Fact]
    public void Resolve_Type_ReturnsHighestPropertyLabelAcrossAllProperties()
    {
        var sut = new SensitivityResolver();

        var label = sut.Resolve(typeof(MixedDto));

        label.Should().Be(SensitivityLabel.Restricted);
    }

    [Fact]
    public void ResolveAll_ReturnsPerPropertyDictionary()
    {
        var sut = new SensitivityResolver();

        IReadOnlyDictionary<string, SensitivityLabel> map = sut.ResolveAll(typeof(MixedDto));

        map.Should().ContainKey(nameof(MixedDto.Id)).WhoseValue.Should().Be(SensitivityLabel.Public);
        map.Should().ContainKey(nameof(MixedDto.Name)).WhoseValue.Should().Be(SensitivityLabel.Confidential);
        map.Should().ContainKey(nameof(MixedDto.Idnp)).WhoseValue.Should().Be(SensitivityLabel.Restricted);
    }

    [Fact]
    public void ResolveAll_ReturnsSameInstance_OnRepeatLookup()
    {
        var sut = new SensitivityResolver();

        var first = sut.ResolveAll(typeof(MixedDto));
        var second = sut.ResolveAll(typeof(MixedDto));

        // Identity (reference) check — the cache must return the SAME dictionary instance.
        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void Resolve_PropertyWithoutAttribute_TypeFloorWins()
    {
        var sut = new SensitivityResolver();

        // The property has no explicit attribute but the type carries Confidential — the
        // floor must win even though the property defaults to Internal.
        var label = sut.Resolve(typeof(TypeFloorBaseDto), nameof(TypeFloorBaseDto.Note));

        label.Should().Be(SensitivityLabel.Confidential);
    }

    [Fact]
    public void Resolve_DerivedClass_InheritsBaseClassLevelAnnotation()
    {
        var sut = new SensitivityResolver();

        var label = sut.Resolve(typeof(DerivedTypeFloorDto), nameof(DerivedTypeFloorDto.OtherNote));

        label.Should().Be(SensitivityLabel.Confidential);
    }

    [Fact]
    public void Resolve_UnknownProperty_FallsBackToTypeLabelThenInternal()
    {
        var sut = new SensitivityResolver();

        var unknownOnUnannotated = sut.Resolve(typeof(UnannotatedDto), "Nope");
        var unknownOnFloor = sut.Resolve(typeof(TypeFloorBaseDto), "Nope");

        unknownOnUnannotated.Should().Be(SensitivityLabel.Internal);
        unknownOnFloor.Should().Be(SensitivityLabel.Confidential);
    }
}
