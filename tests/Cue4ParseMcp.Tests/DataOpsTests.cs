using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Exceptions;
using Cue4ParseMcp.Config;
using CUE4Parse.UE4.Assets.Exports.Engine;
using Cue4ParseMcp.Core;

namespace Cue4ParseMcp.Tests;

public sealed class DataOpsTests
{
    private const string Game = "fixture";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableFilter F(string column, string op, object? value) => new(column, op, JsonSerializer.SerializeToElement(value));

    private static async Task<string> FindAsync(ParsedAssetFixture fixture, string name)
    {
        using var lease = await fixture.Games.AcquireAsync(Game, Ct);
        return Assert.Single(lease.Session.Search(name, null, null, 50, Ct).Results,
            hit => hit.Path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)).Path;
    }

    private static async Task<UDataTable> LoadTableAsync(ParsedAssetFixture fixture, string path)
    {
        using var lease = await fixture.Games.AcquireAsync(Game, Ct);
        return Assert.IsAssignableFrom<UDataTable>(lease.Session.LoadMain(path));
    }

    private static bool HasColumn(UDataTable table, string row, string column) =>
        table.RowMap.Single(kv => kv.Key.Text == row).Value.Properties.Any(p => p.Name.Text == column && p.ArrayIndex == 0);

    [Fact]
    public async Task QueryPagesInTableOrderAndProjectsColumns()
    {
        using var fixture = new ParsedAssetFixture();
        var data = new DataOps(fixture.Games);
        var path = await FindAsync(fixture, "DT_AllProperties");
        var table = await LoadTableAsync(fixture, path);
        var order = table.RowMap.Keys.Select(k => k.Text).ToArray();
        Assert.Equal(["Alpha", "Beta", "Defaults"], order.Order());

        var first = await data.QueryTableAsync(Game, path, null, null, 0, 2, Ct);
        Assert.Equal(path + ".DT_AllProperties", first.Path);
        Assert.Equal("DataTable", first.Class);
        Assert.Equal("FixtureTableRow", first.RowStruct);
        Assert.Equal((3, 3, 2, true, (int?)2), (first.TotalRows, first.MatchedRows, first.Returned, first.HasMore, first.NextOffset));
        Assert.Equal(order[..2], first.Rows.Select(r => r.RowName));
        foreach (var column in new[] { "Number", "LargeNumber", "Message", "Kind", "Nested", "Values" })
            Assert.Contains(column, first.AvailableColumns);
        Assert.Equal(first.AvailableColumns, first.SelectedColumns);

        var second = await data.QueryTableAsync(Game, path, null, null, 2, 2, Ct);
        Assert.Equal((1, false, (int?)null), (second.Returned, second.HasMore, second.NextOffset));
        Assert.Equal(order[2], Assert.Single(second.Rows).RowName);

        var beyond = await data.QueryTableAsync(Game, path, null, null, 10, 5, Ct);
        Assert.Equal((3, 0, false), (beyond.MatchedRows, beyond.Returned, beyond.HasMore));

        var projected = await data.QueryTableAsync(Game, path, ["message", "NUMBER", "$rowName", "Message"],
            [F("$rowName", "eq", "Alpha")], 0, 50, Ct);
        Assert.Equal(["Message", "Number"], projected.SelectedColumns);
        Assert.Equal(1, projected.MatchedRows);
        var alpha = Assert.Single(projected.Rows);
        Assert.Equal(["Message", "Number"], alpha.Values.Keys);
        Assert.Equal("Alpha_CUE4Parse_日本語_äöü", alpha.Values["Message"].GetString());
        Assert.Equal(0x12345678, alpha.Values["Number"].GetInt32());

        // Nested values keep CUE4Parse's structure; arrays stay arrays.
        var all = await data.QueryTableAsync(Game, path, null, [F("$rowName", "eq", "Beta")], 0, 1, Ct);
        var beta = Assert.Single(all.Rows).Values;
        Assert.Equal(JsonValueKind.Object, beta["Nested"].ValueKind);
        Assert.Equal([-7, 0, 7, int.MaxValue], beta["Values"].EnumerateArray().Select(v => v.GetInt32()));

        // Columns a row does not serialize are omitted rather than invented.
        var defaults = Assert.Single((await data.QueryTableAsync(Game, path, ["Message"], [F("$rowName", "eq", "Defaults")], 0, 1, Ct)).Rows);
        Assert.Equal(HasColumn(table, "Defaults", "Message"), defaults.Values.ContainsKey("Message"));
    }

    [Fact]
    public async Task FiltersAreTypedAndCombined()
    {
        using var fixture = new ParsedAssetFixture();
        var data = new DataOps(fixture.Games);
        var path = await FindAsync(fixture, "DT_AllProperties");
        var table = await LoadTableAsync(fixture, path);

        async Task<string[]> Rows(params TableFilter[] filters) =>
            (await data.QueryTableAsync(Game, path, ["Number"], filters, 0, 500, Ct)).Rows.Select(r => r.RowName).Order().ToArray();

        Assert.Equal(["Alpha"], await Rows(F("Number", "gt", 0)));
        Assert.Equal(["Beta"], await Rows(F("Number", "lt", 0)));
        Assert.Equal(["Alpha"], await Rows(F("Number", "gte", 0x12345678), F("number", "lte", 0x12345678)));
        Assert.Equal(["Beta"], await Rows(F("Message", "contains", "bETA_ß_水")));
        Assert.Equal(["Beta"], await Rows(F("LargeNumber", "eq", -9007199254740991L)));
        Assert.Equal(["Alpha"], await Rows(F("LargeNumber", "eq", 0x1122334455667788L)));
        Assert.Empty(await Rows(F("LargeNumber", "eq", 0x1122334455667789L)));
        Assert.Equal(["Beta"], await Rows(F("Values", "contains", int.MaxValue)));
        Assert.Equal(["Beta"], await Rows(F("Values", "eq", new[] { -7, 0, 7, int.MaxValue })));
        Assert.Equal(["Beta"], await Rows(F("$rowName", "ne", "Alpha"), F("Number", "lte", -1)));
        Assert.Equal(["Alpha", "Beta"], await Rows(F("$rowName", "contains", "A"), F("$rowName", "ne", "Defaults")));
        Assert.Empty(await Rows(F("Number", "eq", "305419896"))); // no string/number coercion
        Assert.Empty(await Rows(F("Number", "gt", 0), F("Number", "lt", 0)));

        // Missing cells compare like null.
        var defaultsHasMessage = HasColumn(table, "Defaults", "Message");
        Assert.Equal(defaultsHasMessage ? new[] { "Defaults" } : [], await Rows(F("Message", "eq", "")));
        Assert.Equal(defaultsHasMessage ? [] : new[] { "Defaults" }, await Rows(F("Message", "eq", null)));
        Assert.Equal(defaultsHasMessage ? new[] { "Alpha", "Beta", "Defaults" } : ["Alpha", "Beta"], await Rows(F("Message", "ne", null)));
    }

    [Fact]
    public async Task InvalidQueriesAreRejected()
    {
        using var fixture = new ParsedAssetFixture();
        var data = new DataOps(fixture.Games);
        var path = await FindAsync(fixture, "DT_AllProperties");

        Task Query(string[]? columns = null, TableFilter[]? filters = null, int offset = 0, int limit = 10) =>
            data.QueryTableAsync(Game, path, columns, filters, offset, limit, Ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Query(offset: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Query(limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Query(limit: DataOps.MaxQueryLimit + 1));
        Assert.Contains("Available columns", (await Assert.ThrowsAsync<ArgumentException>(() => Query(columns: ["Nope"]))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => Query(columns: [" "]));
        await Assert.ThrowsAsync<ArgumentException>(() => Query(filters: [F("Nope", "eq", 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => Query(filters: [F("Number", "like", 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => Query(filters: [F("Number", "gt", true)]));
        await Assert.ThrowsAsync<ArgumentException>(() => Query(filters: [F("Number", "lt", null)]));
        await Assert.ThrowsAsync<ArgumentException>(() => Query(filters: [F("Values", "contains", new[] { 1 })]));
        await Assert.ThrowsAsync<ArgumentException>(() => Query(filters: [new TableFilter("Number", "eq", default)]));
        await Assert.ThrowsAsync<ArgumentException>(() => Query(filters: [F("$rowName", "eq", 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => data.ExportTableAsync(Game, path, "xlsx", null, null, fixture.OutputDir, Ct));

        var dataAsset = await FindAsync(fixture, "DA_AllProperties");
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.QueryTableAsync(Game, dataAsset, null, null, 0, 10, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.ExportTableAsync(Game, dataAsset, "json", null, null, fixture.OutputDir, Ct));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => data.QueryTableAsync(Game, path, null, null, 0, 10, cancelled.Token));
    }

    [Fact]
    public async Task ExportJsonWritesTheCompleteInspectJson()
    {
        using var fixture = new ParsedAssetFixture();
        var data = new DataOps(fixture.Games);
        var path = await FindAsync(fixture, "DT_AllProperties");

        var report = await data.ExportJsonAsync(Game, path, fixture.OutputDir, Ct);
        var file = Assert.Single(report.RootFiles);
        Assert.Empty(report.DependencyFiles);
        Assert.Empty(report.Failures);
        Assert.Equal(Path.GetFullPath(fixture.OutputDir), report.OutputDir);
        Assert.Equal(ExpectedFile(fixture.OutputDir, path, "json"), file);
        var bytes = await File.ReadAllBytesAsync(file, Ct);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "JSON is UTF-8 without BOM");

        string inspected;
        using (var lease = await fixture.Games.AcquireAsync(Game, Ct))
            inspected = lease.Session.ToJson(path, int.MaxValue);
        var exported = JsonNode.Parse(bytes);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(inspected), exported));
        var tableJson = Assert.Single(exported!.AsArray(), e => e?["Type"]?.GetValue<string>() == "DataTable");
        Assert.Equal(["Alpha", "Beta", "Defaults"], tableJson!["Rows"]!.AsObject().Select(p => p.Key).Order());

        // An explicit object exports that object, through a borrowed lease.
        using (var lease = await fixture.Games.AcquireAsync(Game, Ct))
        {
            var single = await data.ExportJsonAsync(lease, path + ".DT_AllProperties", null, Ct);
            var singleFile = Assert.Single(single.RootFiles);
            Assert.Equal(ExpectedFile(Path.Combine(fixture.OutputDir, Game), path + ".DT_AllProperties", "json"), singleFile);
            var obj = JsonNode.Parse(await File.ReadAllBytesAsync(singleFile, Ct))!.AsObject();
            Assert.True(JsonNode.DeepEquals(tableJson, obj));
            await Assert.ThrowsAsync<InvalidDataException>(() => data.ExportJsonAsync(lease, path + ".Missing", null, Ct));
        }
    }

    [Fact]
    public async Task ExportTableJsonContainsEveryMatchingRow()
    {
        using var fixture = new ParsedAssetFixture();
        var data = new DataOps(fixture.Games);
        var path = await FindAsync(fixture, "DT_AllProperties");
        TableFilter[] filters = [F("$rowName", "ne", "Defaults")];

        var query = await data.QueryTableAsync(Game, path, null, filters, 0, DataOps.MaxQueryLimit, Ct);
        using var lease = await fixture.Games.AcquireAsync(Game, Ct);
        var report = await data.ExportTableAsync(lease, path, "JSON", null, filters, fixture.OutputDir, Ct);
        var file = Assert.Single(report.RootFiles);
        Assert.Equal(ExpectedFile(fixture.OutputDir, path, "table.json"), file);

        using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(file, Ct));
        var root = doc.RootElement;
        Assert.Equal(query.Path, root.GetProperty("table").GetString());
        Assert.Equal(3, root.GetProperty("totalRows").GetInt32());
        Assert.Equal(2, root.GetProperty("matchedRows").GetInt32());
        Assert.Equal(query.SelectedColumns, root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("$rowName", root.GetProperty("filters")[0].GetProperty("column").GetString());

        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(query.Rows.Select(r => r.RowName), rows.Select(r => r.GetProperty("rowName").GetString()));
        foreach (var (expected, actual) in query.Rows.Zip(rows))
        {
            var values = actual.GetProperty("values");
            Assert.Equal(expected.Values.Keys, values.EnumerateObject().Select(p => p.Name));
            foreach (var (column, value) in expected.Values)
                Assert.True(TableFilters.JsonEquals(value, values.GetProperty(column)), column);
            Assert.Equal(JsonValueKind.Object, values.GetProperty("Nested").ValueKind);
        }

        // Paging never limits an export.
        var paged = await data.QueryTableAsync(Game, path, null, filters, 0, 1, Ct);
        Assert.True(paged.HasMore);
        Assert.Equal(2, rows.Length);
    }

    [Fact]
    public async Task ExportTableCsvIsValidAndRejectsNestedColumns()
    {
        using var fixture = new ParsedAssetFixture();
        var data = new DataOps(fixture.Games);
        var path = await FindAsync(fixture, "DT_AllProperties");
        var table = await LoadTableAsync(fixture, path);

        var report = await data.ExportTableAsync(Game, path, "csv", ["Message", "Number", "LargeNumber", "Kind"], null, fixture.OutputDir, Ct);
        var file = Assert.Single(report.RootFiles);
        Assert.Equal(ExpectedFile(fixture.OutputDir, path, "table.csv"), file);
        var bytes = await File.ReadAllBytesAsync(file, Ct);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "CSV starts with a UTF-8 BOM");
        var records = ParseCsv(new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3));

        Assert.Equal(["$rowName", "Message", "Number", "LargeNumber", "Kind"], records[0].Select(f => f.Value));
        Assert.Equal(table.RowMap.Keys.Select(k => k.Text), records.Skip(1).Select(r => r[0].Value));
        var byName = records.Skip(1).ToDictionary(r => r[0].Value);

        var beta = byName["Beta"];
        Assert.Equal(("Beta_ß_水_🚀", false), beta[1]);
        Assert.Equal(("-202020", false), beta[2]); // numbers are never formula-neutralized
        Assert.Equal(("-9007199254740991", false), beta[3]);
        Assert.EndsWith("Beta", beta[4].Value, StringComparison.Ordinal);
        Assert.Equal(("305419896", false), byName["Alpha"][2]);
        Assert.Equal(("1234605616436508552", false), byName["Alpha"][3]);
        // Empty string is quoted, a missing cell is an empty unquoted field.
        Assert.Equal(("", HasColumn(table, "Defaults", "Message")), byName["Defaults"][1]);

        var rejectedDir = Path.Combine(fixture.OutputDir, "rejected");
        var nested = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            data.ExportTableAsync(Game, path, "csv", ["Message", "Nested"], null, rejectedDir, Ct));
        Assert.Contains("'Nested'", nested.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            data.ExportTableAsync(Game, path, "csv", ["Values"], [F("$rowName", "eq", "Alpha")], rejectedDir, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            data.ExportTableAsync(Game, path, "csv", null, null, rejectedDir, Ct));
        Assert.False(Directory.Exists(rejectedDir) && Directory.EnumerateFiles(rejectedDir, "*", SearchOption.AllDirectories).Any(),
            "a rejected CSV leaves no output or temporary file");
    }

    [Fact]
    public void CsvFieldsRoundTripAndNeutralizeFormulas()
    {
        string[] texts = ["plain", "a,b", "say \"hi\"", "line1\r\nline2", "lf\nonly", "=SUM(A1:A2)", "+1", "-x", "@cmd", "\tTab", "\rCR", "safe=1", "'quoted", ""];
        var line = string.Join(",", texts.Select(TableCsv.FormatText)) + TableCsv.NewLine;
        var parsed = Assert.Single(ParseCsv(line));
        string[] expected = ["plain", "a,b", "say \"hi\"", "line1\r\nline2", "lf\nonly", "'=SUM(A1:A2)", "'+1", "'-x", "'@cmd", "'\tTab", "'\rCR", "safe=1", "'quoted", ""];
        Assert.Equal(expected, parsed.Select(f => f.Value));
        Assert.Equal(("", true), parsed[^1]);

        Assert.Equal("", TableCsv.FormatCell(null));
        Assert.Equal("", TableCsv.FormatCell(JsonSerializer.SerializeToElement<object?>(null)));
        Assert.Equal("-7", TableCsv.FormatCell(JsonSerializer.SerializeToElement(-7)));
        Assert.Equal("1.25", TableCsv.FormatCell(JsonDocument.Parse("1.25").RootElement));
        Assert.Equal("1E+300", TableCsv.FormatCell(JsonDocument.Parse("1E+300").RootElement));
        Assert.Equal("true", TableCsv.FormatCell(JsonSerializer.SerializeToElement(true)));
        Assert.Equal("'-7", TableCsv.FormatCell(JsonSerializer.SerializeToElement("-7")));
        Assert.Throws<InvalidOperationException>(() => TableCsv.FormatCell(JsonDocument.Parse("[1]").RootElement));
        Assert.Throws<InvalidOperationException>(() => TableCsv.FormatCell(JsonDocument.Parse("{\"a\":1}").RootElement));
    }

    [Fact]
    public void FilterComparisonsAreTypedAndExplicit()
    {
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
        static bool E(string op, string? cell, string value) => TableFilters.Evaluate(op, cell is null ? null : J(cell), J(value));

        Assert.True(E("eq", "1", "1.0"));
        Assert.False(E("eq", "9007199254740993", "9007199254740992")); // beyond double precision
        Assert.True(E("gt", "9007199254740993", "9007199254740992"));
        Assert.True(E("lt", "-1e400", "0")); // outside decimal and double range
        Assert.False(E("eq", "5", "\"5\""));
        Assert.True(E("ne", "5", "\"5\""));
        Assert.False(E("gt", "\"b\"", "1"));
        Assert.True(E("gt", "\"b\"", "\"a\""));
        Assert.False(E("eq", "\"Abc\"", "\"abc\""));
        Assert.True(E("contains", "\"Hello World\"", "\"WORLD\""));
        Assert.False(E("contains", "5", "\"5\""));
        Assert.True(E("eq", "true", "true"));
        Assert.False(E("eq", "true", "false"));

        // Null and missing cells.
        foreach (var cell in new[] { null, "null" })
        {
            Assert.True(E("eq", cell, "null"));
            Assert.False(E("ne", cell, "null"));
            Assert.False(E("eq", cell, "0"));
            Assert.True(E("ne", cell, "0"));
            Assert.False(E("contains", cell, "\"x\""));
            Assert.False(E("gte", cell, "0"));
        }
        Assert.True(E("ne", "0", "null"));

        // Nested cells.
        Assert.True(E("eq", "{\"a\":1,\"b\":[1,{\"c\":null}]}", "{\"b\":[1.0,{\"c\":null}],\"a\":1}"));
        Assert.False(E("eq", "{\"a\":1}", "{\"a\":1,\"b\":2}"));
        Assert.False(E("eq", "[1,2]", "[2,1]"));
        Assert.True(E("contains", "[\"x\",2]", "2"));
        Assert.False(E("contains", "[\"X\"]", "\"x\""));
        Assert.False(E("gt", "[5]", "1"));
        Assert.False(E("gt", "{\"a\":5}", "1"));

        Assert.Throws<ArgumentException>(() => E("between", "1", "1"));
        Assert.Throws<ArgumentException>(() => E("gt", "1", "true"));
        Assert.Throws<ArgumentException>(() => E("contains", "[1]", "[1]"));
        Assert.Throws<ArgumentException>(() => TableFilters.Evaluate("eq", J("1"), default));
    }

    [Fact]
    public async Task DeserializationFailureCannotReplaceAnExistingCompleteJsonExport()
    {
        using var fixture = new ParsedAssetFixture();
        var path = await FindAsync(fixture, "DT_AllProperties");
        var outputDir = Path.Combine(fixture.OutputDir, "preserved");
        var good = await new DataOps(fixture.Games).ExportJsonAsync(Game, path, outputDir, Ct);
        var destination = Assert.Single(good.RootFiles);
        var original = await File.ReadAllBytesAsync(destination, Ct);
        using var sourceLease = await fixture.Games.AcquireAsync(Game, Ct);
        var package = Assert.IsType<Package>(sourceLease.Session.LoadPackage(path));
        var firstPayload = package.ExportMap.Where(export => export.SerialSize > 0).Min(export => export.SerialOffset);
        var files = sourceLease.Session.Provider.SavePackage(path);
        var damaged = Path.Combine(fixture.OutputDir, "damaged-packages");
        foreach (var (name, bytes) in files)
        {
            var file = ExportFiles.WithinDirectory(damaged, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var extension = Path.GetExtension(name);
            var content = extension.Equals(".uexp", StringComparison.OrdinalIgnoreCase) ? bytes.AsMemory(0, 1)
                : extension.Equals(".uasset", StringComparison.OrdinalIgnoreCase) && firstPayload < bytes.Length
                    ? bytes.AsMemory(0, checked((int)firstPayload)) : bytes.AsMemory();
            await File.WriteAllBytesAsync(file, content, Ct);
        }
        var config = new AppConfig
        {
            CacheDir = fixture.Config.CacheDir,
            OodlePath = fixture.Config.OodlePath,
            ZlibPath = fixture.Config.ZlibPath,
            Games = new(StringComparer.OrdinalIgnoreCase)
            {
                ["damaged"] = new GameConfig { PaksDir = Path.Combine(damaged, path.Split('/')[0]), UeVersion = "GAME_UE5_8" }
            }
        };
        using var games = new GameManager(config);
        await Assert.ThrowsAsync<ParserException>(() => new DataOps(games).ExportJsonAsync("damaged", path, outputDir, Ct));
        Assert.Equal(original, await File.ReadAllBytesAsync(destination, Ct));
        Assert.Empty(Directory.EnumerateFiles(outputDir, "*.tmp", SearchOption.AllDirectories));
    }

    private static string ExpectedFile(string outputDir, string input, string extension) =>
        Path.GetFullPath(Path.Combine(outputDir, input + "." + extension));

    /// <summary>Strict RFC 4180 reader with CRLF records; reports whether each field was quoted.</summary>
    private static List<List<(string Value, bool Quoted)>> ParseCsv(string text)
    {
        var records = new List<List<(string Value, bool Quoted)>>();
        var record = new List<(string Value, bool Quoted)>();
        var field = new StringBuilder();
        bool quoted = false, inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else inQuotes = false;
            }
            else if (c == '"')
            {
                Assert.False(quoted || field.Length > 0, "quote inside an unquoted field");
                inQuotes = quoted = true;
            }
            else if (c == ',')
            {
                record.Add((field.ToString(), quoted));
                field.Clear();
                quoted = false;
            }
            else if (c == '\r')
            {
                Assert.True(i + 1 < text.Length && text[i + 1] == '\n', "records end with CRLF");
                i++;
                record.Add((field.ToString(), quoted));
                records.Add(record);
                record = [];
                field.Clear();
                quoted = false;
            }
            else
            {
                Assert.False(quoted, "text after a closing quote");
                Assert.NotEqual('\n', c);
                field.Append(c);
            }
        }
        Assert.False(inQuotes, "unterminated quote");
        Assert.True(record.Count == 0 && field.Length == 0, "last record ends with CRLF");
        return records;
    }
}
