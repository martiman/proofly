using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Proofly.Domain.Enums;

namespace Proofly.Api.Platform.Json;

/// <summary>Wire format: camelCase properties (dictionary keys untouched), decimals as strings (never JSON numbers), enums as contract names.</summary>
public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions());

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        // Dictionary keys are written as-is: they are data (enum wire names, entity ids, permission names), never property names.
        o.NumberHandling = JsonNumberHandling.Strict;
        o.Converters.Add(new DecimalStringConverter());
        o.Converters.Add(new WireEnumConverterFactory());
        return o;
    }
}

/// <summary>Money amounts: reads and writes JSON strings only. A JSON number is rejected, never coerced.</summary>
public sealed class DecimalStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) { throw new JsonException("Decimal must be a JSON string."); }
        var s = reader.GetString()!;
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
        {
            throw new JsonException("Invalid decimal string.");
        }
        return v;
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

public sealed class WireEnumConverterFactory : JsonConverterFactory
{
    private static readonly ConcurrentDictionary<Type, JsonConverter> Cache = new();

    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        Cache.GetOrAdd(typeToConvert, t => (JsonConverter)Activator.CreateInstance(typeof(WireEnumConverter<>).MakeGenericType(t))!);
}

public sealed class WireEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> ToWire = Enum.GetValues<T>().ToDictionary(v => v, Name);
    private static readonly Dictionary<string, T> FromWire = ToWire.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    private static string Name(T v) => v switch
    {
        Permission p => WireNames.ToPermission(p),
        ControlArea c => WireNames.ToLowerSnake(c),
        _ => WireNames.ToUpperSnake(v),
    };

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && FromWire.TryGetValue(reader.GetString()!, out var v)) { return v; }
        throw new JsonException($"Unknown {typeof(T).Name} value.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(ToWire[value]);
}
