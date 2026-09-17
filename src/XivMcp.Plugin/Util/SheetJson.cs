using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json.Nodes;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files.Excel;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace XivMcp.Plugin.Util;

/// <summary>
/// Generic, reflection-based access to Lumina Excel sheets: sheet-name registry over the generated
/// <c>Lumina.Excel.Sheets</c> row structs, row lookup by name, and row → JSON conversion that is
/// depth-limited and cycle-safe (references are summarized, never followed recursively).
/// Thread-safe; never touches game memory.
/// </summary>
public static class SheetJson
{
    private const string SheetsNamespace = "Lumina.Excel.Sheets";

    private static readonly Lazy<FrozenDictionary<string, Type>> SheetTypeMap = new(BuildSheetTypeMap);
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();
    private static readonly ConcurrentDictionary<Type, Func<ExcelModule, Language, uint, object?>> RowGetters = new();
    private static readonly ConcurrentDictionary<Type, Func<ExcelModule, Language, uint, IReadOnlyList<object>?>> SubrowGetters = new();
    private static readonly ConcurrentDictionary<Type, Func<ExcelModule, Language, IEnumerable<object>>> Enumerators = new();

    /// <summary>Name of the property used as a human label, in preference order.</summary>
    private static readonly string[] LabelProperties = ["Name", "Singular", "Text", "Description"];

    /// <summary>Every typed sheet (row struct in Lumina.Excel.Sheets), keyed case-insensitively by sheet name.</summary>
    public static FrozenDictionary<string, Type> SheetTypes => SheetTypeMap.Value;

    public static bool IsSubrowType(Type rowType) =>
        rowType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IExcelSubrow<>));

    /// <summary>Sheet name for a row type (from its [Sheet] attribute, falling back to the type name).</summary>
    public static string SheetName(Type rowType) => rowType.GetCustomAttribute<SheetAttribute>()?.Name ?? rowType.Name;

    public static Type? FindSheetType(string sheet) =>
        SheetTypes.TryGetValue(sheet.Trim(), out var type) ? type : null;

    /// <summary>Public column-like properties of a row struct (excludes RowId/SubrowId/page plumbing).</summary>
    public static PropertyInfo[] Columns(Type rowType) => PropertyCache.GetOrAdd(rowType, static t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                        && p.Name is not ("ExcelPage" or "RowOffset" or "RowId" or "SubrowId")
                        && p.PropertyType != typeof(ExcelPage))
            .ToArray());

    public static string DescribeType(Type type)
    {
        if (type == typeof(ReadOnlySeString)) return "string";
        if (type == typeof(RowRef)) return "RowRef";
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var arg = type.GetGenericArguments()[0];
            if (def == typeof(RowRef<>)) return $"RowRef<{SheetName(arg)}>";
            if (def == typeof(SubrowRef<>)) return $"SubrowRef<{SheetName(arg)}>";
            if (def == typeof(Collection<>)) return $"{DescribeType(arg)}[]";
        }

        return type.Name switch
        {
            "Boolean" => "bool",
            "SByte" => "int8",
            "Byte" => "uint8",
            "Int16" => "int16",
            "UInt16" => "uint16",
            "Int32" => "int32",
            "UInt32" => "uint32",
            "Int64" => "int64",
            "UInt64" => "uint64",
            "Single" => "float",
            _ => type.Name,
        };
    }

    /// <summary>Header-only metadata (cheap: does not load the sheet's data pages).</summary>
    public static (uint RowCount, bool Subrows, int ColumnCount)? ReadHeader(GameData gameData, string sheet)
    {
        try
        {
            var header = gameData.GetFile<ExcelHeaderFile>($"exd/{sheet}.exh");
            if (header == null) return null;
            return (header.Header.RowCount, header.Header.Variant == ExcelVariant.Subrows, header.ColumnDefinitions.Length);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A typed row, or null when absent. For subrow sheets returns subrow <paramref name="subrowId"/> (default 0).</summary>
    public static object? GetRow(ExcelModule module, Language language, Type rowType, uint rowId, ushort? subrowId = null)
    {
        if (IsSubrowType(rowType))
        {
            var rows = GetSubrows(module, language, rowType, rowId);
            if (rows == null) return null;
            var index = subrowId ?? 0;
            return index < rows.Count ? rows[index] : null;
        }

        return RowGetters.GetOrAdd(rowType, static t =>
        {
            var method = typeof(SheetJson).GetMethod(nameof(GetRowTyped), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(t);
            return method.CreateDelegate<Func<ExcelModule, Language, uint, object?>>();
        })(module, language, rowId);
    }

    /// <summary>All subrows of a row in a subrow sheet, or null when the row is absent.</summary>
    public static IReadOnlyList<object>? GetSubrows(ExcelModule module, Language language, Type rowType, uint rowId) =>
        SubrowGetters.GetOrAdd(rowType, static t =>
        {
            var method = typeof(SheetJson).GetMethod(nameof(GetSubrowsTyped), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(t);
            return method.CreateDelegate<Func<ExcelModule, Language, uint, IReadOnlyList<object>?>>();
        })(module, language, rowId);

    /// <summary>Every row of a sheet (subrow sheets are flattened), boxed.</summary>
    public static IEnumerable<object> EnumerateRows(ExcelModule module, Language language, Type rowType) =>
        Enumerators.GetOrAdd(rowType, static t =>
        {
            var name = IsSubrowType(t) ? nameof(EnumerateSubrowsTyped) : nameof(EnumerateRowsTyped);
            var method = typeof(SheetJson).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(t);
            return method.CreateDelegate<Func<ExcelModule, Language, IEnumerable<object>>>();
        })(module, language);

    private static object? GetRowTyped<T>(ExcelModule module, Language language, uint rowId) where T : struct, IExcelRow<T>
    {
        var sheet = module.GetSheet<T>(language);
        return sheet.TryGetRow(rowId, out var row) ? row : null;
    }

    private static IReadOnlyList<object>? GetSubrowsTyped<T>(ExcelModule module, Language language, uint rowId) where T : struct, IExcelSubrow<T>
    {
        var sheet = module.GetSubrowSheet<T>(language);
        if (!sheet.TryGetRow(rowId, out var rows)) return null;
        var list = new List<object>(rows.Count);
        foreach (var r in rows) list.Add(r);
        return list;
    }

    private static IEnumerable<object> EnumerateRowsTyped<T>(ExcelModule module, Language language) where T : struct, IExcelRow<T>
    {
        foreach (var row in module.GetSheet<T>(language)) yield return row;
    }

    private static IEnumerable<object> EnumerateSubrowsTyped<T>(ExcelModule module, Language language) where T : struct, IExcelSubrow<T>
    {
        foreach (var rows in module.GetSubrowSheet<T>(language))
        foreach (var row in rows)
            yield return row;
    }

    public static uint RowIdOf(object row) => row switch
    {
        RawRow r => r.RowId,
        RawSubrow s => s.RowId,
        _ => (uint)(row.GetType().GetProperty("RowId")?.GetValue(row) ?? 0u),
    };

    public static ushort? SubrowIdOf(object row) => row switch
    {
        RawSubrow s => s.SubrowId,
        _ => row.GetType().GetProperty("SubrowId")?.GetValue(row) as ushort?,
    };

    /// <summary>Best human label of a typed row (Name, Singular, ...), or null.</summary>
    public static string? LabelOf(object row)
    {
        var type = row.GetType();
        foreach (var name in LabelProperties)
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop?.PropertyType != typeof(ReadOnlySeString)) continue;
            try
            {
                var text = Text((ReadOnlySeString)prop.GetValue(row)!);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch
            {
                // Unreadable column: try the next candidate.
            }
        }

        return null;
    }

    /// <summary>Plain text of a SeString (macros/payloads removed, soft hyphens stripped).</summary>
    public static string Text(ReadOnlySeString value)
    {
        if (value.IsEmpty) return "";
        return value.ExtractText().Replace("­", "", StringComparison.Ordinal);
    }

    /// <summary>Options for <see cref="RowToJson"/>.</summary>
    public sealed class Options
    {
        /// <summary>Emit columns named Unknown*.</summary>
        public bool IncludeUnknown { get; init; } = true;

        /// <summary>Resolve RowRef targets to include the referenced row's label.</summary>
        public bool ResolveReferences { get; init; } = true;

        /// <summary>Maximum nesting for inline structs / collections of structs.</summary>
        public int MaxDepth { get; init; } = 5;

        /// <summary>Only emit these columns (case-insensitive), when non-null.</summary>
        public IReadOnlySet<string>? Columns { get; init; }

        /// <summary>Upper bound on array elements emitted per collection.</summary>
        public int MaxCollectionItems { get; init; } = 64;
    }

    /// <summary>
    /// Converts a typed row (or <see cref="RawRow"/>/<see cref="RawSubrow"/>) into a JSON object:
    /// primitives as JSON values, <see cref="ReadOnlySeString"/> as text, RowRef as
    /// <c>{ rowId, sheet, name? }</c>, Collection&lt;T&gt; as arrays, nested structs as objects.
    /// </summary>
    public static JsonObject RowToJson(object row, Options? options = null)
    {
        options ??= new Options();
        var obj = new JsonObject { ["rowId"] = RowIdOf(row) };
        var subrow = SubrowIdOf(row);
        if (subrow.HasValue) obj["subrowId"] = subrow.Value;

        if (row is RawRow raw)
        {
            WriteRawColumns(obj, raw.Columns.Count, raw.ReadColumn, options);
            return obj;
        }

        if (row is RawSubrow rawSub)
        {
            WriteRawColumns(obj, rawSub.Columns.Count, rawSub.ReadColumn, options);
            return obj;
        }

        WriteProperties(obj, row, options, 0);
        return obj;
    }

    private static void WriteRawColumns(JsonObject obj, int count, Func<int, object> read, Options options)
    {
        for (var i = 0; i < count; i++)
        {
            var key = $"col{i}";
            if (options.Columns != null && !options.Columns.Contains(key)) continue;
            try
            {
                obj[key] = ValueToJson(read(i), options, 0);
            }
            catch (Exception ex)
            {
                obj[key] = $"<error: {ex.GetType().Name}>";
            }
        }
    }

    private static void WriteProperties(JsonObject obj, object value, Options options, int depth)
    {
        foreach (var prop in Columns(value.GetType()))
        {
            if (!options.IncludeUnknown && prop.Name.StartsWith("Unknown", StringComparison.Ordinal)) continue;
            if (depth == 0 && options.Columns != null && !options.Columns.Contains(prop.Name)) continue;
            var key = JsonName(prop.Name);
            try
            {
                obj[key] = ValueToJson(prop.GetValue(value), options, depth);
            }
            catch (Exception ex)
            {
                obj[key] = $"<error: {(ex is TargetInvocationException { InnerException: { } inner } ? inner.GetType().Name : ex.GetType().Name)}>";
            }
        }
    }

    public static string JsonName(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>Converts a single column value to JSON (see <see cref="RowToJson"/>).</summary>
    public static JsonNode? ValueToJson(object? value, Options? options = null, int depth = 0)
    {
        options ??= new Options();
        switch (value)
        {
            case null:
                return null;
            case ReadOnlySeString s:
                return Text(s);
            case string s:
                return s;
            case bool b:
                return b;
            case sbyte or byte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value));
            case ulong ul:
                return ul <= long.MaxValue ? JsonValue.Create((long)ul) : JsonValue.Create(ul.ToString());
            case float f:
                return float.IsFinite(f) ? JsonValue.Create(Math.Round(f, 4)) : null;
            case double d:
                return double.IsFinite(d) ? JsonValue.Create(d) : null;
            case RowRef untyped:
                return RefToJson(untyped.RowId, untyped.RowType, untyped.IsUntyped ? null : ResolveUntyped(untyped, options));
        }

        var type = value.GetType();
        if (type.IsEnum) return value.ToString();

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(RowRef<>) || def == typeof(SubrowRef<>))
            {
                var rowId = (uint)type.GetProperty("RowId")!.GetValue(value)!;
                var rowType = type.GetGenericArguments()[0];
                string? label = null;
                if (options.ResolveReferences && def == typeof(RowRef<>) && HasLabel(rowType))
                {
                    try
                    {
                        var target = type.GetProperty("ValueNullable")!.GetValue(value);
                        if (target != null) label = LabelOf(target);
                    }
                    catch
                    {
                        // Unresolvable reference: emit id only.
                    }
                }

                return RefToJson(rowId, rowType, label);
            }

            if (def == typeof(Collection<>))
            {
                var arr = new JsonArray();
                if (depth >= options.MaxDepth) return arr;
                var n = 0;
                foreach (var item in (IEnumerable)value)
                {
                    if (n++ >= options.MaxCollectionItems) break;
                    arr.Add(ValueToJson(item, options, depth + 1));
                }

                // Fixed-size Excel arrays are mostly zero padding; drop trailing empty slots (leading indices stay stable).
                while (arr.Count > 0 && IsEmpty(arr[^1])) arr.RemoveAt(arr.Count - 1);
                return arr;
            }
        }

        if (type.IsValueType && type.Namespace?.StartsWith(SheetsNamespace, StringComparison.Ordinal) == true)
        {
            // Inline struct (e.g. Quest.QuestParamsStruct) or a row value.
            if (depth >= options.MaxDepth) return null;
            var nested = new JsonObject();
            WriteProperties(nested, value, options, depth + 1);
            return nested;
        }

        return value.ToString();
    }

    /// <summary>True for null, 0, false, "", empty arrays, and objects whose values are all empty (e.g. a RowRef to row 0).</summary>
    public static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.All(kv => kv.Key == "sheet" || IsEmpty(kv.Value)),
        JsonValue v => v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => v.GetValue<string>().Length == 0,
            System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Null => true,
            System.Text.Json.JsonValueKind.Number => v.ToJsonString() is "0" or "0.0" or "-0",
            _ => false,
        },
        _ => false,
    };

    private static readonly ConcurrentDictionary<Type, bool> LabelCache = new();

    private static bool HasLabel(Type rowType) => LabelCache.GetOrAdd(rowType, static t =>
        LabelProperties.Any(n => t.GetProperty(n)?.PropertyType == typeof(ReadOnlySeString)));

    private static string? ResolveUntyped(RowRef reference, Options options)
    {
        if (!options.ResolveReferences || reference.RowType == null) return null;
        try
        {
            var method = typeof(RowRef).GetMethod(IsSubrowType(reference.RowType) ? nameof(RowRef.GetValueOrDefaultSubrow) : nameof(RowRef.GetValueOrDefault))!
                .MakeGenericMethod(reference.RowType);
            var target = method.Invoke(reference, null);
            if (target == null) return null;
            if (target is IEnumerable subrows and not string)
            {
                foreach (var first in subrows) return LabelOf(first!);
                return null;
            }

            return LabelOf(target);
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject RefToJson(uint rowId, Type? rowType, string? label)
    {
        var o = new JsonObject { ["rowId"] = rowId };
        if (rowType != null) o["sheet"] = SheetName(rowType);
        if (!string.IsNullOrEmpty(label)) o["name"] = label;
        return o;
    }

    private static FrozenDictionary<string, Type> BuildSheetTypeMap()
    {
        var dict = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in SafeTypes(typeof(Lumina.Excel.Sheets.Item).Assembly))
        {
            if (!type.IsValueType || type.Namespace != SheetsNamespace || type.IsNested) continue;
            var attr = type.GetCustomAttribute<SheetAttribute>();
            if (attr == null) continue;
            var isRow = type.GetInterfaces().Any(i => i.IsGenericType &&
                                                     (i.GetGenericTypeDefinition() == typeof(IExcelRow<>) ||
                                                      i.GetGenericTypeDefinition() == typeof(IExcelSubrow<>)));
            if (!isRow) continue;
            dict.TryAdd(attr.Name ?? type.Name, type);
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}
