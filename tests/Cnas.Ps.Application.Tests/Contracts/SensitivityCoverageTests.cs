using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Application.Tests.Contracts;

/// <summary>
/// R0228 / TOR SEC 033 — reflection-based regression gate that walks every DTO in
/// <c>Cnas.Ps.Contracts.dll</c> and asserts that every high-risk property name carries
/// at least the minimum-acceptable <see cref="SensitivityClassification"/> label.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a reflection sweep?</b> The per-DTO unit tests in
/// <see cref="Cnas.Ps.Application.Tests.Sensitivity.DtoSensitivityAnnotationTests"/> pin
/// specific properties on specific DTOs — those are explicit lockfiles. This file is the
/// "future-drift" guard: any newly added DTO with a property called <c>Email</c>,
/// <c>Idnp</c>, <c>Password</c>, etc. is automatically required to carry the right label,
/// so a developer adding a new contract type cannot accidentally ship un-annotated PII.
/// </para>
/// <para>
/// <b>Resolution semantics.</b> The effective label is the maximum of (property-level
/// attribute, class-level attribute floor). When neither exists the property is treated
/// as the default <see cref="SensitivityLabel.Internal"/> — which is intentionally NOT
/// good enough for the high-risk names policed here.
/// </para>
/// </remarks>
public sealed class SensitivityCoverageTests
{
    /// <summary>
    /// Names that MUST resolve to at least <see cref="SensitivityLabel.Confidential"/>
    /// when they appear on any DTO. Match is exact on the property name (case-sensitive).
    /// </summary>
    private static readonly HashSet<string> ConfidentialAtLeast = new(StringComparer.Ordinal)
    {
        "Email",
        "MonthlySalary",
    };

    /// <summary>
    /// Prefixes that — when the property name starts with one — must resolve to at least
    /// <see cref="SensitivityLabel.Confidential"/>. Covers <c>PhoneE164</c>,
    /// <c>PhoneNumber</c>, plain <c>Phone</c>, etc.
    /// </summary>
    private static readonly string[] ConfidentialPrefixes =
    {
        "Phone",
    };

    /// <summary>
    /// National-identifier-style names that must resolve to at least
    /// <see cref="SensitivityLabel.Confidential"/> — Restricted is preferred but
    /// Confidential is acceptable per the batch policy ("Idnp / Idno / Iban carry
    /// Restricted OR Confidential").
    /// </summary>
    private static readonly HashSet<string> ConfidentialOrRestrictedNames = new(StringComparer.Ordinal)
    {
        "Idno",
        "Idnp",
        "Iban",
    };

    /// <summary>
    /// Names that MUST resolve to <see cref="SensitivityLabel.Restricted"/>. Currently
    /// only password-related fields — these are the strongest-secret payloads in the
    /// system and never have a defensible reason to ship at a lower label.
    /// </summary>
    private static readonly HashSet<string> RestrictedExact = new(StringComparer.Ordinal)
    {
        "Password",
        "Plaintext",
    };

    /// <summary>
    /// Property names exempted from the sweep — typically internal hash/projection shapes
    /// that intentionally ship at <c>Internal</c> sensitivity because the wire value is
    /// already a non-reversible derivative (e.g. <c>IdnoHashPrefix</c>,
    /// <c>IdnpHashPrefix</c>, <c>TaxpayerHashPrefix</c>).
    /// </summary>
    private static readonly HashSet<string> ExemptNameSuffixes = new(StringComparer.Ordinal)
    {
        "HashPrefix",
        "Hash",
    };

    /// <summary>
    /// Walks every public record / class in <c>Cnas.Ps.Contracts.dll</c> and asserts that
    /// the high-risk property names carry the expected minimum sensitivity label.
    /// </summary>
    [Fact]
    public void EveryHighRiskPropertyAcrossContracts_CarriesMinimumExpectedLabel()
    {
        // Arrange — load every type in the Contracts assembly via a known DTO.
        var contractsAssembly = typeof(InsuredPersonOutput).Assembly;
        var dtoTypes = contractsAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Where(t => t.Namespace?.StartsWith("Cnas.Ps.Contracts", StringComparison.Ordinal) == true)
            .ToArray();

        // Accumulate every drift offence so the first run pinpoints ALL gaps at once.
        var failures = new List<string>();

        foreach (var dto in dtoTypes)
        {
            foreach (var prop in dto.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (IsExempt(prop.Name))
                {
                    continue;
                }

                var required = RequiredLabelFor(prop.Name);
                if (required is null)
                {
                    continue;
                }

                var effective = ResolveEffectiveLabel(dto, prop);
                if (effective < required.Value)
                {
                    failures.Add(
                        $"{dto.FullName}.{prop.Name}: expected ≥ {required.Value} but got {effective}.");
                }
            }
        }

        // Assert — empty failure list = no drift. Non-empty surfaces every gap at once
        // so a developer fixing drift sees the complete list, not just the first offender.
        if (failures.Count > 0)
        {
            var rendered = string.Join(Environment.NewLine, failures);
            Assert.Fail(
                "Every high-risk property name on every Contracts DTO must carry the "
                + "expected minimum sensitivity label per R0228 / SEC 033. Drift detected:"
                + Environment.NewLine + rendered);
        }
    }

    /// <summary>
    /// Returns the minimum acceptable label for the supplied property name, or <c>null</c>
    /// when the name is not policed by this sweep.
    /// </summary>
    /// <param name="propertyName">The property name as it appears on the DTO.</param>
    /// <returns>The minimum required <see cref="SensitivityLabel"/>, or <c>null</c>.</returns>
    private static SensitivityLabel? RequiredLabelFor(string propertyName)
    {
        if (RestrictedExact.Contains(propertyName))
        {
            return SensitivityLabel.Restricted;
        }

        if (ConfidentialAtLeast.Contains(propertyName))
        {
            return SensitivityLabel.Confidential;
        }

        if (ConfidentialOrRestrictedNames.Contains(propertyName))
        {
            return SensitivityLabel.Confidential;
        }

        foreach (var prefix in ConfidentialPrefixes)
        {
            if (propertyName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return SensitivityLabel.Confidential;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied property name ends with one of the
    /// <see cref="ExemptNameSuffixes"/> (e.g. hashed projections).
    /// </summary>
    /// <param name="propertyName">The property name to check.</param>
    /// <returns><c>true</c> when the property is exempt; <c>false</c> otherwise.</returns>
    private static bool IsExempt(string propertyName)
    {
        foreach (var suffix in ExemptNameSuffixes)
        {
            if (propertyName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Mirrors the <c>SensitivityResolver</c> semantics: property attribute wins; falls
    /// back to the class-level attribute floor; otherwise defaults to
    /// <see cref="SensitivityLabel.Internal"/>. The class-level floor, when present,
    /// promotes the property label upwards (but never demotes).
    /// </summary>
    /// <param name="dto">DTO under inspection.</param>
    /// <param name="prop">Property under inspection.</param>
    /// <returns>The effective <see cref="SensitivityLabel"/>.</returns>
    private static SensitivityLabel ResolveEffectiveLabel(Type dto, PropertyInfo prop)
    {
        var typeAttr = dto.GetCustomAttribute<SensitivityClassificationAttribute>(inherit: true);
        var propAttr = prop.GetCustomAttribute<SensitivityClassificationAttribute>(inherit: true);

        var label = propAttr?.Label ?? typeAttr?.Label ?? SensitivityLabel.Internal;
        if (typeAttr is not null && typeAttr.Label > label)
        {
            label = typeAttr.Label;
        }

        return label;
    }
}
