using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Cue4ParseMcp.Core;

/// <summary>
/// CSV policy for DataTable exports: UTF-8 with a BOM (so spreadsheet apps decode non-ASCII text), CRLF record
/// separators and RFC 4180 quoting. A field is quoted when it contains a comma, quote, CR or LF, or is an empty
/// string; embedded quotes are doubled. Null and missing cells are empty unquoted fields, so they stay
/// distinguishable from empty strings (<c>""</c>). Numbers are written as their invariant JSON text, booleans as
/// true/false. Text fields (strings, row names, column names) starting with = + - @ TAB CR or LF get a leading
/// apostrophe so spreadsheets show them as text instead of evaluating a formula; numeric cells are never altered.
/// Objects and arrays are not representable and are rejected.
/// </summary>
public static class TableCsv
{
    public static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public const string NewLine = "\r\n";

    private static readonly SearchValues<char> FormulaStarts = SearchValues.Create("=+-@\t\r\n");
    private static readonly SearchValues<char> NeedsQuotes = SearchValues.Create(",\"\r\n");

    /// <summary>Escapes one text field, neutralizing a leading formula character.</summary>
    public static string FormatText(string text)
    {
        if (text.Length == 0) return "\"\"";
        if (FormulaStarts.Contains(text[0])) text = "'" + text;
        return text.AsSpan().ContainsAny(NeedsQuotes) ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    /// <summary>Formats a scalar cell; <paramref name="cell"/> null means the row lacks the column.</summary>
    /// <exception cref="InvalidOperationException">The cell is an object or array.</exception>
    public static string FormatCell(JsonElement? cell) => cell is not { } value ? "" : value.ValueKind switch
    {
        JsonValueKind.Null => "",
        JsonValueKind.String => FormatText(value.GetString()!),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new InvalidOperationException($"CSV cells must be scalar, not {TableFilters.Describe(value.ValueKind)}.")
    };
}
