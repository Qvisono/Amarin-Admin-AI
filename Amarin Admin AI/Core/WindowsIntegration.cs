using Microsoft.Win32;

namespace Amarin.Core;

/// <summary>Как сообщать о готовом ответе, вопросе и находках расписания (G4).</summary>
public enum NotificationStyle
{
    /// <summary>Своя карточка в углу экрана — как было до 1.28.0.</summary>
    Card,

    /// <summary>Системное уведомление Windows через значок в трее.</summary>
    System
}

/// <summary>Интеграция с Windows (G1–G5): трей, уведомления, сочетания, автозапуск, Проводник.</summary>
public sealed class WindowsIntegrationSettings
{
    /// <summary>Значок в области уведомлений. Заводское — есть: он показывает, идёт ли работа.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// Крестик прячет окно в трей, а не закрывает программу. Отдельного «сворачивать в трей» нет:
    /// две галки про трей путали, а кнопке «свернуть» хватает обычного поведения Windows.
    /// </summary>
    public bool CloseToTray { get; set; }

    public NotificationStyle Notifications { get; set; } = NotificationStyle.Card;

    /// <summary>Запускаться со входом в Windows — сразу в трей.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Пункт «Спросить Amarin» в контекстном меню Проводника.</summary>
    public bool ExplorerMenu { get; set; }

    /// <summary>Глобальные сочетания: действие → «Win+Shift+A». Нет записи — заводское.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Глобальные сочетания (G2): работают, даже когда окно спрятано или не в фокусе.</summary>
internal static class GlobalHotkeys
{
    public const string ShowHide = "show-hide";
    public const string NewFromClipboard = "new-from-clipboard";
    public const string NewWithScreenshot = "new-with-screenshot";

    public static readonly string[] All = [ShowHide, NewFromClipboard, NewWithScreenshot];

    /// <summary>
    /// Заводское — только у «показать или спрятать». Предположение, что Windows это сочетание не
    /// занимает, проверяется при регистрации: занятое называется человеку как конфликт.
    /// </summary>
    public static string? Default(string action) => action == ShowHide ? "Win+Shift+A" : null;

    public static string? Effective(WindowsIntegrationSettings settings, string action) =>
        settings.Hotkeys.TryGetValue(action, out var gesture) ? (string.IsNullOrWhiteSpace(gesture) ? null : gesture) : Default(action);

    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    /// <summary>
    /// Разбирает «Ctrl+Alt+K» в модификаторы <c>RegisterHotKey</c> и код клавиши. Без модификатора
    /// не бывает: голая клавиша отняла бы букву у всех программ сразу.
    /// </summary>
    public static bool TryParse(string? gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                "win" or "windows" => ModWin,
                _ => 0x80000000
            };
        }

        if ((modifiers & 0x80000000) != 0 || modifiers == ModShift)
        {
            modifiers = 0;
            return false;
        }

        var key = parts[^1].ToUpperInvariant();
        virtualKey = key switch
        {
            _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => key[0],
            _ when key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24 => (uint)(0x6F + number),
            "SPACE" => 0x20,
            _ => 0
        };

        return virtualKey != 0;
    }
}

/// <summary>Автозапуск (G3): запись в <c>HKCU\…\Run</c>.</summary>
internal static class AutoStart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Amarin Admin AI";

    /// <summary>Сразу в трей: при входе в Windows окно поверх всего никому не нужно.</summary>
    public static string Command(string exePath) => "\"" + exePath + "\" --tray";

    /// <summary>
    /// Приводит запись к настройке. Путь сверяется на каждом старте: exe переносят и обновляют,
    /// и запись со старым путём молча перестала бы работать. Чужую запись с тем же именем
    /// выключение не трогает — только нашу.
    /// </summary>
    /// <returns>Удалось ли (реестр мог отказать).</returns>
    public static bool Apply(RegistryKey root, bool enabled, string exePath)
    {
        try
        {
            using var key = root.CreateSubKey(RunKey, writable: true);
            var current = key.GetValue(ValueName) as string;
            var wanted = Command(exePath);
            if (enabled)
            {
                if (!string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, wanted);
                }
            }
            else if (current is not null && current.Contains("--tray", StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Пункт «Спросить Amarin» в Проводнике (G5). В Windows 11 он окажется в «Показать
/// дополнительные параметры»: новое меню принимает только упакованные расширения.
/// </summary>
internal static class ExplorerMenu
{
    public const string Verb = "AmarinAsk";

    /// <summary>Куда пишется пункт и какой аргумент подставляет Проводник.</summary>
    public static readonly (string ClassPath, string Placeholder)[] Targets =
    [
        (@"Software\Classes\*\shell\" + Verb, "%1"),
        (@"Software\Classes\Directory\shell\" + Verb, "%1"),
        (@"Software\Classes\Directory\Background\shell\" + Verb, "%V")
    ];

    public static string Command(string exePath, string placeholder) =>
        "\"" + exePath + "\" --ask-path \"" + placeholder + "\"";

    public static bool Apply(RegistryKey root, bool enabled, string exePath, string label)
    {
        try
        {
            foreach (var (classPath, placeholder) in Targets)
            {
                if (!enabled)
                {
                    root.DeleteSubKeyTree(classPath, throwOnMissingSubKey: false);
                    continue;
                }

                using var verb = root.CreateSubKey(classPath, writable: true);
                verb.SetValue("MUIVerb", label);
                verb.SetValue("Icon", "\"" + exePath + "\",0");
                using var command = verb.CreateSubKey("command", writable: true);
                command.SetValue("", Command(exePath, placeholder));
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
