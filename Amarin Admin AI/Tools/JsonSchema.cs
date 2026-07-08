using System.Text.Json;

namespace Amarin.Tools;

internal static class JsonSchema
{
    public static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}