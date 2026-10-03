using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

internal sealed class ToolArgumentsJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "{}",
            JsonTokenType.StartObject or JsonTokenType.StartArray =>
                JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
            JsonTokenType.Null => "{}",
            _ => "{}"
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}