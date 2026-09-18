using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Core.Registry;

internal enum ParameterKind
{
    Argument,
    ToolContext,
    CancellationToken,
}

internal sealed class ParameterBinding
{
    public required ParameterInfo Info { get; init; }

    public required ParameterKind Kind { get; init; }

    public required string JsonName { get; init; }

    public required Type Type { get; init; }

    /// <summary><see cref="Type"/> with Nullable&lt;T&gt; removed.</summary>
    public required Type ValueType { get; init; }

    public bool IsNullable { get; init; }

    public bool IsRequired { get; init; }

    public bool HasDefault { get; init; }

    public object? DefaultValue { get; init; }

    public McpParamAttribute? Attribute { get; init; }

    /// <summary>Allowed string values ([McpParam(Enum=...)] or C# enum names), used for validation and completion.</summary>
    public string[]? EnumValues { get; init; }

    public JsonObject? Schema { get; set; }

    /// <summary>Named subschemas that <see cref="Schema"/> references as <c>#/$defs/&lt;name&gt;</c> (recursive types), or null.</summary>
    public JsonObject? SchemaDefs { get; set; }
}

internal static class ArgumentBinder
{
    public static ParameterBinding[] Describe(MethodInfo method)
    {
        var nullability = new NullabilityInfoContext();
        var parameters = method.GetParameters();
        var result = new ParameterBinding[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType.IsByRef || p.IsOut)
                throw new ArgumentException($"{Where(method)}: parameter '{p.Name}' must not be ref/out.");

            if (p.ParameterType == typeof(ToolContext))
            {
                result[i] = new ParameterBinding { Info = p, Kind = ParameterKind.ToolContext, JsonName = p.Name!, Type = p.ParameterType, ValueType = p.ParameterType };
                continue;
            }

            if (p.ParameterType == typeof(CancellationToken))
            {
                result[i] = new ParameterBinding { Info = p, Kind = ParameterKind.CancellationToken, JsonName = p.Name!, Type = p.ParameterType, ValueType = p.ParameterType };
                continue;
            }

            var valueType = TypeShapes.UnwrapNullable(p.ParameterType);
            var isNullable = Nullable.GetUnderlyingType(p.ParameterType) is not null ||
                             (!p.ParameterType.IsValueType && nullability.Create(p).ReadState == NullabilityState.Nullable);
            var hasDefault = p.HasDefaultValue;
            object? defaultValue = null;
            if (hasDefault)
            {
                defaultValue = p.DefaultValue is DBNull or Missing ? null : p.DefaultValue;
                if (defaultValue is not null && valueType.IsEnum && defaultValue.GetType() != valueType)
                    defaultValue = Enum.ToObject(valueType, defaultValue);
            }

            var attr = p.GetCustomAttribute<McpParamAttribute>();
            string[]? enumValues = attr?.Enum is { Length: > 0 } explicitValues ? explicitValues : null;
            if (enumValues is null)
            {
                var elementType = TypeShapes.GetEnumerableElementType(valueType);
                var enumType = valueType.IsEnum ? valueType : elementType is not null && TypeShapes.UnwrapNullable(elementType).IsEnum ? TypeShapes.UnwrapNullable(elementType) : null;
                if (enumType is not null)
                    enumValues = TypeShapes.EnumNames(enumType);
            }

            var binding = new ParameterBinding
            {
                Info = p,
                Kind = ParameterKind.Argument,
                JsonName = JsonNamingPolicy.CamelCase.ConvertName(p.Name ?? $"arg{i}"),
                Type = p.ParameterType,
                ValueType = valueType,
                IsNullable = isNullable,
                IsRequired = !hasDefault && !isNullable,
                HasDefault = hasDefault,
                DefaultValue = defaultValue,
                Attribute = attr,
                EnumValues = enumValues,
            };
            binding.Schema = SchemaGenerator.ForParameter(binding);
            result[i] = binding;
        }

        return result;
    }

    private static string Where(MethodInfo m) => $"{m.DeclaringType?.FullName}.{m.Name}";

    /// <summary>
    /// Binds a JSON arguments object. Collects every problem into <paramref name="errors"/> so a model
    /// can fix them all in one retry. Injected parameters come from <paramref name="context"/>.
    /// </summary>
    public static object?[] Bind(
        IReadOnlyList<ParameterBinding> parameters,
        JsonObject? arguments,
        ToolContext? context,
        CancellationToken cancellationToken,
        List<string> errors,
        bool rejectUnknown = true)
    {
        var values = new object?[parameters.Count];
        HashSet<string>? used = arguments is null ? null : new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            switch (p.Kind)
            {
                case ParameterKind.ToolContext:
                    values[i] = context;
                    continue;
                case ParameterKind.CancellationToken:
                    values[i] = cancellationToken;
                    continue;
            }

            JsonNode? node = null;
            var present = false;
            if (arguments is not null)
            {
                if (arguments.TryGetPropertyValue(p.JsonName, out node))
                {
                    present = true;
                    used!.Add(p.JsonName);
                }
                else
                {
                    foreach (var kv in arguments)
                    {
                        if (string.Equals(kv.Key, p.JsonName, StringComparison.OrdinalIgnoreCase))
                        {
                            node = kv.Value;
                            present = true;
                            used!.Add(kv.Key);
                            break;
                        }
                    }
                }
            }

            if (!present || node is null)
            {
                if (present && p.IsNullable)
                {
                    values[i] = null;
                }
                else if (p.HasDefault)
                {
                    values[i] = p.DefaultValue;
                }
                else if (p.IsNullable)
                {
                    values[i] = null;
                }
                else
                {
                    errors.Add(present
                        ? $"argument '{p.JsonName}' must not be null"
                        : $"missing required argument '{p.JsonName}'");
                    values[i] = DefaultOf(p.Type);
                }

                continue;
            }

            if (!TryConvert(node, p.ValueType, p.JsonName, out var value, out var error))
            {
                errors.Add(error!);
                values[i] = DefaultOf(p.Type);
                continue;
            }

            if (!ValidateConstraints(p, ref value, out error))
            {
                errors.Add(error!);
                values[i] = DefaultOf(p.Type);
                continue;
            }

            values[i] = value;
        }

        if (rejectUnknown && arguments is not null && used!.Count < arguments.Count)
        {
            var known = parameters.Where(x => x.Kind == ParameterKind.Argument).Select(x => x.JsonName).ToArray();
            foreach (var kv in arguments)
            {
                if (!used.Contains(kv.Key))
                {
                    errors.Add(known.Length == 0
                        ? $"unknown argument '{kv.Key}' (this takes no arguments)"
                        : $"unknown argument '{kv.Key}' (valid: {string.Join(", ", known)})");
                }
            }
        }

        return values;
    }

    /// <summary>Binds string-valued arguments (prompt arguments, URI template variables).</summary>
    public static object?[] BindStrings(
        IReadOnlyList<ParameterBinding> parameters,
        IReadOnlyDictionary<string, string> arguments,
        ToolContext? context,
        CancellationToken cancellationToken,
        List<string> errors)
    {
        var obj = new JsonObject();
        foreach (var kv in arguments)
            obj[kv.Key] = kv.Value;
        return Bind(parameters, obj, context, cancellationToken, errors, rejectUnknown: false);
    }

    private static object? DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    private static bool ValidateConstraints(ParameterBinding p, ref object? value, out string? error)
    {
        error = null;
        var attr = p.Attribute;
        if (attr is not null && (!double.IsNaN(attr.Minimum) || !double.IsNaN(attr.Maximum)))
        {
            IEnumerable<object?> items = value is System.Collections.IEnumerable e && value is not string
                ? e.Cast<object?>()
                : [value];
            foreach (var item in items)
            {
                if (item is null || !TryToDouble(item, out var d))
                    continue;
                if (!double.IsNaN(attr.Minimum) && d < attr.Minimum || !double.IsNaN(attr.Maximum) && d > attr.Maximum)
                {
                    error = RangeMessage(p.JsonName, attr.Minimum, attr.Maximum, item);
                    return false;
                }
            }
        }

        if (attr?.Enum is { Length: > 0 } allowed)
        {
            if (value is string s)
            {
                var canonical = MatchAllowed(allowed, s);
                if (canonical is null)
                {
                    error = $"argument '{p.JsonName}' has invalid value '{s}' (allowed: {string.Join(", ", allowed)})";
                    return false;
                }

                value = canonical;
            }
            else if (value is string[] arr)
            {
                for (var i = 0; i < arr.Length; i++)
                {
                    var canonical = MatchAllowed(allowed, arr[i]);
                    if (canonical is null)
                    {
                        error = $"argument '{p.JsonName}[{i}]' has invalid value '{arr[i]}' (allowed: {string.Join(", ", allowed)})";
                        return false;
                    }

                    arr[i] = canonical;
                }
            }
            else if (value is List<string> list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var canonical = MatchAllowed(allowed, list[i]);
                    if (canonical is null)
                    {
                        error = $"argument '{p.JsonName}[{i}]' has invalid value '{list[i]}' (allowed: {string.Join(", ", allowed)})";
                        return false;
                    }

                    list[i] = canonical;
                }
            }
        }

        return true;
    }

    private static string RangeMessage(string name, double min, double max, object got)
    {
        var g = Convert.ToString(got, CultureInfo.InvariantCulture);
        if (!double.IsNaN(min) && !double.IsNaN(max))
            return $"argument '{name}' must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)} (got {g})";
        if (!double.IsNaN(min))
            return $"argument '{name}' must be >= {min.ToString(CultureInfo.InvariantCulture)} (got {g})";
        return $"argument '{name}' must be <= {max.ToString(CultureInfo.InvariantCulture)} (got {g})";
    }

    private static string? MatchAllowed(string[] allowed, string value)
    {
        foreach (var a in allowed)
        {
            if (a == value)
                return a;
        }

        foreach (var a in allowed)
        {
            if (string.Equals(a, value, StringComparison.OrdinalIgnoreCase))
                return a;
        }

        return null;
    }

    private static bool TryToDouble(object value, out double d)
    {
        switch (value)
        {
            case double x: d = x; return true;
            case float x: d = x; return true;
            case decimal x: d = (double)x; return true;
            case int x: d = x; return true;
            case long x: d = x; return true;
            case short x: d = x; return true;
            case byte x: d = x; return true;
            case sbyte x: d = x; return true;
            case uint x: d = x; return true;
            case ulong x: d = x; return true;
            case ushort x: d = x; return true;
            default: d = 0; return false;
        }
    }

    private static string Describe(JsonNode node) => node switch
    {
        JsonObject => "an object",
        JsonArray => "an array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => $"string \"{Truncate(v.GetValue<string>())}\"",
            JsonValueKind.Number => $"number {v.ToJsonString()}",
            JsonValueKind.True or JsonValueKind.False => $"boolean {v.ToJsonString()}",
            _ => v.ToJsonString(),
        },
        _ => "null",
    };

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";

    public static bool TryConvert(JsonNode? node, Type type, string path, out object? value, out string? error)
    {
        value = null;
        error = null;
        type = TypeShapes.UnwrapNullable(type);

        if (node is null)
        {
            if (!type.IsValueType)
                return true;
            error = $"argument '{path}' must not be null";
            return false;
        }

        var kind = node is JsonValue jv ? jv.GetValueKind() : node is JsonObject ? JsonValueKind.Object : JsonValueKind.Array;

        if (type == typeof(string))
        {
            if (kind == JsonValueKind.String)
            {
                value = node.GetValue<string>();
                return true;
            }

            if (kind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                value = node.ToJsonString();
                return true;
            }

            error = $"argument '{path}' must be a string (got {Describe(node)})";
            return false;
        }

        if (type == typeof(bool))
        {
            if (kind is JsonValueKind.True or JsonValueKind.False)
            {
                value = kind == JsonValueKind.True;
                return true;
            }

            if (kind == JsonValueKind.String && bool.TryParse(node.GetValue<string>().Trim(), out var b))
            {
                value = b;
                return true;
            }

            error = $"argument '{path}' must be a boolean (got {Describe(node)})";
            return false;
        }

        if (TypeShapes.IsInteger(type))
            return TryConvertInteger(node, kind, type, path, out value, out error);

        if (TypeShapes.IsFloat(type))
        {
            string? text = kind switch
            {
                JsonValueKind.Number => node.ToJsonString(),
                JsonValueKind.String => node.GetValue<string>().Trim(),
                _ => null,
            };
            if (text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            {
                if (type == typeof(double))
                    value = d;
                else if (type == typeof(float))
                    value = (float)d;
                else if (type == typeof(Half))
                    value = (Half)d;
                else
                    value = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : (decimal)d;
                return true;
            }

            error = $"argument '{path}' must be a number (got {Describe(node)})";
            return false;
        }

        if (type.IsEnum)
        {
            if (kind != JsonValueKind.String)
            {
                error = $"argument '{path}' must be one of: {string.Join(", ", TypeShapes.EnumNames(type))} (got {Describe(node)})";
                return false;
            }

            if (TryParseEnum(type, node.GetValue<string>(), out value))
                return true;
            error = $"argument '{path}' has unknown value '{Truncate(node.GetValue<string>())}' (allowed: {string.Join(", ", TypeShapes.EnumNames(type))})";
            return false;
        }

        if (type == typeof(char))
        {
            if (kind == JsonValueKind.String && node.GetValue<string>() is { Length: 1 } one)
            {
                value = one[0];
                return true;
            }

            error = $"argument '{path}' must be a single character";
            return false;
        }

        if (type == typeof(JsonNode) || type == typeof(object))
        {
            value = node.DeepClone();
            return true;
        }

        if (type == typeof(JsonObject))
        {
            if (node is JsonObject o)
            {
                value = o.DeepClone();
                return true;
            }

            error = $"argument '{path}' must be an object (got {Describe(node)})";
            return false;
        }

        if (type == typeof(JsonArray))
        {
            if (node is JsonArray a)
            {
                value = a.DeepClone();
                return true;
            }

            error = $"argument '{path}' must be an array (got {Describe(node)})";
            return false;
        }

        if (type == typeof(JsonElement))
        {
            value = JsonSerializer.SerializeToElement(node);
            return true;
        }

        if (type != typeof(byte[]) && TypeShapes.GetDictionaryValueType(type) is null && TypeShapes.GetEnumerableElementType(type) is { } elementType)
        {
            if (node is not JsonArray array)
            {
                error = $"argument '{path}' must be an array (got {Describe(node)})";
                return false;
            }

            var items = Array.CreateInstance(elementType, array.Count);
            var ok = true;
            for (var i = 0; i < array.Count; i++)
            {
                if (!TryConvert(array[i], elementType, $"{path}[{i}]", out var item, out var itemError))
                {
                    error = itemError;
                    ok = false;
                    break;
                }

                items.SetValue(item, i);
            }

            if (!ok)
                return false;

            if (type.IsArray || type.IsAssignableFrom(items.GetType()))
            {
                value = items;
                return true;
            }

            var listType = typeof(List<>).MakeGenericType(elementType);
            if (type.IsAssignableFrom(listType))
            {
                value = Activator.CreateInstance(listType, items);
                return true;
            }

            try
            {
                value = Activator.CreateInstance(type, items);
                return true;
            }
            catch (Exception)
            {
                error = $"argument '{path}': collection type {type.Name} is not supported";
                return false;
            }
        }

        try
        {
            value = node.Deserialize(type, McpJson.Options);
            if (value is null && type.IsValueType)
            {
                error = $"argument '{path}' must not be null";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            var at = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? path : path + ex.Path.TrimStart('$');
            error = $"argument '{at}' is invalid for type {FriendlyTypeName(type)}: {FirstSentence(ex.Message)}";
            return false;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
        {
            error = $"argument '{path}' is invalid for type {FriendlyTypeName(type)}: {FirstSentence(ex.Message)}";
            return false;
        }
    }

    private static string FirstSentence(string message)
    {
        var idx = message.IndexOf(". ", StringComparison.Ordinal);
        return idx > 0 ? message[..(idx + 1)] : message;
    }

    private static string FriendlyTypeName(Type t) => t.IsGenericType ? t.Name[..t.Name.IndexOf('`')] : t.Name;

    public static bool TryParseEnum(Type enumType, string text, out object? value)
    {
        value = null;
        var fields = enumType.GetFields(BindingFlags.Public | BindingFlags.Static);
        var names = TypeShapes.EnumNames(enumType);
        var trimmed = text.Trim();

        for (var pass = 0; pass < 3; pass++)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                var match = pass switch
                {
                    0 => names[i] == trimmed,
                    1 => string.Equals(names[i], trimmed, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(fields[i].Name, trimmed, StringComparison.OrdinalIgnoreCase),
                    _ => string.Equals(Normalize(names[i]), Normalize(trimmed), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Normalize(fields[i].Name), Normalize(trimmed), StringComparison.OrdinalIgnoreCase),
                };
                if (match)
                {
                    value = fields[i].GetValue(null);
                    return true;
                }
            }
        }

        return false;

        static string Normalize(string s) => s.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
    }

    private static bool TryConvertInteger(JsonNode node, JsonValueKind kind, Type type, string path, out object? value, out string? error)
    {
        value = null;
        error = null;
        string? text = kind switch
        {
            JsonValueKind.Number => node.ToJsonString(),
            JsonValueKind.String => node.GetValue<string>().Trim(),
            _ => null,
        };

        Int128 n = 0;
        var parsed = false;
        if (text is not null)
        {
            if (Int128.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
            {
                parsed = true;
            }
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            {
                if (Math.Floor(d) != d)
                {
                    error = $"argument '{path}' must be an integer (got {text})";
                    return false;
                }

                if (Math.Abs(d) < 1.7e38)
                {
                    n = (Int128)d;
                    parsed = true;
                }
            }
        }

        if (!parsed)
        {
            error = $"argument '{path}' must be an integer (got {Describe(node)})";
            return false;
        }

        (Int128 min, Int128 max) = IntegerBounds(type);
        if (n < min || n > max)
        {
            error = $"argument '{path}' is out of range for {type.Name} ({min}..{max}, got {n})";
            return false;
        }

        value = type switch
        {
            _ when type == typeof(int) => (int)n,
            _ when type == typeof(long) => (long)n,
            _ when type == typeof(short) => (short)n,
            _ when type == typeof(byte) => (byte)n,
            _ when type == typeof(sbyte) => (sbyte)n,
            _ when type == typeof(uint) => (uint)n,
            _ when type == typeof(ulong) => (ulong)n,
            _ when type == typeof(ushort) => (ushort)n,
            _ when type == typeof(nint) => (nint)(long)n,
            _ when type == typeof(nuint) => (nuint)(ulong)n,
            _ when type == typeof(UInt128) => (UInt128)n,
            _ => (object)n,
        };
        return true;
    }

    private static (Int128, Int128) IntegerBounds(Type t)
    {
        if (t == typeof(int)) return (int.MinValue, int.MaxValue);
        if (t == typeof(long)) return (long.MinValue, long.MaxValue);
        if (t == typeof(short)) return (short.MinValue, short.MaxValue);
        if (t == typeof(byte)) return (byte.MinValue, byte.MaxValue);
        if (t == typeof(sbyte)) return (sbyte.MinValue, sbyte.MaxValue);
        if (t == typeof(uint)) return (uint.MinValue, uint.MaxValue);
        if (t == typeof(ulong)) return (ulong.MinValue, ulong.MaxValue);
        if (t == typeof(ushort)) return (ushort.MinValue, ushort.MaxValue);
        if (t == typeof(nint)) return (long.MinValue, long.MaxValue);
        if (t == typeof(nuint)) return (ulong.MinValue, ulong.MaxValue);
        if (t == typeof(UInt128)) return (0, Int128.MaxValue);
        return (Int128.MinValue, Int128.MaxValue);
    }
}
