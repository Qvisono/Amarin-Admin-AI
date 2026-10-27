using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Чего браузер агента не делает никогда (1.37.0) — по образцу <see cref="ComputerSafety"/>.
/// </summary>
/// <remarks>
/// Адрес выбирает модель, а страница может её подговорить: только http и https и ничего на этой
/// машине и в локальной сети (<see cref="RemoteImages.IsSafeTarget(Uri)"/>) — иначе браузер стал
/// бы подделкой запросов в домашнюю сеть человека. Тем же правилом окно режет и переходы, которые
/// начала сама страница. Пароль агент не вводит: вход на сайт делает человек (<c>handoff</c>).
/// Тексты отказов читает модель, поэтому в них сказано, что делать дальше.
/// </remarks>
internal static class BrowserSafety
{
    /// <summary>Клавиши, которые агент нажимает в странице.</summary>
    public static readonly FrozenSet<string> Keys = new[]
    {
        "enter", "tab", "escape", "backspace", "delete", "space",
        "up", "down", "left", "right", "pageup", "pagedown", "home", "end"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Можно ли открыть адрес. Нельзя — отказ для модели.</summary>
    public static bool TryOpen(string? url, [NotNullWhen(true)] out Uri? target, [NotNullWhen(false)] out string? refusal)
    {
        target = null;
        refusal = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            refusal = "url must be a full web address that starts with https:// or http://.";
            return false;
        }

        if (!RemoteImages.IsSafeTarget(uri))
        {
            refusal = $"The browser does not open {uri.Scheme}://{uri.Host}: only web sites on the internet, never " +
                      "files, this computer or the local network. For files and this computer use your other tools. " +
                      "Do not retry this address.";
            return false;
        }

        target = uri;
        return true;
    }

    /// <summary>Пускает ли окно переход, начатый страницей или человеком.</summary>
    public static bool AllowsNavigation(Uri uri) =>
        uri.Scheme is "about" or "data" or "blob" || RemoteImages.IsSafeTarget(uri);

    /// <summary>Жёсткий запрет шлюза: адрес <c>open</c> проверяется до вопроса и SynGuard.</summary>
    public static bool TryGetHardBlockReason(JsonElement arguments, [NotNullWhen(true)] out string? reason)
    {
        reason = null;
        var action = FileToolPaths.String(arguments, "action");
        var url = FileToolPaths.String(arguments, "url");

        // read с адресом открывает его (BrowserTool) — и проверяется так же, как open.
        return (action == "open" || (action == "read" && !string.IsNullOrWhiteSpace(url))) &&
               !TryOpen(url, out _, out reason);
    }

    /// <summary>Отказ ввода: поле пароля.</summary>
    public static string? TypeRefusal(BrowserElement element) =>
        element.Password
            ? "This is a password field: the browser never types passwords. Ask the person to sign in themselves: " +
              "call the browser with action=handoff and a caption that says what to do, then continue."
            : null;

    /// <summary>Отказ, когда рядом нет человека, а действие ждёт его.</summary>
    public const string NobodyThere =
        "Nobody is at the computer during this run, so nobody can sign in or press Done. Finish without this step " +
        "and say in your report what the person has to do in the agent browser.";

    /// <summary>Человек закрыл окно браузера.</summary>
    public const string ClosedByHuman =
        "The person closed the agent browser window. Do not open it again in this run: report what is done and what is left.";

    /// <summary>Браузер занят другим агентом.</summary>
    public const string Busy =
        "The agent browser is in use by another agent right now. Do the task without the browser or report that it has to wait.";
}
