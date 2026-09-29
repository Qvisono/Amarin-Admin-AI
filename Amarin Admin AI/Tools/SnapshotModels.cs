using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Tools;

internal sealed class ServiceSnapshotEntry
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StartType { get; set; } = string.Empty;
}

internal sealed class ScheduledTaskSnapshotEntry
{
    public string TaskName { get; set; } = string.Empty;
    public string TaskPath { get; set; } = string.Empty;

    /// <summary>Ready, Running, Queued, Disabled, Unknown.</summary>
    [JsonConverter(typeof(TaskStateConverter))]
    public string State { get; set; } = string.Empty;
}

internal sealed class StartupProgramSnapshotEntry
{
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? Location { get; set; }
}

/// <summary>
/// Состояние задачи строкой, даже если PowerShell прислал его числом.
/// </summary>
/// <remarks>
/// <c>ConvertTo-Json</c> в Windows PowerShell 5.1 пишет перечисления числами: <c>"State": 3</c>.
/// Строковое поле на числе роняло разбор всего списка, и снимок задач выходил пустым — откатывать
/// было нечего. Скрипт снимка теперь сам приводит состояние к строке, а этот разбор читает и
/// числа — на случай снимков, сделанных иначе. Числа — StateEnum планировщика.
/// </remarks>
internal sealed class TaskStateConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "",
            JsonTokenType.Number when reader.TryGetInt32(out var number) => number switch
            {
                1 => "Disabled",
                2 => "Queued",
                3 => "Ready",
                4 => "Running",
                _ => "Unknown"
            },
            JsonTokenType.Null => "",
            _ => Skip(ref reader)
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    private static string Skip(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return "Unknown";
    }
}
