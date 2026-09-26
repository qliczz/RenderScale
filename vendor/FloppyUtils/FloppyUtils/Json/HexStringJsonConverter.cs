using Newtonsoft.Json;
using System;

namespace FloppyUtils.Json;

/// <summary>
/// https://stackoverflow.com/a/43494134
/// </summary>
public class HexStringJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return typeof(uint).Equals(objectType);
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        writer.WriteValue($"0x{value:X8}");
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var str = reader.Value as string;
        if (string.IsNullOrEmpty(str) || !str.StartsWith("0x"))
        {
            throw new JsonSerializationException($"Expected hex value, got {str ?? "non-string"}");
        }

        return Convert.ToUInt32(str["0x".Length..], 16);
    }
}
