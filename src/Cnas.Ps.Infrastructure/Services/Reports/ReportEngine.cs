using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reports;

/// <summary>
/// R0156 / TOR CF 09.02 / FLEX 003 — default <see cref="IReportEngine"/>
/// implementation. Loads a <see cref="ReportTemplate"/>, splices the persisted
/// QBE filter, applies multi-column ordering, optionally aggregates by a single
/// group-by field, gates the materialisation through the R0167 budget guard, and
/// projects the rows into a <see cref="IReadOnlyDictionary{TKey, TValue}"/>
/// shape that survives the wire round-trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Entity dispatch.</b> The engine ships with a hard-wired registry → entity
/// type map. Today only the <see cref="Solicitant"/> registry has a wired QBE
/// schema; additional registries land alongside their schemas. Adding a new
/// entity is a one-line edit to <see cref="EntityDispatch"/>.
/// </para>
/// <para>
/// <b>Group-by today.</b> One row per distinct value of the group-by field,
/// with a synthetic <c>"count"</c> aggregate column carrying the row count for
/// the group. Sum / avg / min / max are deferred to a future batch — see
/// TODO.md.
/// </para>
/// </remarks>
public sealed class ReportEngine(
    ICnasDbContext db,
    ICallerContext caller,
    ICnasTimeProvider clock,
    IQbeRegistrySchemaProvider schemas,
    IQbeToLinqConverter qbeConverter,
    IQueryBudgetService budget,
    IGridExporter exporter,
    IAuditService audit)
    : IReportEngine
{
    private readonly ICnasDbContext _db = db;
    private readonly ICallerContext _caller = caller;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IQbeRegistrySchemaProvider _schemas = schemas;
    private readonly IQbeToLinqConverter _qbeConverter = qbeConverter;
    private readonly IQueryBudgetService _budget = budget;
    private readonly IGridExporter _exporter = exporter;
    private readonly IAuditService _audit = audit;

    /// <summary>JSON options for round-tripping the persisted payloads.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>Stable outcome strings — written verbatim to <see cref="ReportRun.OutcomeCode"/>.</summary>
    private static class Outcomes
    {
        public const string Success = "Success";
        public const string BudgetExceeded = "BudgetExceeded";
        public const string ValidationFailed = "ValidationFailed";
        public const string ExportFailed = "ExportFailed";
    }

    /// <summary>
    /// Registry → (entity CLR type, DbSet selector) dispatch table. The selector
    /// returns an <see cref="IQueryable"/> over the live data backing the registry.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<ICnasDbContext, IQueryable>> EntityDispatch =
        new Dictionary<string, Func<ICnasDbContext, IQueryable>>(StringComparer.Ordinal)
        {
            [QueryBudgetRegistries.Solicitant] = ctx => ctx.Solicitants.Where(s => s.IsActive),
        };

    /// <summary>
    /// Registry → entity CLR type lookup, mirrored against
    /// <see cref="EntityDispatch"/>. Built lazily because the underlying call sites
    /// only need it when the registry is recognised.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> EntityTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [QueryBudgetRegistries.Solicitant] = typeof(Solicitant),
        };

    /// <inheritdoc />
    public async Task<Result<ReportExecutionResultDto>> RunAsync(
        long templateId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var row = await _db.ReportTemplates
            .SingleOrDefaultAsync(t => t.Id == templateId && t.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<ReportExecutionResultDto>.Failure(ErrorCodes.NotFound, "Report template not found.");
        }
        if (!CanAccess(row))
        {
            return Result<ReportExecutionResultDto>.Failure(
                ErrorCodes.Forbidden,
                "The caller cannot execute this report template.");
        }

        var executeResult = await ExecuteAsync(row, skip, take, ct).ConfigureAwait(false);
        stopwatch.Stop();

        await PersistRunAsync(
            templateId: row.Id,
            outcome: ResolveOutcome(executeResult),
            rowCount: executeResult.IsSuccess ? executeResult.Value.Rows.Count : 0,
            durationMs: (int)stopwatch.ElapsedMilliseconds,
            failureReason: executeResult.IsFailure ? executeResult.ErrorMessage : null,
            ct).ConfigureAwait(false);

        await EmitAuditAsync(row, executeResult.IsSuccess ? executeResult.Value.Rows.Count : 0,
            (int)stopwatch.ElapsedMilliseconds,
            outcome: ResolveOutcome(executeResult),
            ct).ConfigureAwait(false);

        return executeResult;
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> ExportAsync(
        long templateId,
        ExportFormat format,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var row = await _db.ReportTemplates
            .SingleOrDefaultAsync(t => t.Id == templateId && t.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.NotFound, "Report template not found.");
        }
        if (!CanAccess(row))
        {
            return Result<byte[]>.Failure(
                ErrorCodes.Forbidden,
                "The caller cannot export this report template.");
        }

        // Materialise without paging — export pulls the entire matched set (subject to
        // the GridExporter's own row cap).
        var executeResult = await ExecuteAsync(row, skip: 0, take: int.MaxValue, ct).ConfigureAwait(false);
        if (executeResult.IsFailure)
        {
            stopwatch.Stop();
            await PersistRunAsync(row.Id, ResolveOutcome(executeResult), 0,
                (int)stopwatch.ElapsedMilliseconds, executeResult.ErrorMessage, ct).ConfigureAwait(false);
            return Result<byte[]>.Failure(executeResult.ErrorCode!, executeResult.ErrorMessage!);
        }

        var page = executeResult.Value;

        // Map the dictionary rows onto the GridExporter grammar — every projected
        // column becomes a Text-typed GridColumn unless we can detect a richer
        // runtime type on the first row.
        var columns = page.Columns
            .Select(c => new GridColumn(c, c, InferDataType(page.Rows, c)))
            .ToList();
        var gridRows = page.Rows
            .Select(r => new GridRow(r.Cells))
            .ToList();

        var request = new GridExportRequest(
            GridName: $"ReportTemplate:{row.Code}",
            Columns: columns,
            Rows: gridRows,
            Title: row.Name);

        var rendered = await _exporter.ExportAsync(request, format, ct).ConfigureAwait(false);
        stopwatch.Stop();

        if (rendered.IsFailure)
        {
            await PersistRunAsync(row.Id, Outcomes.ExportFailed, 0,
                (int)stopwatch.ElapsedMilliseconds, rendered.ErrorMessage, ct).ConfigureAwait(false);
            return Result<byte[]>.Failure(rendered.ErrorCode!, rendered.ErrorMessage!);
        }

        await PersistRunAsync(row.Id, Outcomes.Success, gridRows.Count,
            (int)stopwatch.ElapsedMilliseconds, null, ct).ConfigureAwait(false);
        await EmitAuditAsync(row, gridRows.Count, (int)stopwatch.ElapsedMilliseconds,
            Outcomes.Success, ct).ConfigureAwait(false);

        return Result<byte[]>.Success(rendered.Value.Content);
    }

    /// <summary>
    /// Shared materialisation pipeline used by <see cref="RunAsync"/> and
    /// <see cref="ExportAsync"/>. Loads the template, splices the QBE filter, gates
    /// through the budget guard, applies ordering / grouping, and projects each row
    /// into a dictionary keyed by selected-field name.
    /// </summary>
    /// <param name="row">Persisted template row.</param>
    /// <param name="skip">Rows to skip; 0 for exports.</param>
    /// <param name="take">Page size; <see cref="int.MaxValue"/> for exports.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Engine result.</returns>
    private async Task<Result<ReportExecutionResultDto>> ExecuteAsync(
        ReportTemplate row,
        int skip,
        int take,
        CancellationToken ct)
    {
        if (!EntityDispatch.TryGetValue(row.Registry, out var queryFactory)
            || !EntityTypes.TryGetValue(row.Registry, out var entityType))
        {
            return Result<ReportExecutionResultDto>.Failure(
                ErrorCodes.QbeRegistryUnknown,
                $"Registry '{row.Registry}' is not wired into the report engine.");
        }

        var schema = _schemas.GetForRegistry(row.Registry);
        if (schema is null)
        {
            return Result<ReportExecutionResultDto>.Failure(
                ErrorCodes.QbeRegistryUnknown,
                $"Registry '{row.Registry}' has no QBE schema.");
        }

        // Decode the persisted JSON payloads. A schema drift after a registry edit
        // can leave a template with stale fields — surface those as Validation
        // failures so the caller can repair the template rather than seeing a 500.
        IReadOnlyList<string> selectedFields;
        IReadOnlyList<ReportOrderingDto> ordering;
        QbeFilterDto filter;
        try
        {
            selectedFields = (IReadOnlyList<string>?)JsonSerializer.Deserialize<List<string>>(row.SelectedFieldsJson, JsonOptions)
                ?? Array.Empty<string>();
            ordering = (IReadOnlyList<ReportOrderingDto>?)JsonSerializer.Deserialize<List<ReportOrderingDto>>(row.OrderingJson, JsonOptions)
                ?? Array.Empty<ReportOrderingDto>();
            filter = JsonSerializer.Deserialize<QbeFilterDto>(row.FilterJson, JsonOptions)
                ?? new QbeFilterDto(QbeFilter.CombinatorAnd, Array.Empty<QbeConditionDto>());
        }
        catch (JsonException ex)
        {
            return Result<ReportExecutionResultDto>.Failure(
                ErrorCodes.ValidationFailed,
                $"Report template payload is malformed: {ex.Message}");
        }

        for (var i = 0; i < selectedFields.Count; i++)
        {
            if (schema.FindField(selectedFields[i]) is null)
            {
                return Result<ReportExecutionResultDto>.Failure(
                    ErrorCodes.QbeFieldNotQueryable,
                    $"Field '{selectedFields[i]}' is not declared on registry '{row.Registry}'.");
            }
        }

        var query = queryFactory(_db);

        // Splice the QBE filter — dispatch through reflection because the converter
        // is generic in TEntity.
        if (filter.Conditions is not null && filter.Conditions.Count > 0)
        {
            var qbeDomain = ToDomainFilter(filter);
            var convertMethod = typeof(IQbeToLinqConverter)
                .GetMethod(nameof(IQbeToLinqConverter.Convert))!
                .MakeGenericMethod(entityType);
            var converted = convertMethod.Invoke(_qbeConverter, new object?[] { row.Registry, qbeDomain });
            // Result<Expression<Func<T,bool>>>
            var resultType = converted!.GetType();
            var isSuccess = (bool)resultType.GetProperty(nameof(Result<int>.IsSuccess))!.GetValue(converted)!;
            if (!isSuccess)
            {
                var code = (string?)resultType.GetProperty(nameof(Result<int>.ErrorCode))!.GetValue(converted);
                var message = (string?)resultType.GetProperty(nameof(Result<int>.ErrorMessage))!.GetValue(converted);
                return Result<ReportExecutionResultDto>.Failure(
                    code ?? ErrorCodes.ValidationFailed,
                    message ?? "QBE conversion failed.");
            }
            var predicate = resultType.GetProperty(nameof(Result<int>.Value))!.GetValue(converted)!;
            query = ApplyWhere(query, entityType, (LambdaExpression)predicate);
        }

        // Budget guard — count BEFORE applying any ordering/skip/take.
        var ctxBuilder = new QueryFilterContext();
        if (filter.Conditions is { Count: > 0 } cs)
        {
            ctxBuilder = ctxBuilder.With("Qbe", cs.Count.ToString(CultureInfo.InvariantCulture));
        }
        var verdict = await _budget.EvaluateAsync(row.Registry, query, ctxBuilder, ct).ConfigureAwait(false);
        if (!verdict.Allowed)
        {
            return Result<ReportExecutionResultDto>.Failure(
                ErrorCodes.QueryTooBroad,
                QueryBudgetFailureEnvelope.FailureMessage);
        }

        // GROUP BY path — single field, count aggregate.
        if (!string.IsNullOrEmpty(row.GroupByField))
        {
            return await ExecuteGroupByAsync(query, entityType, row.GroupByField!, verdict.EstimatedRowCount, ct)
                .ConfigureAwait(false);
        }

        // Multi-column ordering.
        query = ApplyOrdering(query, entityType, ordering);

        // Page projection — materialise into anonymous-typed dictionary rows.
        var skipNorm = Math.Max(0, skip);
        var takeNorm = take == int.MaxValue ? int.MaxValue : Math.Clamp(take, 1, 10_000);
        var rows = await MaterialiseAsync(query, entityType, selectedFields, skipNorm, takeNorm, ct)
            .ConfigureAwait(false);

        var resultDto = new ReportExecutionResultDto(
            Columns: selectedFields,
            Rows: rows,
            TotalRowCount: verdict.EstimatedRowCount,
            ElapsedMs: 0);
        return Result<ReportExecutionResultDto>.Success(resultDto);
    }

    /// <summary>
    /// Builds the GROUP BY pipeline — projects entity → (key, count) → dictionary
    /// row. The total-row-count slot carries the unique group count.
    /// </summary>
    /// <param name="query">Filter-applied queryable.</param>
    /// <param name="entityType">Entity CLR type.</param>
    /// <param name="groupByField">Property name to group by.</param>
    /// <param name="prePagingTotal">Pre-aggregation matched row count from the budget verdict.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Engine result with one row per group.</returns>
    private static async Task<Result<ReportExecutionResultDto>> ExecuteGroupByAsync(
        IQueryable query,
        Type entityType,
        string groupByField,
        int prePagingTotal,
        CancellationToken ct)
    {
        var groupProperty = entityType.GetProperty(groupByField,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (groupProperty is null)
        {
            return Result<ReportExecutionResultDto>.Failure(
                ErrorCodes.QbeFieldNotQueryable,
                $"Entity '{entityType.Name}' does not expose property '{groupByField}'.");
        }

        // Build: list = await query.ToListAsync(ct).
        // We then group client-side because EF's group-by translation rules are
        // provider-specific (Postgres + InMemory + SQL Server each have their own
        // quirks); reading the row set once and aggregating in memory keeps the
        // engine generic across providers while remaining bounded by the budget
        // guard's row cap.
        var toListMethod = typeof(EntityFrameworkQueryableExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(EntityFrameworkQueryableExtensions.ToListAsync)
                && m.GetParameters().Length == 2)
            .MakeGenericMethod(entityType);
        var task = (Task)toListMethod.Invoke(null, new object[] { query, ct })!;
        await task.ConfigureAwait(false);
        var resultProperty = task.GetType().GetProperty("Result")!;
        var entities = (System.Collections.IEnumerable)resultProperty.GetValue(task)!;

        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entities)
        {
            var key = groupProperty.GetValue(e);
            var keyStr = key?.ToString() ?? string.Empty;
            groups[keyStr] = groups.GetValueOrDefault(keyStr) + 1;
        }

        var aggregateRows = groups
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new ReportRowDto(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [groupByField] = kvp.Key,
                ["count"] = kvp.Value,
            }))
            .ToList();

        return Result<ReportExecutionResultDto>.Success(new ReportExecutionResultDto(
            Columns: new[] { groupByField, "count" },
            Rows: aggregateRows,
            TotalRowCount: prePagingTotal,
            ElapsedMs: 0));
    }

    /// <summary>
    /// Splices a predicate onto an untyped <see cref="IQueryable"/> via the generic
    /// <c>Queryable.Where</c> overload.
    /// </summary>
    /// <param name="query">Source query.</param>
    /// <param name="entityType">Entity CLR type.</param>
    /// <param name="predicate">Predicate lambda.</param>
    /// <returns>The narrowed query.</returns>
    private static IQueryable ApplyWhere(IQueryable query, Type entityType, LambdaExpression predicate)
    {
        var whereMethod = typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m =>
                m.Name == nameof(Queryable.Where)
                && m.GetParameters().Length == 2
                && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
            .MakeGenericMethod(entityType);
        return (IQueryable)whereMethod.Invoke(null, new object[] { query, predicate })!;
    }

    /// <summary>
    /// Applies a multi-column ordering by reflectively calling
    /// <c>Queryable.OrderBy</c> / <c>OrderByDescending</c> for the first column and
    /// <c>ThenBy</c> / <c>ThenByDescending</c> for subsequent columns.
    /// </summary>
    /// <param name="query">Source query.</param>
    /// <param name="entityType">Entity CLR type.</param>
    /// <param name="ordering">Ordering specifications.</param>
    /// <returns>The ordered query.</returns>
    private static IQueryable ApplyOrdering(IQueryable query, Type entityType, IReadOnlyList<ReportOrderingDto> ordering)
    {
        if (ordering.Count == 0)
        {
            // EF requires a stable order for Skip/Take; default to Id ascending when
            // the template did not declare an order.
            var idProp = entityType.GetProperty("Id");
            if (idProp is null)
            {
                return query;
            }
            return ApplyOrderingStep(query, entityType, idProp, descending: false, isFirst: true);
        }
        var current = query;
        for (var i = 0; i < ordering.Count; i++)
        {
            var spec = ordering[i];
            var prop = entityType.GetProperty(spec.Field,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is null)
            {
                continue;
            }
            var descending = string.Equals(spec.Direction, ReportOrderingDto.Desc, StringComparison.OrdinalIgnoreCase);
            current = ApplyOrderingStep(current, entityType, prop, descending, isFirst: i == 0);
        }
        return current;
    }

    /// <summary>
    /// Reflective implementation of the OrderBy / ThenBy / ...Descending step.
    /// </summary>
    /// <param name="query">Source query.</param>
    /// <param name="entityType">Entity type.</param>
    /// <param name="prop">Property to order by.</param>
    /// <param name="descending">True for descending.</param>
    /// <param name="isFirst">True for the first column (OrderBy*), false for subsequent (ThenBy*).</param>
    /// <returns>The ordered query.</returns>
    private static IQueryable ApplyOrderingStep(
        IQueryable query,
        Type entityType,
        PropertyInfo prop,
        bool descending,
        bool isFirst)
    {
        var param = Expression.Parameter(entityType, "e");
        var body = Expression.Property(param, prop);
        var lambda = Expression.Lambda(body, param);
        var methodName = isFirst
            ? (descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy))
            : (descending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy));
        var method = typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m =>
                m.Name == methodName
                && m.GetParameters().Length == 2)
            .MakeGenericMethod(entityType, prop.PropertyType);
        return (IQueryable)method.Invoke(null, new object[] { query, lambda })!;
    }

    /// <summary>
    /// Materialises the query into <see cref="ReportRowDto"/> rows by projecting the
    /// selected fields one at a time. Skip/Take are applied in-IQueryable so EF can
    /// translate them to SQL LIMIT/OFFSET when the provider supports it.
    /// </summary>
    /// <param name="query">Ordered query.</param>
    /// <param name="entityType">Entity CLR type.</param>
    /// <param name="selectedFields">Field-name projection list.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Page size.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The materialised dictionary rows.</returns>
    private static async Task<IReadOnlyList<ReportRowDto>> MaterialiseAsync(
        IQueryable query,
        Type entityType,
        IReadOnlyList<string> selectedFields,
        int skip,
        int take,
        CancellationToken ct)
    {
        // Skip / Take on IQueryable.
        var skipMethod = typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(Queryable.Skip) && m.GetParameters().Length == 2)
            .MakeGenericMethod(entityType);
        var takeMethod = typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(Queryable.Take)
                && m.GetParameters().Length == 2
                && m.GetParameters()[1].ParameterType == typeof(int))
            .MakeGenericMethod(entityType);
        if (skip > 0)
        {
            query = (IQueryable)skipMethod.Invoke(null, new object[] { query, skip })!;
        }
        if (take != int.MaxValue)
        {
            query = (IQueryable)takeMethod.Invoke(null, new object[] { query, take })!;
        }

        // Cache property reflectors so we don't pay the lookup cost per row.
        var props = new List<PropertyInfo>(selectedFields.Count);
        for (var i = 0; i < selectedFields.Count; i++)
        {
            var p = entityType.GetProperty(selectedFields[i],
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p is not null)
            {
                props.Add(p);
            }
        }

        var toListMethod = typeof(EntityFrameworkQueryableExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(EntityFrameworkQueryableExtensions.ToListAsync)
                && m.GetParameters().Length == 2)
            .MakeGenericMethod(entityType);
        var task = (Task)toListMethod.Invoke(null, new object[] { query, ct })!;
        await task.ConfigureAwait(false);
        var entities = (System.Collections.IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!;

        var rows = new List<ReportRowDto>();
        foreach (var e in entities)
        {
            var cells = new Dictionary<string, object?>(props.Count, StringComparer.Ordinal);
            for (var i = 0; i < props.Count; i++)
            {
                cells[props[i].Name] = props[i].GetValue(e);
            }
            rows.Add(new ReportRowDto(cells));
        }
        return rows;
    }

    /// <summary>
    /// Wire → domain projection of the persisted filter envelope, mirroring the
    /// converter in <c>SolicitantsController</c>.
    /// </summary>
    /// <param name="dto">Persisted filter; non-null.</param>
    /// <returns>Domain envelope.</returns>
    private static QbeFilter ToDomainFilter(QbeFilterDto dto)
    {
        var conditions = new List<QbeCondition>(dto.Conditions?.Count ?? 0);
        if (dto.Conditions is not null)
        {
            foreach (var c in dto.Conditions)
            {
                if (!Enum.TryParse<QbeOperator>(c.Operator, ignoreCase: false, out var op))
                {
                    op = (QbeOperator)int.MinValue;
                }
                conditions.Add(new QbeCondition(c.FieldName, op, c.Value, c.Value2));
            }
        }
        return new QbeFilter(
            string.IsNullOrEmpty(dto.Combinator) ? QbeFilter.CombinatorAnd : dto.Combinator,
            conditions);
    }

    /// <summary>
    /// Heuristic detection of the cell-value runtime type used by the export
    /// pipeline. Inspects the first non-null cell for the named column and maps
    /// the CLR type onto a <see cref="GridColumnDataType"/>. Falls back to
    /// <see cref="GridColumnDataType.Text"/> when no row has a value.
    /// </summary>
    /// <param name="rows">Projected rows.</param>
    /// <param name="column">Column name to inspect.</param>
    /// <returns>The inferred data type.</returns>
    private static GridColumnDataType InferDataType(IReadOnlyList<ReportRowDto> rows, string column)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Cells.TryGetValue(column, out var value) && value is not null)
            {
                return value switch
                {
                    DateTime => GridColumnDataType.DateTime,
                    DateOnly => GridColumnDataType.Date,
                    bool => GridColumnDataType.Boolean,
                    decimal or double or float => GridColumnDataType.Decimal,
                    byte or short or int or long or sbyte or ushort or uint or ulong => GridColumnDataType.Integer,
                    _ => GridColumnDataType.Text,
                };
            }
        }
        return GridColumnDataType.Text;
    }

    /// <summary>
    /// Resolves the persisted outcome string for an engine result. Maps the
    /// service-layer error codes back to one of the stable outcome literals.
    /// </summary>
    /// <param name="result">Engine result.</param>
    /// <returns>The outcome literal.</returns>
    private static string ResolveOutcome(Result<ReportExecutionResultDto> result)
    {
        if (result.IsSuccess)
        {
            return Outcomes.Success;
        }
        return result.ErrorCode switch
        {
            ErrorCodes.QueryTooBroad => Outcomes.BudgetExceeded,
            ErrorCodes.ExportTooLarge or ErrorCodes.ExportFormatNotSupported => Outcomes.ExportFailed,
            _ => Outcomes.ValidationFailed,
        };
    }

    /// <summary>
    /// Returns <c>true</c> when the calling user is permitted to execute the
    /// template — they own it, the template is shared, OR the caller is the row's
    /// owner. Anonymous callers are refused.
    /// </summary>
    /// <param name="row">Persisted template.</param>
    /// <returns><c>true</c> when access is permitted.</returns>
    private bool CanAccess(ReportTemplate row)
    {
        if (_caller.UserId is not long callerId)
        {
            return false;
        }
        return row.OwnerUserId == callerId || row.IsShared;
    }

    /// <summary>Persists a <see cref="ReportRun"/> capturing the outcome of an engine call.</summary>
    /// <param name="templateId">Template id.</param>
    /// <param name="outcome">Outcome literal.</param>
    /// <param name="rowCount">Materialised row count (0 on failure).</param>
    /// <param name="durationMs">Wall-clock duration.</param>
    /// <param name="failureReason">Optional human-readable failure message.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PersistRunAsync(
        long templateId,
        string outcome,
        int rowCount,
        int durationMs,
        string? failureReason,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var executor = _caller.UserId ?? 0L;
        // Trim oversized failure messages so a runaway exception text cannot blow
        // through the 512-char column cap.
        var trimmedReason = failureReason is { Length: > 512 }
            ? failureReason[..512]
            : failureReason;
        _db.ReportRuns.Add(new ReportRun
        {
            ReportTemplateId = templateId,
            ExecutedByUserId = executor,
            ExecutedAtUtc = now,
            RowCount = rowCount,
            OutcomeCode = outcome,
            DurationMs = durationMs,
            FailureReason = trimmedReason,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Emits the <c>REPORT.EXECUTED</c> audit row.</summary>
    /// <param name="row">Template row.</param>
    /// <param name="rowCount">Materialised row count.</param>
    /// <param name="durationMs">Wall-clock duration.</param>
    /// <param name="outcome">Outcome literal.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(
        ReportTemplate row,
        int rowCount,
        int durationMs,
        string outcome,
        CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            templateSqid = row.Id,
            rowCount,
            durationMs,
            outcome,
        });
        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: "REPORT.EXECUTED",
            severity: AuditSeverity.Information,
            actorId: actor,
            targetEntity: nameof(ReportTemplate),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
