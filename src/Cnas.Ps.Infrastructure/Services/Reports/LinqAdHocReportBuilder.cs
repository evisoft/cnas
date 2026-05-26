using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reports;

/// <summary>
/// R0580 / TOR CF 09.02 — default <see cref="IAdHocReportBuilder"/>
/// implementation. Projects a chosen entity set through optional filters
/// and ordering, materialising up to
/// <see cref="IAdHocReportBuilder.MaxRowsPerRun"/> rows as a dynamic-shape
/// dictionary array.
/// </summary>
/// <remarks>
/// <para>
/// <b>Column allow-lists.</b> Every supported entity exposes a tightly
/// curated set of queryable / projectable columns. The allow-list prevents
/// reflection over arbitrary internal columns (which could leak hashes /
/// PII without the sensitivity middleware noticing) and lets the builder
/// keep its filter parser tiny.
/// </para>
/// </remarks>
public sealed class LinqAdHocReportBuilder : IAdHocReportBuilder
{
    private readonly IReadOnlyCnasDbContext _readDb;
    private readonly IValidator<AdHocReportSpecDto> _validator;

    /// <summary>
    /// Per-entity column metadata. Each entry maps a stable column name to a
    /// <see cref="ColumnAccessor"/> describing the runtime type and the
    /// row → value reader. Adding a new column is a one-line edit; the
    /// runtime never touches columns that are not declared here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, ColumnAccessor>> Schemas =
        new Dictionary<string, IReadOnlyDictionary<string, ColumnAccessor>>(StringComparer.Ordinal)
        {
            [AdHocReportEntitySets.Applications] = new Dictionary<string, ColumnAccessor>(StringComparer.Ordinal)
            {
                ["Id"] = ColumnAccessor.For<ServiceApplication>(a => a.Id),
                ["ReferenceNumber"] = ColumnAccessor.For<ServiceApplication>(a => a.ReferenceNumber),
                ["Status"] = ColumnAccessor.For<ServiceApplication>(a => a.Status.ToString()),
                ["SubmittedAtUtc"] = ColumnAccessor.For<ServiceApplication>(a => a.SubmittedAtUtc),
                ["ClosedAtUtc"] = ColumnAccessor.For<ServiceApplication>(a => a.ClosedAtUtc),
                ["SubdivisionCode"] = ColumnAccessor.For<ServiceApplication>(a => a.SubdivisionCode),
                ["CreatedAtUtc"] = ColumnAccessor.For<ServiceApplication>(a => a.CreatedAtUtc),
            },
            [AdHocReportEntitySets.Contributors] = new Dictionary<string, ColumnAccessor>(StringComparer.Ordinal)
            {
                ["Id"] = ColumnAccessor.For<Contributor>(c => c.Id),
                ["Idno"] = ColumnAccessor.For<Contributor>(c => c.Idno),
                ["Denumire"] = ColumnAccessor.For<Contributor>(c => c.Denumire),
                ["IsInsolvent"] = ColumnAccessor.For<Contributor>(c => c.IsInsolvent),
                ["CreatedAtUtc"] = ColumnAccessor.For<Contributor>(c => c.CreatedAtUtc),
            },
            [AdHocReportEntitySets.Dossiers] = new Dictionary<string, ColumnAccessor>(StringComparer.Ordinal)
            {
                ["Id"] = ColumnAccessor.For<Dossier>(d => d.Id),
                ["DossierNumber"] = ColumnAccessor.For<Dossier>(d => d.DossierNumber),
                ["ApplicationId"] = ColumnAccessor.For<Dossier>(d => d.ApplicationId),
                ["ClosedAtUtc"] = ColumnAccessor.For<Dossier>(d => d.ClosedAtUtc),
                ["CreatedAtUtc"] = ColumnAccessor.For<Dossier>(d => d.CreatedAtUtc),
            },
            [AdHocReportEntitySets.Decisions] = new Dictionary<string, ColumnAccessor>(StringComparer.Ordinal)
            {
                ["Id"] = ColumnAccessor.For<ServiceApplication>(a => a.Id),
                ["ReferenceNumber"] = ColumnAccessor.For<ServiceApplication>(a => a.ReferenceNumber),
                ["Status"] = ColumnAccessor.For<ServiceApplication>(a => a.Status.ToString()),
                ["ClosedAtUtc"] = ColumnAccessor.For<ServiceApplication>(a => a.ClosedAtUtc),
                ["SubmittedAtUtc"] = ColumnAccessor.For<ServiceApplication>(a => a.SubmittedAtUtc),
            },
        };

    /// <summary>Constructs the builder.</summary>
    /// <param name="readDb">Replica-routed read context.</param>
    /// <param name="validator">FluentValidation rules for the spec envelope.</param>
    public LinqAdHocReportBuilder(IReadOnlyCnasDbContext readDb, IValidator<AdHocReportSpecDto> validator)
    {
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(validator);
        _readDb = readDb;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<AdHocReportResultDto>> BuildAsync(
        AdHocReportSpecDto spec,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var validation = await _validator.ValidateAsync(spec, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<AdHocReportResultDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        if (!Schemas.TryGetValue(spec.EntitySet, out var schema))
        {
            return Result<AdHocReportResultDto>.Failure(
                ErrorCodes.AdHocReportUnknownEntity,
                $"Unknown entity set '{spec.EntitySet}'.");
        }

        // Column allow-list check — every requested output column must be declared on
        // the entity's schema. Catches typos at the boundary.
        foreach (var col in spec.Columns)
        {
            if (!schema.ContainsKey(col))
            {
                return Result<AdHocReportResultDto>.Failure(
                    ErrorCodes.AdHocReportUnknownColumn,
                    $"Unknown column '{col}' on entity set '{spec.EntitySet}'.");
            }
        }

        // OrderBy column must also live in the schema if supplied.
        if (!string.IsNullOrWhiteSpace(spec.OrderBy) && !schema.ContainsKey(spec.OrderBy))
        {
            return Result<AdHocReportResultDto>.Failure(
                ErrorCodes.AdHocReportUnknownColumn,
                $"Unknown OrderBy column '{spec.OrderBy}' on entity set '{spec.EntitySet}'.");
        }

        // Filter columns also must be in the schema.
        foreach (var f in spec.Filters)
        {
            if (!schema.ContainsKey(f.Field))
            {
                return Result<AdHocReportResultDto>.Failure(
                    ErrorCodes.AdHocReportUnknownColumn,
                    $"Unknown filter column '{f.Field}' on entity set '{spec.EntitySet}'.");
            }
        }

        // Materialise the rows. Each entity-set branch builds an IEnumerable<entity>
        // pre-filtered by the in-memory predicates derived from the spec, ordered as
        // requested, and capped at MaxRowsPerRun + 1 so we can detect a runaway run.
        List<IReadOnlyDictionary<string, object?>> rows;
        switch (spec.EntitySet)
        {
            case AdHocReportEntitySets.Applications:
                rows = await ProjectAsync<ServiceApplication>(
                    _readDb.Applications.Where(a => a.IsActive), schema, spec, ct).ConfigureAwait(false);
                break;
            case AdHocReportEntitySets.Contributors:
                rows = await ProjectAsync<Contributor>(
                    _readDb.Contributors.Where(c => c.IsActive), schema, spec, ct).ConfigureAwait(false);
                break;
            case AdHocReportEntitySets.Dossiers:
                rows = await ProjectAsync<Dossier>(
                    _readDb.Dossiers.Where(d => d.IsActive), schema, spec, ct).ConfigureAwait(false);
                break;
            case AdHocReportEntitySets.Decisions:
                rows = await ProjectAsync<ServiceApplication>(
                    _readDb.Applications.Where(a => a.IsActive &&
                        (a.Status == ApplicationStatus.Approved || a.Status == ApplicationStatus.Rejected)),
                    schema, spec, ct).ConfigureAwait(false);
                break;
            default:
                return Result<AdHocReportResultDto>.Failure(
                    ErrorCodes.AdHocReportUnknownEntity,
                    $"Unknown entity set '{spec.EntitySet}'.");
        }

        if (rows.Count > IAdHocReportBuilder.MaxRowsPerRun)
        {
            return Result<AdHocReportResultDto>.Failure(
                ErrorCodes.AdHocReportTooLarge,
                $"Result set {rows.Count} exceeds the per-run cap of {IAdHocReportBuilder.MaxRowsPerRun}; narrow the filter.");
        }

        return Result<AdHocReportResultDto>.Success(
            new AdHocReportResultDto(spec.Columns, rows, rows.Count));
    }

    /// <summary>
    /// Materialises the supplied <paramref name="source"/> through the
    /// spec's filters / ordering / column projection. The query is fully
    /// pulled to memory because the filter operators are typed and only
    /// safe to evaluate in-memory.
    /// </summary>
    /// <typeparam name="TEntity">Row type backing the entity set.</typeparam>
    /// <param name="source">Pre-narrowed IQueryable&lt;TEntity&gt; (e.g. with IsActive filter).</param>
    /// <param name="schema">Column allow-list for the entity.</param>
    /// <param name="spec">The validated spec.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The materialised dictionary rows.</returns>
    private static async Task<List<IReadOnlyDictionary<string, object?>>> ProjectAsync<TEntity>(
        IQueryable<TEntity> source,
        IReadOnlyDictionary<string, ColumnAccessor> schema,
        AdHocReportSpecDto spec,
        CancellationToken ct)
        where TEntity : class
    {
        // Materialise up to MaxRowsPerRun + 1 rows so the caller can detect "too large".
        // The full filter pass happens in-memory because the schema is reflection-based.
        var materialised = await source
            .Take(IAdHocReportBuilder.MaxRowsPerRun + 1)
            .ToListAsync(ct).ConfigureAwait(false);

        IEnumerable<TEntity> filtered = materialised;
        foreach (var f in spec.Filters)
        {
            var accessor = schema[f.Field];
            filtered = filtered.Where(entity =>
                MatchesFilter(accessor.GetValue(entity!), f.Operator, f.Value));
        }

        if (!string.IsNullOrWhiteSpace(spec.OrderBy))
        {
            var orderAccessor = schema[spec.OrderBy];
            filtered = spec.Descending
                ? filtered.OrderByDescending(e => orderAccessor.GetValue(e!) as IComparable)
                : filtered.OrderBy(e => orderAccessor.GetValue(e!) as IComparable);
        }

        var result = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var entity in filtered)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var col in spec.Columns)
            {
                row[col] = schema[col].GetValue(entity!);
            }
            result.Add(row);
        }
        return result;
    }

    /// <summary>
    /// In-memory match between a row cell value and the (operator, value)
    /// pair from the spec. Supports string / numeric / date / boolean
    /// values with the operators defined in <see cref="AdHocReportOperators"/>.
    /// </summary>
    /// <param name="cellValue">The cell value extracted from the row.</param>
    /// <param name="op">The filter operator (one of <see cref="AdHocReportOperators"/>).</param>
    /// <param name="literal">The right-hand-side comparison value as a string.</param>
    /// <returns><c>true</c> when the cell matches the filter; otherwise <c>false</c>.</returns>
    private static bool MatchesFilter(object? cellValue, string op, string literal)
    {
        // CONTAINS only makes sense on strings; fall back to string ToString() for
        // other types so an integer / date cell still answers the substring probe.
        if (string.Equals(op, AdHocReportOperators.Contains, StringComparison.Ordinal))
        {
            var asString = cellValue?.ToString() ?? string.Empty;
            return asString.Contains(literal, StringComparison.OrdinalIgnoreCase);
        }

        var cmp = CompareValues(cellValue, literal);
        return op switch
        {
            AdHocReportOperators.Eq => cmp == 0,
            AdHocReportOperators.Ne => cmp != 0,
            AdHocReportOperators.Gte => cmp >= 0,
            AdHocReportOperators.Lte => cmp <= 0,
            _ => false,
        };
    }

    /// <summary>
    /// Compares <paramref name="cellValue"/> against the supplied literal.
    /// Returns the standard <c>IComparable.CompareTo</c>-shape integer
    /// (negative / 0 / positive). When the two operands cannot be coerced
    /// to a common type the comparison falls back to a case-insensitive
    /// string compare.
    /// </summary>
    /// <param name="cellValue">The cell value extracted from the row.</param>
    /// <param name="literal">The right-hand-side comparison value as a string.</param>
    /// <returns>Comparison sign (-1 / 0 / 1).</returns>
    private static int CompareValues(object? cellValue, string literal)
    {
        if (cellValue is null)
        {
            return string.IsNullOrEmpty(literal) ? 0 : -1;
        }
        var type = cellValue.GetType();

        if (type == typeof(string))
        {
            return string.Compare((string)cellValue, literal, StringComparison.OrdinalIgnoreCase);
        }
        if (type == typeof(int) || type == typeof(long))
        {
            if (long.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return Convert.ToInt64(cellValue, CultureInfo.InvariantCulture).CompareTo(parsed);
            }
        }
        if (type == typeof(decimal))
        {
            if (decimal.TryParse(literal, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                return ((decimal)cellValue).CompareTo(parsed);
            }
        }
        if (type == typeof(bool))
        {
            if (bool.TryParse(literal, out var parsed))
            {
                return ((bool)cellValue).CompareTo(parsed);
            }
        }
        if (type == typeof(DateTime))
        {
            if (DateTime.TryParse(literal, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return ((DateTime)cellValue).CompareTo(parsed);
            }
        }
        if (type == typeof(DateOnly))
        {
            if (DateOnly.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return ((DateOnly)cellValue).CompareTo(parsed);
            }
        }
        // Fallback: stringified comparison so unknown types still answer (case-insensitive).
        return string.Compare(
            cellValue.ToString(), literal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lightweight per-column accessor that captures the row-to-value
    /// reader without resorting to runtime reflection on every cell lookup.
    /// </summary>
    private sealed class ColumnAccessor
    {
        /// <summary>The row-to-value reader. Boxes value types.</summary>
        private readonly Func<object, object?> _getter;

        private ColumnAccessor(Func<object, object?> getter)
        {
            _getter = getter;
        }

        /// <summary>Reads the cell value from the supplied row.</summary>
        /// <param name="row">The entity row.</param>
        /// <returns>The boxed cell value (or <c>null</c>).</returns>
        public object? GetValue(object row) => _getter(row);

        /// <summary>Builds a column accessor that reads a value from <typeparamref name="TEntity"/>.</summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="reader">The strongly-typed row-to-value reader.</param>
        /// <returns>The wrapped accessor.</returns>
        public static ColumnAccessor For<TEntity>(Func<TEntity, object?> reader)
            where TEntity : class
        {
            return new ColumnAccessor(row => reader((TEntity)row));
        }
    }
}
