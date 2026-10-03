using System.Globalization;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>Команды Центра обновления: установка, скрытие, пауза.</summary>
/// <remarks>
/// Установка идёт через COM <c>Microsoft.Update.Session</c> — тот же агент, что у «Параметров»,
/// без стороннего модуля PSWindowsUpdate. Перезагрузку скрипты не делают никогда: человек
/// узнаёт, что она нужна, и решает сам.
/// </remarks>
internal static partial class UpdateCommands
{
    /// <summary>Дольше Windows паузу не даёт.</summary>
    public const int MaxPauseDays = 35;

    public const string SettingsKey = @"HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    /// <summary>Номера KB от модели: «KB5034441» или «5034441», не больше двадцати.</summary>
    public static bool TryKbList(IEnumerable<string?> values, out List<string> kbs, out string? error)
    {
        kbs = [];
        foreach (var raw in values)
        {
            var match = Kb().Match(raw?.Trim() ?? "");
            if (!match.Success)
            {
                error = $"«{raw}» is not a KB number (KB followed by 6-8 digits).";
                return false;
            }

            var kb = match.Groups[1].Value;
            if (!kbs.Contains(kb))
            {
                kbs.Add(kb);
            }
        }

        if (kbs.Count > 20)
        {
            error = "No more than 20 KB numbers at once.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Скрипт установки: пусто в <paramref name="kbs"/> — все ожидающие, кроме скрытых.</summary>
    public static string InstallScript(IReadOnlyList<string> kbs) => $$"""
        $ErrorActionPreference = 'Stop'
        $wanted = @({{KbList(kbs)}})
        $session = New-Object -ComObject Microsoft.Update.Session
        $found = $session.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0 and Type='Software'").Updates
        $batch = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($u in $found) {
          $ids = @($u.KBArticleIDs | ForEach-Object { [string]$_ })
          if ($wanted.Count -eq 0 -or ($ids | Where-Object { $wanted -contains $_ })) {
            if (-not $u.EulaAccepted) { $u.AcceptEula() }
            [void]$batch.Add($u)
          }
        }
        if ($batch.Count -eq 0) { 'NO_MATCHING_UPDATES'; return }
        $downloader = $session.CreateUpdateDownloader(); $downloader.Updates = $batch; [void]$downloader.Download()
        $installer = $session.CreateUpdateInstaller(); $installer.Updates = $batch
        $result = $installer.Install()
        for ($i = 0; $i -lt $batch.Count; $i++) {
          $r = $result.GetUpdateResult($i)
          '{0} | result {1} | 0x{2:X8}' -f $batch.Item($i).Title, $r.ResultCode, $r.HResult
        }
        "REBOOT_REQUIRED: $($result.RebootRequired)"
        """;

    /// <summary>Скрыть или вернуть обновления с этими номерами.</summary>
    public static string HideScript(IReadOnlyList<string> kbs, bool hide) => $$"""
        $ErrorActionPreference = 'Stop'
        $wanted = @({{KbList(kbs)}})
        $session = New-Object -ComObject Microsoft.Update.Session
        $found = $session.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden={{(hide ? 0 : 1)}}").Updates
        $done = 0
        foreach ($u in $found) {
          $ids = @($u.KBArticleIDs | ForEach-Object { [string]$_ })
          if ($ids | Where-Object { $wanted -contains $_ }) {
            $u.IsHidden = ${{(hide ? "true" : "false")}}
            $done++
            "{{(hide ? "hidden" : "unhidden")}}: $($u.Title)"
          }
        }
        if ($done -eq 0) { 'NO_MATCHING_UPDATES' }
        """;

    /// <summary>
    /// Пауза: те же значения, что пишут «Параметры» в <c>WindowsUpdate\UX\Settings</c>.
    /// </summary>
    /// <remarks>
    /// Время — в UTC и в формате «yyyy-MM-ddTHH:mm:ssZ», как у самих «Параметров». Снимок
    /// реестра перед правкой вернёт прежние значения, а «resume» снимает паузу.
    /// </remarks>
    public static string PauseScript(int days, DateTime utcNow)
    {
        days = Math.Clamp(days, 1, MaxPauseDays);
        var start = utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var end = utcNow.AddDays(days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $key = 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
            if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
            foreach ($pair in @(
              @('PauseUpdatesStartTime', '{{start}}'), @('PauseUpdatesExpiryTime', '{{end}}'),
              @('PauseFeatureUpdatesStartTime', '{{start}}'), @('PauseFeatureUpdatesEndTime', '{{end}}'),
              @('PauseQualityUpdatesStartTime', '{{start}}'), @('PauseQualityUpdatesEndTime', '{{end}}'))) {
              Set-ItemProperty -Path $key -Name $pair[0] -Value $pair[1] -Type String
            }
            "PAUSED_UNTIL: {{end}}"
            """;
    }

    public static string ResumeScript() => """
        $ErrorActionPreference = 'Stop'
        $key = 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
        foreach ($name in @('PauseUpdatesStartTime','PauseUpdatesExpiryTime','PauseFeatureUpdatesStartTime',
                            'PauseFeatureUpdatesEndTime','PauseQualityUpdatesStartTime','PauseQualityUpdatesEndTime')) {
          Remove-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue
        }
        'RESUMED'
        """;

    /// <summary>Номера без «KB» — так их хранит <c>KBArticleIDs</c>.</summary>
    private static string KbList(IReadOnlyList<string> kbs) =>
        string.Join(",", kbs.Select(kb => "'" + kb + "'"));

    [GeneratedRegex(@"^(?:KB)?(\d{6,8})$", RegexOptions.IgnoreCase)]
    private static partial Regex Kb();
}
