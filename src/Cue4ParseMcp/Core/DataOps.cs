using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using NJ = Newtonsoft.Json;

namespace Cue4ParseMcp.Core;

/// <summary>One returned DataTable row. Columns the row does not serialize are omitted from <see cref="Values"/>; a present null stays JSON null.</summary>
public sealed record TableRow(string RowName, IReadOnlyDictionary<string, JsonElement> Values)
{
    /// <summary>Resolved text of FText cells (cell path such as "Name" or "Items[1].Label" to string); only set when a culture was requested.</summary>
    public IReadOnlyDictionary<string, string>? LocalizedString { get; init; }
}

/// <summary>Localisation lookup result. A key with no entry in the culture maps to null.</summary>
public sealed record LocalizeResult(string Culture, string Namespace, IReadOnlyDictionary<string, string?> Strings);

public sealed record TableQueryResult(
    string Path,
    string Class,
    string? RowStruct,
    int TotalRows,
    int MatchedRows,
    int Offset,
    int Limit,
    int Returned,
    bool HasMore,
    int? NextOffset,
    IReadOnlyList<string> AvailableColumns,
    IReadOnlyList<string> SelectedColumns,
    IReadOnlyList<TableRow> Rows);

/// <summary>Full JSON exports and DataTable queries/exports. Values use CUE4Parse's own JSON serialization.</summary>
public sealed class DataOps(GameManager games)
{
    public const int MaxQueryLimit = 500;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonWriterOptions TableJsonOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // readable non-ASCII; the file is not embedded in HTML
    };

    private const int FlushThreshold = 64 * 1024;

    #region Full JSON

    public async Task<ExportReport> ExportJsonAsync(string? game, string path, string? outDir, CancellationToken ct)
    {
        using var lease = await games.AcquireAsync(game, ct);
        return await ExportJsonAsync(lease, path, outDir, ct);
    }

    /// <summary>Borrows <paramref name="lease"/> without disposing it. A package path writes every export; "Pkg.Object" writes that export.</summary>
    public async Task<ExportReport> ExportJsonAsync(GameLease lease, string path, string? outDir, CancellationToken ct)
    {
        var session = lease.Session;
        var (packagePath, objectName) = session.ParsePath(path);
        var package = session.LoadPackage(packagePath);
        var single = objectName is null
            ? null
            : package.GetExportOrNull(objectName, StringComparison.OrdinalIgnoreCase)
              ?? throw new InvalidDataException($"{packagePath} has no export named '{objectName}'. Exports: {string.Join(", ", session.GetExports(packagePath).Select(e => e.Name))}");

        var outputDir = Path.GetFullPath(games.ResolveOutputDir(lease, outDir));
        var destination = ExportFiles.AssetPath(session, path, outputDir, "json");
        await ExportFiles.WriteAsync(destination, (stream, token) =>
        {
            WritePackageJson(stream, package, single, token);
            return Task.CompletedTask;
        }, ct);
        return new ExportReport(outputDir, [destination], [], []);
    }

    /// <summary>Same payload as inspect_asset's property JSON, streamed and never truncated.</summary>
    private static void WritePackageJson(Stream stream, IPackage package, UObject? single, CancellationToken ct)
    {
        var serializer = NJ.JsonSerializer.CreateDefault();
        using var text = new StreamWriter(stream, Utf8NoBom, 64 * 1024, leaveOpen: true);
        using var writer = new NJ.JsonTextWriter(text) { Formatting = NJ.Formatting.Indented };
        if (single is not null)
        {
            serializer.Serialize(writer, single);
        }
        else
        {
            writer.WriteStartArray();
            foreach (var export in package.ExportsLazy)
            {
                ct.ThrowIfCancellationRequested();
                serializer.Serialize(writer, export.Value);
            }
            writer.WriteEndArray();
        }
        writer.Flush();
    }

    #endregion

    #region DataTables

    public async Task<TableQueryResult> QueryTableAsync(string? game, string path, string[]? columns, TableFilter[]? filters, int offset, int limit, CancellationToken ct, string? culture = null)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "offset must be 0 or greater.");
        if (limit is < 1 or > MaxQueryLimit) throw new ArgumentOutOfRangeException(nameof(limit), limit, $"limit must be between 1 and {MaxQueryLimit}.");

        using var lease = await games.AcquireAsync(game, ct);
        var (table, tablePath) = LoadTable(lease.Session, path);
        var plan = TablePlan.Create(table, columns, filters);
        var serializer = NJ.JsonSerializer.CreateDefault();
        var strings = culture is null ? null : SelectCulture(lease.Session, culture);

        var rows = new List<TableRow>();
        var matched = 0;
        foreach (var (name, row) in table.RowMap)
        {
            ct.ThrowIfCancellationRequested();
            var rowName = name.Text;
            if (!plan.Matches(rowName, row, serializer)) continue;
            if (matched++ < offset || rows.Count == limit) continue;

            using var cells = SerializeCells(row, plan.SelectedSet, serializer);
            var values = new Dictionary<string, JsonElement>(plan.Selected.Length, StringComparer.Ordinal);
            var lookup = Cells(cells);
            foreach (var column in plan.Selected)
                if (lookup.TryGetValue(column, out var value)) values[column] = value.Clone();
            Dictionary<string, string>? localized = null;
            if (strings is not null)
            {
                localized = [];
                foreach (var (column, value) in values) CollectLocalized(column, value, strings, localized);
            }
            rows.Add(new TableRow(rowName, values) { LocalizedString = localized });
        }

        var hasMore = offset + rows.Count < matched;
        return new TableQueryResult(tablePath, table.ExportType, table.RowStructName, table.RowMap.Count, matched,
            offset, limit, rows.Count, hasMore, hasMore ? offset + rows.Count : null,
            plan.Available, plan.Selected, rows);
    }

    #endregion

    #region Localisation

    /// <summary>Resolves <paramref name="keys"/> in <paramref name="ns"/> for <paramref name="culture"/> from the game's .locres files.</summary>
    public async Task<LocalizeResult> LocalizeAsync(string? game, string[] keys, string culture, string ns, CancellationToken ct)
    {
        if (keys is not { Length: > 0 }) throw new ArgumentException("keys must name at least one text key.", nameof(keys));
        using var lease = await games.AcquireAsync(game, ct);
        var strings = SelectCulture(lease.Session, culture);
        var result = new Dictionary<string, string?>(keys.Length, StringComparer.Ordinal);
        foreach (var key in keys)
            result[key] = strings.TryGetValue(ns, out var space) && space.TryGetValue(key, out var text) && text.Length > 0 ? text : null;
        return new LocalizeResult(culture, ns, result);
    }

    /// <summary>Loads the culture's .locres entries into the provider (a provider-wide switch) and returns them.</summary>
    private static InternationalizationDictionary SelectCulture(GameSession session, string culture)
    {
        var i18n = session.Provider.Internationalization;
        lock (i18n)
        {
            if (!string.Equals(i18n.Culture, culture, StringComparison.OrdinalIgnoreCase) && !session.Provider.TryChangeCulture(culture))
                throw new ArgumentException($"Culture '{culture}' is not available. Available: {string.Join(", ", i18n.AvailableCultures)}");
            return i18n;
        }
    }

    /// <summary>Walks a cell and records the localized string of every FText (an object carrying Namespace, Key and SourceString).</summary>
    private static void CollectLocalized(string path, JsonElement cell, InternationalizationDictionary strings, Dictionary<string, string> into)
    {
        switch (cell.ValueKind)
        {
            case JsonValueKind.Object:
                if (cell.TryGetProperty("Key", out var key) && key.ValueKind == JsonValueKind.String && cell.TryGetProperty("SourceString", out _))
                {
                    var ns = cell.TryGetProperty("Namespace", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                    var text = strings.SafeGet(ns, key.GetString()!);
                    if (text.Length > 0) into[path] = text;
                    return;
                }
                foreach (var property in cell.EnumerateObject()) CollectLocalized($"{path}.{property.Name}", property.Value, strings, into);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in cell.EnumerateArray()) CollectLocalized($"{path}[{index++}]", item, strings, into);
                break;
        }
    }

    #endregion

    #region DataTable export

    public async Task<ExportReport> ExportTableAsync(string? game, string path, string format, string[]? columns, TableFilter[]? filters, string? outDir, CancellationToken ct)
    {
        ParseTableFormat(format);
        using var lease = await games.AcquireAsync(game, ct);
        return await ExportTableAsync(lease, path, format, columns, filters, outDir, ct);
    }

    /// <summary>Borrows <paramref name="lease"/> without disposing it. Writes every matching row (no paging).</summary>
    public async Task<ExportReport> ExportTableAsync(GameLease lease, string path, string format, string[]? columns, TableFilter[]? filters, string? outDir, CancellationToken ct)
    {
        var csv = ParseTableFormat(format);
        var session = lease.Session;
        var (table, tablePath) = LoadTable(session, path);
        var plan = TablePlan.Create(table, columns, filters);

        var outputDir = Path.GetFullPath(games.ResolveOutputDir(lease, outDir));
        var destination = ExportFiles.AssetPath(session, path, outputDir, csv ? "table.csv" : "table.json");
        await ExportFiles.WriteAsync(destination, (stream, token) => csv
            ? WriteTableCsvAsync(stream, table, plan, token)
            : WriteTableJsonAsync(stream, table, tablePath, plan, filters ?? [], token), ct);
        return new ExportReport(outputDir, [destination], [], []);
    }

    private static bool ParseTableFormat(string format) => format.Trim().ToLowerInvariant() switch
    {
        "json" => false,
        "csv" => true,
        _ => throw new ArgumentException($"Unknown table format '{format}'. Use json or csv.")
    };

    private static async Task WriteTableJsonAsync(Stream stream, UDataTable table, string tablePath, TablePlan plan, TableFilter[] filters, CancellationToken ct)
    {
        var serializer = NJ.JsonSerializer.CreateDefault();
        await using var writer = new Utf8JsonWriter(stream, TableJsonOptions);
        writer.WriteStartObject();
        writer.WriteString("table", tablePath);
        writer.WriteString("class", table.ExportType);
        writer.WriteString("rowStruct", table.RowStructName);
        writer.WriteStartArray("columns");
        foreach (var column in plan.Selected) writer.WriteStringValue(column);
        writer.WriteEndArray();
        writer.WriteStartArray("filters");
        foreach (var filter in filters)
        {
            writer.WriteStartObject();
            writer.WriteString("column", filter.Column);
            writer.WriteString("op", filter.Op);
            writer.WritePropertyName("value");
            filter.Value.WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        var matched = 0;
        writer.WriteStartArray("rows");
        foreach (var (name, row) in table.RowMap)
        {
            ct.ThrowIfCancellationRequested();
            using var cells = SerializeCells(row, plan.NeededSet, serializer);
            var lookup = Cells(cells);
            var rowName = name.Text;
            if (!plan.Matches(rowName, lookup)) continue;
            matched++;

            writer.WriteStartObject();
            writer.WriteString("rowName", rowName);
            writer.WriteStartObject("values");
            foreach (var column in plan.Selected)
            {
                if (!lookup.TryGetValue(column, out var value)) continue;
                writer.WritePropertyName(column);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            if (writer.BytesPending >= FlushThreshold) await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();
        writer.WriteNumber("totalRows", table.RowMap.Count);
        writer.WriteNumber("matchedRows", matched);
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    private static async Task WriteTableCsvAsync(Stream stream, UDataTable table, TablePlan plan, CancellationToken ct)
    {
        var serializer = NJ.JsonSerializer.CreateDefault();
        await using var text = new StreamWriter(stream, TableCsv.Encoding, 64 * 1024, leaveOpen: true) { NewLine = TableCsv.NewLine };
        var line = new StringBuilder();
        line.Append(TableCsv.FormatText(TableFilters.RowNameColumn));
        foreach (var column in plan.Selected) line.Append(',').Append(TableCsv.FormatText(column));
        await text.WriteLineAsync(line, ct);

        foreach (var (name, row) in table.RowMap)
        {
            ct.ThrowIfCancellationRequested();
            using var cells = SerializeCells(row, plan.NeededSet, serializer);
            var lookup = Cells(cells);
            var rowName = name.Text;
            if (!plan.Matches(rowName, lookup)) continue;

            line.Clear().Append(TableCsv.FormatText(rowName));
            foreach (var column in plan.Selected)
            {
                JsonElement? cell = lookup.TryGetValue(column, out var value) ? value : null;
                if (cell is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } nested)
                    throw new InvalidOperationException(
                        $"Column '{column}' of row '{rowName}' is {TableFilters.Describe(nested.ValueKind)}; CSV export only supports scalar cells. " +
                        "Select scalar columns or use format=json.");
                line.Append(',').Append(TableCsv.FormatCell(cell));
            }
            await text.WriteLineAsync(line, ct);
        }
        await text.FlushAsync(ct);
    }

    private static (UDataTable Table, string TablePath) LoadTable(GameSession session, string path)
    {
        var (packagePath, _) = session.ParsePath(path);
        var obj = session.LoadMain(path);
        if (obj is not UDataTable table)
            throw new InvalidOperationException($"{path} is a {obj.ExportType}, not a DataTable. Use export_json for other assets.");
        if (table.RowMap is null)
            throw new InvalidDataException($"{packagePath}.{table.Name}: rows could not be read because its row struct " +
                                           $"'{table.RowStructName ?? "(unknown)"}' could not be resolved (check the game's mappings).");
        return (table, packagePath + "." + table.Name);
    }

    /// <summary>Column name exactly as CUE4Parse's FStructFallback converter writes it.</summary>
    private static string ColumnName(FPropertyTag property) =>
        property.ArrayIndex > 0 ? $"{property.Name.Text}[{property.ArrayIndex}]" : property.Name.Text;

    /// <summary>Serializes the wanted top-level properties of a row like FStructFallbackConverter, into a JSON object.</summary>
    private static JsonDocument SerializeCells(FStructFallback row, IReadOnlySet<string> wanted, NJ.JsonSerializer serializer)
    {
        var buffer = new MemoryStream();
        using (var text = new StreamWriter(buffer, Utf8NoBom, 4096, leaveOpen: true))
        using (var writer = new NJ.JsonTextWriter(text))
        {
            writer.WriteStartObject();
            foreach (var property in row.Properties)
            {
                var name = ColumnName(property);
                if (!wanted.Contains(name)) continue;
                writer.WritePropertyName(name);
                serializer.Serialize(writer, property.Tag);
            }
            writer.WriteEndObject();
        }
        // The document keeps referencing this buffer; it is not reused.
        return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
    }

    private static Dictionary<string, JsonElement> Cells(JsonDocument cells)
    {
        var lookup = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in cells.RootElement.EnumerateObject()) lookup[property.Name] = property.Value; // last duplicate wins
        return lookup;
    }

    private sealed record CompiledFilter(string? Column, FilterOp Op, JsonElement Value);

    /// <summary>Resolved columns and filters for one table.</summary>
    private sealed class TablePlan
    {
        public required string[] Available { get; init; }
        public required string[] Selected { get; init; }
        public required HashSet<string> SelectedSet { get; init; }
        public required CompiledFilter[] Filters { get; init; }
        /// <summary>Columns referenced by filters.</summary>
        public required HashSet<string> FilterSet { get; init; }
        /// <summary>Selected plus filter columns.</summary>
        public required HashSet<string> NeededSet { get; init; }

        public static TablePlan Create(UDataTable table, string[]? columns, TableFilter[]? filters)
        {
            var available = new List<string>();
            var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in table.RowMap.Values)
            foreach (var property in row.Properties)
            {
                var name = ColumnName(property);
                if (canonical.TryAdd(name, name)) available.Add(name);
            }

            string Resolve(string? requested, string context)
            {
                var trimmed = requested?.Trim();
                if (string.IsNullOrEmpty(trimmed)) throw new ArgumentException($"{context}: column name is empty.");
                return canonical.TryGetValue(trimmed, out var name)
                    ? name
                    : throw new ArgumentException($"{context}: unknown column '{trimmed}'. Available columns: {RowNameColumnList(available)}.");
            }

            string[] selected;
            if (columns is not { Length: > 0 })
            {
                selected = [.. available];
            }
            else
            {
                var picked = new List<string>();
                foreach (var requested in columns)
                {
                    if (requested?.Trim() is { } trimmed && trimmed.Equals(TableFilters.RowNameColumn, StringComparison.OrdinalIgnoreCase))
                        continue; // always included as the row key
                    var name = Resolve(requested, "columns");
                    if (!picked.Contains(name, StringComparer.Ordinal)) picked.Add(name);
                }
                selected = [.. picked];
            }

            var compiled = new List<CompiledFilter>();
            foreach (var (filter, index) in (filters ?? []).Select((f, i) => (f, i)))
            {
                var context = $"filters[{index}]";
                if (filter is null) throw new ArgumentException($"{context} is null.");
                var op = TableFilters.ParseOp(filter.Op);
                TableFilters.ValidateValue(op, filter.Value, context);
                if (filter.Column?.Trim() is { } key && key.Equals(TableFilters.RowNameColumn, StringComparison.OrdinalIgnoreCase))
                {
                    if (filter.Value.ValueKind != JsonValueKind.String)
                        throw new ArgumentException($"{context}: {TableFilters.RowNameColumn} is a string; the value must be a string, not {TableFilters.Describe(filter.Value.ValueKind)}.");
                    compiled.Add(new CompiledFilter(null, op, filter.Value));
                }
                else
                {
                    compiled.Add(new CompiledFilter(Resolve(filter.Column, context), op, filter.Value));
                }
            }

            var filterSet = compiled.Where(f => f.Column is not null).Select(f => f.Column!).ToHashSet(StringComparer.Ordinal);
            return new TablePlan
            {
                Available = [.. available],
                Selected = selected,
                SelectedSet = selected.ToHashSet(StringComparer.Ordinal),
                Filters = [.. compiled],
                FilterSet = filterSet,
                NeededSet = [.. selected.Concat(filterSet)]
            };
        }

        private static string RowNameColumnList(List<string> available) =>
            string.Join(", ", available.Prepend(TableFilters.RowNameColumn));

        /// <summary>Matches without serializing anything when every filter is on the row name.</summary>
        public bool Matches(string rowName, FStructFallback row, NJ.JsonSerializer serializer)
        {
            if (Filters.Length == 0) return true;
            if (FilterSet.Count == 0) return Matches(rowName, EmptyCells);
            using var cells = SerializeCells(row, FilterSet, serializer);
            return Matches(rowName, Cells(cells));
        }

        public bool Matches(string rowName, IReadOnlyDictionary<string, JsonElement> cells)
        {
            JsonElement? rowNameCell = null;
            foreach (var filter in Filters)
            {
                JsonElement? cell;
                if (filter.Column is null) cell = rowNameCell ??= JsonSerializer.SerializeToElement(rowName);
                else cell = cells.TryGetValue(filter.Column, out var value) ? value : null;
                if (!TableFilters.Evaluate(filter.Op, cell, filter.Value)) return false;
            }
            return true;
        }

        private static readonly IReadOnlyDictionary<string, JsonElement> EmptyCells = new Dictionary<string, JsonElement>();
    }

    #endregion
}
