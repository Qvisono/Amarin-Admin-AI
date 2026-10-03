using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

/// <summary>Точка восстановления Windows — не снимок программы, а целиком система.</summary>
public sealed record WindowsRestorePoint(int Sequence, DateTime Created, string Description, int Type);

/// <summary>Список точек восстановления Windows для журнала.</summary>
/// <remarks>
/// Тот же <c>Get-ComputerRestorePoint</c>, что у <c>restore_point list</c>, но с выводом в JSON:
/// инструмент отдаёт таблицу для модели, а разбирать её обратно значило бы ломаться на каждой
/// смене формулировки. Без прав администратора Windows отвечает отказом, а не пустым списком, —
/// и это различается: «точек нет» и «точки не видны» — разные ответы человеку.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsRestorePoints
{
    private const string Script = """
        $ErrorActionPreference = 'Continue'
        $Error.Clear()
        $points = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue)
        $denied = $false
        foreach ($e in $Error) {
          if ([string]$e -match 'Access denied|access is denied|отказано в доступе|недостаточно прав|UnauthorizedAccess|0x80070005') {
            $denied = $true
            break
          }
        }
        $rows = @($points | ForEach-Object {
          $created = $null
          try { $created = [System.Management.ManagementDateTimeConverter]::ToDateTime($_.CreationTime).ToString('o') } catch { }
          [PSCustomObject]@{
            seq = [int]$_.SequenceNumber
            created = $created
            description = [string]$_.Description
            type = [int]$_.RestorePointType
          }
        })
        [PSCustomObject]@{ denied = $denied; points = $rows } | ConvertTo-Json -Depth 4 -Compress
        """;

    /// <summary>Новые сверху. <paramref name="denied"/> — Windows не показала точки без прав.</summary>
    public static IReadOnlyList<WindowsRestorePoint> List(out bool denied)
    {
        denied = false;
        var result = PowerShellHelper.Run(Script, 60);
        return result.Success ? Parse(PowerShellHelper.ExtractStdout(result.Output), out denied) : [];
    }

    internal static IReadOnlyList<WindowsRestorePoint> Parse(string json, out bool denied)
    {
        denied = false;
        var start = json.IndexOf('{');
        if (start < 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json[start..]);
            var root = document.RootElement;
            denied = root.TryGetProperty("denied", out var d) && d.ValueKind == JsonValueKind.True;

            if (!root.TryGetProperty("points", out var points))
            {
                return [];
            }

            // Один элемент PowerShell 5.1 сворачивает из массива в объект.
            var items = points.ValueKind switch
            {
                JsonValueKind.Array => points.EnumerateArray().ToList(),
                JsonValueKind.Object => [points],
                _ => []
            };

            var list = new List<WindowsRestorePoint>();
            foreach (var item in items)
            {
                var created = item.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.String &&
                              DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture,
                                  DateTimeStyles.RoundtripKind, out var stamp)
                    ? stamp.ToLocalTime()
                    : DateTime.MinValue;
                list.Add(new WindowsRestorePoint(
                    item.TryGetProperty("seq", out var s) && s.TryGetInt32(out var seq) ? seq : 0,
                    created,
                    item.TryGetProperty("description", out var text) ? text.GetString() ?? "" : "",
                    item.TryGetProperty("type", out var t) && t.TryGetInt32(out var type) ? type : 0));
            }

            list.Sort((left, right) => right.Created.CompareTo(left.Created));
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
