using System.ComponentModel;
using Cue4ParseMcp.Core;
using ModelContextProtocol.Server;

namespace Cue4ParseMcp.Tools;

[McpServerToolType]
public sealed class DataTools(DataOps ops)
{
    private const string GameParam = "Game id from games.json (e.g. valorant, dbd). Defaults to the most recently loaded game.";
    private const string OutDirParam = "Folder to write into. Defaults to the game's outputDir from games.json. Files keep the in-game folder structure below it.";
    private const string TablePathParam = "DataTable asset path as returned by search_assets (type DataTable), e.g. Game/Content/Data/DT_Weapons. 'Pkg.Object' selects one export.";

    private const string ColumnsParam =
        "Top-level row columns to include, matched case-insensitively (fixed-array elements are named like 'Prop[1]'). " +
        "Omit for all columns; unknown names are rejected. The row name is always included.";

    private const string FiltersParam =
        "Row conditions, all of which must match, e.g. [{\"column\":\"Damage\",\"op\":\"gte\",\"value\":50},{\"column\":\"$rowName\",\"op\":\"contains\",\"value\":\"rifle\"}]. " +
        "column: a row column or $rowName. op: eq, ne, contains, gt, gte, lt, lte. Comparisons are typed (5 never equals \"5\"); " +
        "numbers by value; strings ordinal for eq/ne/ordering, contains is case-insensitive; contains on an array cell matches an equal element. " +
        "Null or missing cells only match eq null or ne <non-null>; object/array cells compare by deep equality for eq/ne and never match ordering.";

    [McpServerTool(Name = "export_json"), Description(
        "Write an asset's complete, untruncated property JSON (the same representation inspect_asset shows) to a .json file. " +
        "A package path writes a JSON array of every export; 'Pkg.Object' writes that one export as an object. Returns the written file path.")]
    public Task<ExportReport> ExportJson(
        [Description("Asset path as returned by search_assets. '/Game/...' and 'Pkg.Object' forms also work.")] string path,
        [Description(OutDirParam)] string? outDir = null,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
        => ops.ExportJsonAsync(game, path, outDir, ct);

    [McpServerTool(Name = "query_table", ReadOnly = true), Description(
        "Read DataTable rows with optional column projection, filters and paging. Returns rowName plus selected values per row " +
        "(columns a row doesn't serialize are omitted), totalRows, matchedRows, hasMore/nextOffset and the available columns. " +
        "Use export_table to write every matching row to a file.")]
    public Task<TableQueryResult> QueryTable(
        [Description(TablePathParam)] string path,
        [Description(ColumnsParam)] string[]? columns = null,
        [Description(FiltersParam)] TableFilter[]? filters = null,
        [Description("Matching rows to skip (0 or more).")] int offset = 0,
        [Description("Max rows to return (1-500).")] int limit = 50,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
        => ops.QueryTableAsync(game, path, columns, filters, offset, limit, ct);

    [McpServerTool(Name = "export_table"), Description(
        "Write every DataTable row matching the filters to <asset>.table.json or <asset>.table.csv. " +
        "json keeps nested values. csv is UTF-8 with BOM, CRLF, RFC 4180 quoting; null/missing cells are empty, empty strings are \"\"; " +
        "text starting with = + - @ tab CR or LF is prefixed with ' so spreadsheets don't run formulas; selecting a column with a struct/array value fails.")]
    public Task<ExportReport> ExportTable(
        [Description(TablePathParam)] string path,
        [Description("json or csv.")] string format = "json",
        [Description(ColumnsParam)] string[]? columns = null,
        [Description(FiltersParam)] TableFilter[]? filters = null,
        [Description(OutDirParam)] string? outDir = null,
        [Description(GameParam)] string? game = null,
        CancellationToken ct = default)
        => ops.ExportTableAsync(game, path, format, columns, filters, outDir, ct);
}
