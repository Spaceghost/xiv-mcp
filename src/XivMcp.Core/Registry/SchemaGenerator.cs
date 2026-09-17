using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace XivMcp.Core.Registry;

internal static class TypeShapes
{
    public static Type UnwrapNullable(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    public static bool IsInteger(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) ||
        t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(nint) || t == typeof(nuint) ||
        t == typeof(Int128) || t == typeof(UInt128);

    public static bool IsFloat(Type t) => t == typeof(double) || t == typeof(float) || t == typeof(decimal) || t == typeof(Half);

    public static bool IsJsonAny(Type t) =>
        t == typeof(object) || t == typeof(JsonNode) || t == typeof(JsonElement) || t == typeof(JsonValue) || t == typeof(JsonDocument);

    /// <summary>Inclusive integer bounds worth advertising in a schema (int/long are left unbounded to keep schemas readable).</summary>
    public static (double? Min, double? Max) SchemaRange(Type t)
    {
        if (t == typeof(byte)) return (byte.MinValue, byte.MaxValue);
        if (t == typeof(sbyte)) return (sbyte.MinValue, sbyte.MaxValue);
        if (t == typeof(short)) return (short.MinValue, short.MaxValue);
        if (t == typeof(ushort)) return (ushort.MinValue, ushort.MaxValue);
        if (t == typeof(uint)) return (uint.MinValue, uint.MaxValue);
        if (t == typeof(ulong) || t == typeof(nuint) || t == typeof(UInt128)) return (0, null);
        return (null, null);
    }

    public static Type? GetDictionaryValueType(Type t)
    {
        foreach (var candidate in SelfAndInterfaces(t))
        {
            if (!candidate.IsGenericType)
                continue;
            var def = candidate.GetGenericTypeDefinition();
            if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                return candidate.GetGenericArguments()[1];
        }

        return null;
    }

    public static Type? GetEnumerableElementType(Type t)
    {
        if (t == typeof(string) || t == typeof(byte[]))
            return null;
        if (t.IsArray)
            return t.GetElementType();
        if (GetDictionaryValueType(t) is not null)
            return null;
        foreach (var candidate in SelfAndInterfaces(t))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return candidate.GetGenericArguments()[0];
        }

        return null;
    }

    private static IEnumerable<Type> SelfAndInterfaces(Type t)
    {
        yield return t;
        foreach (var i in t.GetInterfaces())
            yield return i;
    }

    /// <summary>Whether values of this type serialize to a JSON object (and so need no {"result": ...} wrapper).</summary>
    public static bool SerializesAsObject(Type t)
    {
        t = UnwrapNullable(t);
        if (t == typeof(JsonObject))
            return true;
        if (IsJsonAny(t) || t == typeof(JsonArray) || t == typeof(string) || t.IsPrimitive || t.IsEnum || IsFloat(t) || IsInteger(t))
            return false;
        if (t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) ||
            t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(Uri) || t == typeof(byte[]) || t == typeof(Version))
            return false;
        if (GetDictionaryValueType(t) is not null)
            return true;
        if (GetEnumerableElementType(t) is not null)
            return false;
        return McpJson.Options.GetTypeInfo(t).Kind == JsonTypeInfoKind.Object;
    }

    /// <summary>JSON names of an enum's members as serialized by <see cref="McpJson.Options"/>.</summary>
    public static string[] EnumNames(Type enumType)
    {
        var fields = enumType.GetFields(BindingFlags.Public | BindingFlags.Static);
        var names = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            var custom = fields[i].GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
            names[i] = custom?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(fields[i].Name);
        }

        return names;
    }
}

/// <summary>JSON Schema (draft 2020-12 vocabulary, no $schema keyword) for C# parameters and return types.</summary>
internal static class SchemaGenerator
{
    private const int MaxDepth = 12;

    public static JsonObject BuildInputSchema(IReadOnlyList<ParameterBinding> parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in parameters)
        {
            if (p.Kind != ParameterKind.Argument)
                continue;
            properties[p.JsonName] = p.Schema!.DeepClone();
            if (p.IsRequired)
                required.Add(p.JsonName);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
            schema["required"] = required;
        schema["additionalProperties"] = false;
        return schema;
    }

    /// <summary>Schema for one bindable parameter, including [McpParam] metadata and the C# default.</summary>
    public static JsonObject ForParameter(ParameterBinding p)
    {
        var schema = ForType(p.ValueType, forOutput: false, new HashSet<Type>(), 0);
        var attr = p.Attribute;
        if (attr is not null && !string.IsNullOrWhiteSpace(attr.Description))
            schema.Insert(0, "description", attr.Description);

        if (attr?.Enum is { Length: > 0 } allowed)
        {
            var target = schema["type"]?.GetValue<string>() == "array" && schema["items"] is JsonObject items ? items : schema;
            target["enum"] = new JsonArray(allowed.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        if (attr is not null)
        {
            var target = schema["type"]?.GetValue<string>() == "array" && schema["items"] is JsonObject items ? items : schema;
            if (!double.IsNaN(attr.Minimum))
                target["minimum"] = JsonNumber(attr.Minimum);
            if (!double.IsNaN(attr.Maximum))
                target["maximum"] = JsonNumber(attr.Maximum);
        }

        if (p.HasDefault && p.DefaultValue is not null)
        {
            try
            {
                var node = JsonSerializer.SerializeToNode(p.DefaultValue, p.ValueType, McpJson.Options);
                if (node is not null)
                    schema["default"] = node;
            }
            catch (Exception)
            {
                // Defaults are informational only.
            }
        }

        return schema;
    }

    /// <summary>
    /// Output schema for a tool return type. Non-object values are described as {"result": value};
    /// <paramref name="wrap"/> tells the caller to wrap structuredContent identically.
    /// </summary>
    public static JsonObject? BuildOutputSchema(Type valueType, bool nullable, out bool wrap)
    {
        wrap = false;
        valueType = TypeShapes.UnwrapNullable(valueType);
        if (valueType == typeof(void) || valueType == typeof(ToolResult))
            return null;

        var inner = ForType(valueType, forOutput: true, new HashSet<Type>(), 0);
        if (TypeShapes.SerializesAsObject(valueType) && !nullable)
            return inner;

        wrap = true;
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["result"] = inner },
        };
        if (!nullable)
            schema["required"] = new JsonArray("result");
        return schema;
    }

    public static JsonObject ForType(Type type, bool forOutput, HashSet<Type> visiting, int depth)
    {
        type = TypeShapes.UnwrapNullable(type);

        if (type == typeof(string))
            return new JsonObject { ["type"] = "string" };
        if (type == typeof(char))
            return new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1 };
        if (type == typeof(bool))
            return new JsonObject { ["type"] = "boolean" };
        if (TypeShapes.IsInteger(type))
        {
            var s = new JsonObject { ["type"] = "integer" };
            var (min, max) = TypeShapes.SchemaRange(type);
            if (min is not null)
                s["minimum"] = JsonNumber(min.Value);
            if (max is not null)
                s["maximum"] = JsonNumber(max.Value);
            return s;
        }

        if (TypeShapes.IsFloat(type))
            return new JsonObject { ["type"] = "number" };
        if (type.IsEnum)
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(TypeShapes.EnumNames(type).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            };
        }

        if (type == typeof(Guid))
            return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (type == typeof(DateOnly))
            return new JsonObject { ["type"] = "string", ["format"] = "date" };
        if (type == typeof(TimeOnly))
            return new JsonObject { ["type"] = "string", ["format"] = "time" };
        if (type == typeof(TimeSpan) || type == typeof(Version))
            return new JsonObject { ["type"] = "string" };
        if (type == typeof(Uri))
            return new JsonObject { ["type"] = "string", ["format"] = "uri" };
        if (type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>) || type == typeof(Memory<byte>))
            return new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" };
        if (type == typeof(JsonObject))
            return new JsonObject { ["type"] = "object" };
        if (type == typeof(JsonArray))
            return new JsonObject { ["type"] = "array" };
        if (TypeShapes.IsJsonAny(type))
            return new JsonObject();

        if (depth >= MaxDepth || visiting.Contains(type))
            return new JsonObject();

        if (TypeShapes.GetDictionaryValueType(type) is { } valueType)
        {
            visiting.Add(type);
            var s = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = ForType(valueType, forOutput, visiting, depth + 1),
            };
            visiting.Remove(type);
            return s;
        }

        if (TypeShapes.GetEnumerableElementType(type) is { } element)
        {
            visiting.Add(type);
            var s = new JsonObject { ["type"] = "array", ["items"] = ForType(element, forOutput, visiting, depth + 1) };
            visiting.Remove(type);
            return s;
        }

        JsonTypeInfo info;
        try
        {
            info = McpJson.Options.GetTypeInfo(type);
        }
        catch (Exception)
        {
            return new JsonObject();
        }

        if (info.Kind != JsonTypeInfoKind.Object)
            return new JsonObject();

        visiting.Add(type);
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var prop in info.Properties)
        {
            if (forOutput ? prop.Get is null : (prop.Set is null && !IsConstructorParameter(type, prop.Name)))
                continue;

            var member = prop.AttributeProvider as MemberInfo;
            var propSchema = ForType(prop.PropertyType, forOutput, visiting, depth + 1);
            var description = member?.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(description))
                propSchema.Insert(0, "description", description);
            properties[prop.Name] = propSchema;

            if (forOutput)
            {
                var ignore = member?.GetCustomAttribute<JsonIgnoreAttribute>();
                var alwaysWritten = prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) is null;
                if (alwaysWritten && ignore is null)
                    required.Add(prop.Name);
            }
            else if (prop.IsRequired)
            {
                required.Add(prop.Name);
            }
        }

        visiting.Remove(type);
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private static bool IsConstructorParameter(Type type, string jsonName)
    {
        foreach (var ctor in type.GetConstructors())
        {
            foreach (var p in ctor.GetParameters())
            {
                if (p.Name is not null && string.Equals(JsonNamingPolicy.CamelCase.ConvertName(p.Name), jsonName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static JsonNode JsonNumber(double value) =>
        Math.Floor(value) == value && Math.Abs(value) < 9007199254740992d
            ? JsonValue.Create((long)value)
            : JsonValue.Create(value);

    /// <summary>Short human-readable signature used in argument error messages, e.g. "itemId: integer (required)".</summary>
    public static string DescribeParameters(IReadOnlyList<ParameterBinding> parameters)
    {
        var parts = new List<string>();
        foreach (var p in parameters)
        {
            if (p.Kind != ParameterKind.Argument)
                continue;
            var type = p.Schema?["type"]?.GetValue<string>() ?? "any";
            if (type == "array" && p.Schema?["items"]?["type"] is JsonValue it)
                type = it.GetValue<string>() + "[]";
            var detail = type;
            if (p.EnumValues is { Length: > 0 } values)
                detail += " (" + string.Join("|", values.Take(12)) + (values.Length > 12 ? "|…" : "") + ")";
            var min = p.Schema?["minimum"];
            var max = p.Schema?["maximum"];
            if (min is not null || max is not null)
                detail += $" [{min?.ToJsonString() ?? ""}..{max?.ToJsonString() ?? ""}]";
            if (p.IsRequired)
                detail += ", required";
            else if (p.HasDefault && p.DefaultValue is not null)
                detail += ", default " + (p.Schema?["default"]?.ToJsonString() ?? p.DefaultValue.ToString());
            else
                detail += ", optional";
            parts.Add($"{p.JsonName}: {detail}");
        }

        return parts.Count == 0 ? "(no arguments)" : string.Join("; ", parts);
    }
}
