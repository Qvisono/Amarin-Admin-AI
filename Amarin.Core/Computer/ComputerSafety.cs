using System.Collections.Frozen;

namespace Amarin.Core;

/// <summary>
/// Что модель не делает мышью и клавиатурой, о чём бы её ни попросили и что бы ни ответил
/// SynGuard. Каждый отказ — строка для модели с тем, что делать дальше.
/// </summary>
/// <remarks>
/// <para>
/// Окна самой программы — главное: иначе агент мог бы ответить «Да» на собственный вопрос об
/// опасном действии или выключить себе защиту на странице «Безопасность». Плашка, Роза и
/// карточки — тот же процесс, поэтому правило по процессу, а не по заголовку.
/// </para>
/// <para>
/// Консоль, «Выполнить» и командная строка в любом поле — потому что команды разбирает шлюз
/// только у <c>run_powershell</c>. Набранная мышью команда прошла бы мимо разбора, мимо
/// жёстких запретов и мимо «только чтения» расписания.
/// </para>
/// <para>
/// Повышенное окно, вход в Windows и вопрос UAC Windows и так не пускает: ввод от обычного
/// процесса туда не доходит. Отказ здесь — чтобы модель узнала об этом сразу и понятно, а не
/// кликала в пустоту, считая, что нажала.
/// </para>
/// </remarks>
internal static class ComputerSafety
{
    /// <summary>Программы, первым словом которых начинается командная строка.</summary>
    private static readonly FrozenSet<string> Launchers = new[]
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec",
        "bash", "wsl", "sh", "certutil", "bitsadmin", "schtasks", "reg", "sc", "net", "net1", "netsh",
        "wmic", "bcdedit", "diskpart", "vssadmin", "taskkill", "shutdown", "curl", "wget", "python",
        "python3", "py", "node", "%comspec%"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Executable = [".exe", ".com", ".bat", ".cmd"];

    /// <summary>Открыт ли сеанс человека. Null — да.</summary>
    public static string? Desk(ComputerDesk desk) => desk switch
    {
        ComputerDesk.Locked => Loc.Get("S.Computer.Locked"),
        ComputerDesk.Secure => Loc.Get("S.Computer.SecureDesk"),
        _ => null
    };

    /// <summary>Можно ли нажимать и печатать в это окно. Null — можно.</summary>
    /// <param name="ownProcessId">Свой процесс; параметром — ради тестов.</param>
    public static string? Target(ComputerWindow? window, int ownProcessId)
    {
        if (window is null)
        {
            return Loc.Get("S.Computer.NoWindow");
        }

        if (window.ProcessId == ownProcessId)
        {
            return Loc.Get("S.Computer.Own");
        }

        if (window.Elevated)
        {
            return Loc.Format("S.Computer.Elevated", Title(window));
        }

        return window.Kind is ComputerWindowKind.Console or ComputerWindowKind.RunDialog
            ? Loc.Format("S.Computer.Console", Title(window))
            : null;
    }

    /// <summary>Можно ли напечатать этот текст в это поле. Null — можно.</summary>
    public static string? Typing(ComputerWindow? window, ComputerElement? field, string text, int ownProcessId)
    {
        if (Target(window, ownProcessId) is { } refusal)
        {
            return refusal;
        }

        if (field is { IsPassword: true })
        {
            return Loc.Get("S.Computer.Password");
        }

        return IsCommandLine(text) ? Loc.Get("S.Computer.CommandText") : null;
    }

    /// <summary>Можно ли нажать это сочетание, когда впереди это окно. Null — можно.</summary>
    public static string? Keys(ComputerWindow? window, IReadOnlyList<string> chord, int ownProcessId)
    {
        // До окна: «Выполнить» и меню Win+X открываются поверх любого окна.
        if (chord.Contains(ComputerKeys.Win) && chord[^1] is "r" or "x")
        {
            return Loc.Format("S.Computer.RunKeys", ComputerKeys.Join(chord));
        }

        if (Target(window, ownProcessId) is { } refusal)
        {
            return refusal;
        }

        var deletes = chord[^1] == "delete" ||
                      (chord.Count == 2 && chord[0] == ComputerKeys.Ctrl && chord[1] == "d");
        return deletes && window is { Kind: ComputerWindowKind.Explorer or ComputerWindowKind.FileDialog }
            ? Loc.Get("S.Computer.Delete")
            : null;
    }

    /// <summary>Можно ли вставить буфер обмена этим сочетанием. Null — можно.</summary>
    /// <remarks>
    /// У агента есть и <c>write_clipboard</c>, и клавиатура: командная строка, положенная в буфер
    /// и вставленная, прошла бы мимо запрета печатать её (<see cref="Typing"/>). Буфер читается,
    /// только когда сочетание правда вставляет.
    /// </remarks>
    public static string? Paste(IReadOnlyList<string> chord, Func<string?> clipboard)
    {
        var pastes = chord.Count == 2 &&
                     ((chord[0] == ComputerKeys.Ctrl && chord[1] == "v") || (chord[0] == ComputerKeys.Shift && chord[1] == "insert"));
        var plain = chord.Count == 3 && chord[0] == ComputerKeys.Ctrl && chord[1] == ComputerKeys.Shift && chord[2] == "v";
        return (pastes || plain) && IsCommandLine(clipboard()) ? Loc.Get("S.Computer.Paste") : null;
    }

    /// <summary>
    /// Похож ли текст на командную строку: первое слово — программа, которая исполняет команды,
    /// а за ним ключ, путь или адрес.
    /// </summary>
    /// <remarks>
    /// Только с аргументом: само имя программы в поиске меню «Пуск» или в браузере — не команда.
    /// Открытая так консоль всё равно закрыта для ввода (<see cref="Target"/>).
    /// </remarks>
    public static bool IsCommandLine(string? text)
    {
        var words = (text ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return false;
        }

        var first = words[0].Trim('"', '\'', '&', '(', ')');
        var slash = first.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            first = first[(slash + 1)..];
        }

        foreach (var extension in Executable)
        {
            if (first.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                first = first[..^extension.Length];
                break;
            }
        }

        if (!Launchers.Contains(first))
        {
            return false;
        }

        return words.Skip(1).Any(static word =>
            (word.Length > 1 && word[0] is '-' or '/') ||
            word.Contains('\\', StringComparison.Ordinal) ||
            word.Contains(':', StringComparison.Ordinal) ||
            word.Contains('.', StringComparison.Ordinal));
    }

    private static string Title(ComputerWindow window) =>
        string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;
}
