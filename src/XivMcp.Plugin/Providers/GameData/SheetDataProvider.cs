using System.Globalization;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Text.ReadOnly;
using XivMcp.Core;
using XivMcp.Plugin.Util;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>Generic, read-only access to any Excel sheet (typed Lumina sheets plus raw fallback).</summary>
[McpProvider("gamedata")]
public sealed class SheetDataProvider : IDisposable
{
    private readonly GameDataIndex index;

    public SheetDataProvider(IDataManager data) => index = GameDataIndex.For(data);

    /// <summary>Stops the shared index warmup thread and releases cached indexes on plugin unload.</summary>
    public void Dispose() => GameDataIndex.Release();

    [McpTool("list_sheets",
        Title = "List Excel sheets",
        Description =
            "Lists game Excel sheets that have typed column definitions (Lumina.Excel.Sheets), optionally filtered by nameContains, with row count, " +
            "whether rows have subrows, and column names with types (string, uint8..int64, float, bool, RowRef<Sheet> links, arrays). " +
            "Use it to discover sheet and column names for get_sheet_row and search_sheet when no dedicated tool exists. " +
            "includeUntyped also lists raw sheet names without column names (their columns read as col0, col1, ...). Paged; columns are included only " +
            "when includeColumns is true.",
        GameThread = false,
        RequiresLogin = false)]
    public PagedResult<SheetInfo> ListSheets(
        [McpParam("Case-insensitive substring of the sheet name, e.g. \"Item\", \"Mount\", \"Fish\".")] string? nameContains = null,
        [McpParam("Include column names and types for each returned sheet.")] bool includeColumns = true,
        [McpParam("Also list sheets without typed definitions (quest dialogue, custom sheets, ...).")] bool includeUntyped = false,
        [McpParam("Maximum results (1-500).", Minimum = 1, Maximum = 500)] int limit = 50,
        [McpParam("Results to skip for paging.", Minimum = 0)] int offset = 0)
    {
        var filter = nameContains?.Trim() ?? "";
        var typed = SheetJson.SheetTypes;
        var names = typed.Keys.AsEnumerable();
        if (includeUntyped)
            names = names.Concat(index.Module.SheetNames.Where(n => !typed.ContainsKey(n)));
        var matching = names
            .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => filter.Length > 0 && !n.Equals(filter, StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n.Length)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var page = Page<string>.From(matching, offset, limit);
        var results = page.Items.Select(name =>
        {
            var header = SheetJson.ReadHeader(index.GameData, name);
            if (!typed.TryGetValue(name, out var type))
                return new SheetInfo(name, header?.RowCount, header?.Subrows, null, false);
            var columns = includeColumns
                ? SheetJson.Columns(type).Select(p => $"{p.Name}: {SheetJson.DescribeType(p.PropertyType)}").ToList()
                : null;
            return new SheetInfo(name, header?.RowCount, header?.Subrows, columns, true);
        }).ToList();

        return new PagedResult<SheetInfo>(page.Total, page.Offset, results.Count, page.Truncated, results,
            page.Total == 0 ? "No sheet matches; try a shorter nameContains or includeUntyped=true." : null);
    }

    [McpTool("get_sheet_row",
        Title = "Read an Excel sheet row",
        Description =
            "Reads one row of any game Excel sheet by sheet name and row id and returns it as JSON: numbers/bools as values, text as plain strings, " +
            "RowRef links as {rowId, sheet, name} (name is the linked row's Name/Singular when it has one; links are not followed further), arrays as " +
            "JSON arrays and nested structs as objects. Subrow sheets take subrowId (default 0) and report subrowCount. " +
            "Sheets without typed definitions return columns as col0, col1, .... Use columns to return only some fields and includeUnknown=false " +
            "to drop Unknown* columns. Prefer dedicated tools (get_item, get_action, get_quest, get_duty, get_recipe) when they exist.",
        GameThread = false,
        RequiresLogin = false)]
    public SheetRowResult GetSheetRow(
        [McpParam("Sheet name, case-insensitive (e.g. \"Mount\", \"TerritoryType\", \"ClassJob\"). See list_sheets.")] string sheet,
        [McpParam("Row id.")] uint rowId,
        [McpParam("Subrow id for subrow sheets (default 0).")] ushort? subrowId = null,
        [McpParam("Only include these column names (case-insensitive).")] string[]? columns = null,
        [McpParam("Include columns named Unknown*.")] bool includeUnknown = true)
    {
        var options = new SheetJson.Options
        {
            IncludeUnknown = includeUnknown,
            Columns = columns is { Length: > 0 } ? new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase) : null,
        };

        if (SheetJson.FindSheetType(sheet) is { } type)
        {
            var name = SheetJson.SheetName(type);
            if (SheetJson.IsSubrowType(type))
            {
                var rows = SheetJson.GetSubrows(index.Module, index.Language, type, rowId)
                           ?? throw new McpToolException($"Row {rowId} not found in sheet {name}.");
                var sub = subrowId ?? 0;
                if (sub >= rows.Count) throw new McpToolException($"Row {rowId} of {name} has {rows.Count} subrows; subrowId {sub} is out of range.");
                return new SheetRowResult(name, rowId, sub, rows.Count, Bound(SheetJson.RowToJson(rows[sub], options)));
            }

            var row = SheetJson.GetRow(index.Module, index.Language, type, rowId)
                      ?? throw new McpToolException($"Row {rowId} not found in sheet {name}.");
            return new SheetRowResult(name, rowId, null, null, Bound(SheetJson.RowToJson(row, options)));
        }

        var raw = RawSheet(sheet);
        if (raw is RawSubrowExcelSheet subSheet)
        {
            var sub = subrowId ?? 0;
            if (!subSheet.TryGetSubrowCount(rowId, out var count)) throw new McpToolException($"Row {rowId} not found in sheet {sheet}.");
            if (sub >= count) throw new McpToolException($"Row {rowId} of {sheet} has {count} subrows; subrowId {sub} is out of range.");
            var typedSheet = new SubrowExcelSheet<RawSubrow>(subSheet);
            return new SheetRowResult(sheet, rowId, sub, count, Bound(SheetJson.RowToJson(typedSheet.GetSubrow(rowId, sub), options)));
        }

        var plain = new ExcelSheet<RawRow>(raw);
        if (!plain.TryGetRow(rowId, out var rawRow)) throw new McpToolException($"Row {rowId} not found in sheet {sheet}.");
        return new SheetRowResult(sheet, rowId, null, null, Bound(SheetJson.RowToJson(rawRow, options)));
    }

    [McpResourceTemplate("ffxiv://sheet/{sheet}/{rowId}",
        Name = "Excel sheet row",
        Description = "One Excel sheet row as JSON (same content as get_sheet_row with default options).",
        GameThread = false,
        RequiresLogin = false)]
    public SheetRowResult SheetRowResource(string sheet, uint rowId) => GetSheetRow(sheet, rowId);

    [McpTool("search_sheet",
        Title = "Search an Excel sheet column",
        Description =
            "Scans one column of any Excel sheet and returns matching rows as {rowId, subrowId, label, value}, where label is the row's Name/Singular " +
            "when it has one. Text columns match case-insensitively by substring (match=\"exact\" for whole value); numeric and bool columns match the " +
            "number/true/false exactly, or a range \"min..max\"; RowRef columns match the linked row id. Column names come from list_sheets " +
            "(untyped sheets use col0, col1, ...). Paged with total and truncated. Use get_sheet_row to read a full match.",
        GameThread = false,
        RequiresLogin = false)]
    public PagedResult<SheetMatch> SearchSheet(
        [McpParam("Sheet name, case-insensitive.")] string sheet,
        [McpParam("Column (property) name, case-insensitive, e.g. \"Name\", \"ClassJobLevel\", \"TerritoryType\".")] string column,
        [McpParam("Text to find, a number, true/false, or a numeric range like \"50..60\".")] string query,
        [McpParam("\"contains\" (default, text only) or \"exact\".", Enum = ["contains", "exact"])] string match = "contains",
        [McpParam("Maximum results (1-500).", Minimum = 1, Maximum = 500)] int limit = 25,
        [McpParam("Results to skip for paging.", Minimum = 0)] int offset = 0)
    {
        if (string.IsNullOrEmpty(query)) throw new McpToolException("query is required.");
        var exact = string.Equals(match, "exact", StringComparison.OrdinalIgnoreCase);
        var matcher = Matcher.Create(query, exact);
        var hits = new List<SheetMatch>();
        var total = 0;
        offset = TextSearch.ClampOffset(offset);
        limit = TextSearch.ClampLimit(limit);

        void Consider(Func<object?> read, Func<string?> label, uint rowId, ushort? subrowId)
        {
            object? value;
            try
            {
                value = read();
            }
            catch
            {
                return;
            }

            if (!matcher.IsMatch(value)) return;
            total++;
            if (total <= offset || hits.Count >= limit) return;
            hits.Add(new SheetMatch(rowId, subrowId, label(), SheetJson.ValueToJson(value, new SheetJson.Options { MaxCollectionItems = 16 })));
        }

        string sheetName;
        if (SheetJson.FindSheetType(sheet) is { } type)
        {
            sheetName = SheetJson.SheetName(type);
            var prop = SheetJson.Columns(type).FirstOrDefault(p => p.Name.Equals(column.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new McpToolException($"Sheet {sheetName} has no column \"{column}\". Columns: {string.Join(", ", SheetJson.Columns(type).Select(p => p.Name).Take(80))}");
            foreach (var row in SheetJson.EnumerateRows(index.Module, index.Language, type))
            {
                Consider(() => prop.GetValue(row), () => SheetJson.LabelOf(row), SheetJson.RowIdOf(row), SheetJson.SubrowIdOf(row));
            }
        }
        else
        {
            var raw = RawSheet(sheet);
            sheetName = sheet;
            var col = column.Trim().ToLowerInvariant();
            if (!col.StartsWith("col", StringComparison.Ordinal) || !int.TryParse(col[3..], out var colIndex) || colIndex < 0 || colIndex >= raw.Columns.Count)
                throw new McpToolException($"Sheet {sheet} has no typed columns; use col0..col{raw.Columns.Count - 1}.");
            if (raw is RawSubrowExcelSheet subSheet)
            {
                foreach (var rows in new SubrowExcelSheet<RawSubrow>(subSheet))
                foreach (var r in rows)
                    Consider(() => r.ReadColumn(colIndex), () => null, r.RowId, r.SubrowId);
            }
            else
            {
                foreach (var r in new ExcelSheet<RawRow>(raw))
                    Consider(() => r.ReadColumn(colIndex), () => null, r.RowId, null);
            }
        }

        return new PagedResult<SheetMatch>(total, offset, hits.Count, offset + hits.Count < total, hits,
            total == 0 ? $"No rows in {sheetName} where {column} matches \"{query}\"." : null);
    }

    private const int MaxRowChars = 30_000;

    /// <summary>Keeps a row under <see cref="MaxRowChars"/> by replacing the largest columns with a placeholder.</summary>
    private static JsonObject Bound(JsonObject row)
    {
        var size = row.ToJsonString().Length;
        while (size > MaxRowChars)
        {
            var largest = row
                .Where(kv => kv.Value is JsonArray or JsonObject)
                .Select(kv => (kv.Key, Length: kv.Value!.ToJsonString().Length))
                .OrderByDescending(kv => kv.Length)
                .FirstOrDefault();
            if (largest.Key == null || largest.Length < 64) break;
            row[largest.Key] = $"<omitted {largest.Length} chars: request with columns=[\"{largest.Key}\"]>";
            size = row.ToJsonString().Length;
        }

        return row;
    }

    private RawExcelSheet RawSheet(string sheet)
    {
        var name = index.Module.SheetNames.FirstOrDefault(n => n.Equals(sheet.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new McpToolException($"Sheet \"{sheet}\" not found. Use list_sheets to find sheet names.");
        try
        {
            return index.Module.GetRawSheet(name, index.Language);
        }
        catch (Exception ex)
        {
            throw new McpToolException($"Sheet \"{name}\" could not be loaded: {ex.Message}");
        }
    }

    private sealed class Matcher
    {
        private readonly string text;
        private readonly bool exact;
        private readonly double? number;
        private readonly (double Min, double Max)? range;
        private readonly bool? boolean;

        private Matcher(string text, bool exact)
        {
            this.text = text;
            this.exact = exact;
            var trimmed = text.Trim();
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) number = n;
            var dots = trimmed.IndexOf("..", StringComparison.Ordinal);
            if (dots > 0 &&
                double.TryParse(trimmed[..dots], NumberStyles.Float, CultureInfo.InvariantCulture, out var min) &&
                double.TryParse(trimmed[(dots + 2)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var max))
                range = (min, max);
            if (bool.TryParse(trimmed, out var b)) boolean = b;
        }

        public static Matcher Create(string text, bool exact) => new(text, exact);

        public bool IsMatch(object? value)
        {
            switch (value)
            {
                case null:
                    return false;
                case ReadOnlySeString s:
                    return MatchText(SheetJson.Text(s));
                case string s:
                    return MatchText(s);
                case bool b:
                    return boolean == b;
                case RowRef r:
                    return MatchNumber(r.RowId);
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double:
                    return MatchNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }

            var type = value.GetType();
            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                if (def == typeof(RowRef<>) || def == typeof(SubrowRef<>))
                    return MatchNumber((uint)type.GetProperty("RowId")!.GetValue(value)!);
                if (def == typeof(Collection<>))
                {
                    foreach (var item in (System.Collections.IEnumerable)value)
                    {
                        if (IsMatch(item)) return true;
                    }
                }
            }

            return false;
        }

        private bool MatchText(string value) =>
            exact ? value.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase) : value.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase);

        private bool MatchNumber(double value)
        {
            if (range is { } r) return value >= r.Min && value <= r.Max;
            return number is { } n && Math.Abs(value - n) < 1e-6;
        }
    }
}
