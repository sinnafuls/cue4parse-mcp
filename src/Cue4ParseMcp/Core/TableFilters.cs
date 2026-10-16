using System.Globalization;
using System.Text.Json;

namespace Cue4ParseMcp.Core;

/// <summary>
/// One DataTable row condition. <see cref="Column"/> is a top-level row column or <c>$rowName</c> for the row key;
/// <see cref="Op"/> is eq, ne, contains, gt, gte, lt or lte. All filters of a query must match (AND).
/// </summary>
public sealed record TableFilter(string Column, string Op, JsonElement Value);

internal enum FilterOp { Eq, Ne, Contains, Gt, Gte, Lt, Lte }

/// <summary>
/// Typed, deterministic comparison of a serialized row cell against a filter value.
/// <list type="bullet">
/// <item>No cross-type coercion: the number 5 never equals the string "5".</item>
/// <item>Numbers compare by value (1 == 1.0), exactly as decimals when both fit, otherwise as doubles.</item>
/// <item>Strings: eq/ne and ordering are ordinal (case-sensitive); contains is an ordinal case-insensitive substring test.</item>
/// <item>Booleans support eq/ne only.</item>
/// <item>A null or missing cell: eq null matches, ne null does not; eq with a non-null value never matches, ne does.
/// contains and ordering never match.</item>
/// <item>Object/array cells: eq/ne use deep JSON equality (object key order ignored, numbers by value);
/// contains on an array matches if any element equals the value; ordering never matches.</item>
/// </list>
/// </summary>
public static class TableFilters
{
    public const string RowNameColumn = "$rowName";

    public static IReadOnlyList<string> Operators { get; } = ["eq", "ne", "contains", "gt", "gte", "lt", "lte"];

    /// <summary>Validates <paramref name="op"/> and <paramref name="value"/>, then compares; <paramref name="cell"/> null means the row lacks the column.</summary>
    public static bool Evaluate(string op, JsonElement? cell, JsonElement value)
    {
        var parsed = ParseOp(op);
        ValidateValue(parsed, value, $"Filter '{op}'");
        return Evaluate(parsed, cell, value);
    }

    internal static FilterOp ParseOp(string? op) => op?.Trim().ToLowerInvariant() switch
    {
        "eq" => FilterOp.Eq,
        "ne" => FilterOp.Ne,
        "contains" => FilterOp.Contains,
        "gt" => FilterOp.Gt,
        "gte" => FilterOp.Gte,
        "lt" => FilterOp.Lt,
        "lte" => FilterOp.Lte,
        _ => throw new ArgumentException($"Unsupported filter op '{op}'. Use one of: {string.Join(", ", Operators)}.")
    };

    internal static void ValidateValue(FilterOp op, JsonElement value, string subject)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException($"{subject} needs a value (use JSON null to compare against null).");
        switch (op)
        {
            case FilterOp.Contains when value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False):
                throw new ArgumentException($"{subject}: contains needs a string, number or boolean value, not {Describe(value.ValueKind)}.");
            case FilterOp.Gt or FilterOp.Gte or FilterOp.Lt or FilterOp.Lte when value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String):
                throw new ArgumentException($"{subject}: ordering operators need a number or string value, not {Describe(value.ValueKind)}.");
        }
    }

    internal static bool Evaluate(FilterOp op, JsonElement? cell, JsonElement value)
    {
        if (cell is not { ValueKind: not JsonValueKind.Null } present)
            return op switch
            {
                FilterOp.Eq => value.ValueKind == JsonValueKind.Null,
                FilterOp.Ne => value.ValueKind != JsonValueKind.Null,
                _ => false
            };

        switch (op)
        {
            case FilterOp.Eq:
                return JsonEquals(present, value);
            case FilterOp.Ne:
                return !JsonEquals(present, value);
            case FilterOp.Contains:
                if (present.ValueKind == JsonValueKind.String)
                    return value.ValueKind == JsonValueKind.String
                           && present.GetString()!.Contains(value.GetString()!, StringComparison.OrdinalIgnoreCase);
                if (present.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in present.EnumerateArray())
                        if (JsonEquals(item, value)) return true;
                }
                return false;
            default:
                int order;
                if (present.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number)
                    order = CompareNumbers(present, value);
                else if (present.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String)
                    order = string.CompareOrdinal(present.GetString(), value.GetString());
                else
                    return false;
                return op switch
                {
                    FilterOp.Gt => order > 0,
                    FilterOp.Gte => order >= 0,
                    FilterOp.Lt => order < 0,
                    _ => order <= 0
                };
        }
    }

    /// <summary>Deep JSON equality: same kinds, numbers by value, ordinal strings, objects regardless of key order.</summary>
    public static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Number:
                return CompareNumbers(a, b) == 0;
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                var left = a.EnumerateArray();
                var right = b.EnumerateArray();
                while (left.MoveNext() && right.MoveNext())
                    if (!JsonEquals(left.Current, right.Current)) return false;
                return true;
            case JsonValueKind.Object:
                var leftProperties = Properties(a);
                var rightProperties = Properties(b);
                return leftProperties.Count == rightProperties.Count
                       && leftProperties.All(p => rightProperties.TryGetValue(p.Key, out var other) && JsonEquals(p.Value, other));
            default: // true, false, null
                return true;
        }
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement obj)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject()) properties[property.Name] = property.Value; // last duplicate wins
        return properties;
    }

    private static int CompareNumbers(JsonElement a, JsonElement b)
    {
        if (a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y)) return x.CompareTo(y);
        return ToDouble(a).CompareTo(ToDouble(b));
    }

    // Out-of-range literals parse to +/-Infinity, which still orders correctly.
    private static double ToDouble(JsonElement number) => number.TryGetDouble(out var d)
        ? d
        : double.Parse(number.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);

    internal static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Number => "a number",
        JsonValueKind.String => "a string",
        _ => "no value"
    };
}
