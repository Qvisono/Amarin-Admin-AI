using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

internal static class AppJson
{
    /// <summary>Глубина вложенности, которую читают и пишут файлы переписок.</summary>
    public const int MaxDepth = 256;

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Заводской предел — 64 уровня. Переписка вкладывается глубоко: раунды, вызовы,
            // вложенные агенты, а с 1.28.0 ещё и варианты ответа внутри вариантов. Упёршийся
            // в предел чат не записывался и не читался — выглядел бы пропавшим.
            MaxDepth = MaxDepth
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
