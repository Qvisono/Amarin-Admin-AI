using System.Text;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>Ссылка на новое issue и то, что не влезло в неё и должно уйти в буфер обмена.</summary>
/// <param name="ClipboardBody">Полный текст, если в ссылку он не поместился; <c>null</c> — поместился.</param>
internal sealed record BugReportLink(string Url, string? ClipboardBody);

/// <summary>
/// «Сообщить об ошибке» (H5): issue на GitHub с уже заполненным отчётом.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт уходит в открытый браузер и дальше — в публичный трекер, поэтому чистится ещё раз поверх
/// <see cref="CrashReport.Scrub"/>: путь профиля Windows (в стеке он есть почти всегда —
/// <c>C:\Users\Имя\AppData\…</c>), имя пользователя и имя компьютера. Ключи отчёт уже не несёт,
/// но проход по <c>Bearer</c> повторяется — журнал мог писать и старый код.
/// </para>
/// <para>
/// Отправляет человек, не программа: ссылка только открывает форму, где всё видно до кнопки
/// «Submit». Длина ссылки у GitHub ограничена (около 8 КБ, длиннее — ошибка страницы), поэтому
/// длинный отчёт кладётся в буфер обмена, а в ссылке остаётся просьба его вставить.
/// </para>
/// </remarks>
internal static class BugReport
{
    /// <summary>Предел длины ссылки. Больше — тело уходит в буфер обмена.</summary>
    public const int MaxUrlLength = 8000;

    /// <summary>Заменитель имени пользователя и компьютера в отчёте.</summary>
    public const string UserMark = "<user>";

    public const string MachineMark = "<pc>";

    /// <summary>Что остаётся в ссылке, когда отчёт уехал в буфер.</summary>
    public const string PasteNote =
        "The full report did not fit into the link and has been copied to the clipboard. Please paste it here (Ctrl+V).";

    private const string CrashHeader = "=== Amarin Admin AI - ";

    /// <summary>
    /// Убирает из текста то, что указывает на человека: путь профиля, имя пользователя, имя
    /// компьютера. Имена короче трёх знаков не трогаются — такая замена резала бы обычные слова.
    /// </summary>
    public static string Sanitize(string? text, string? userProfile, string? userName, string? machineName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var profile = userProfile.TrimEnd('\\', '/');
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        text = ReplaceWord(text, userName, UserMark);
        text = ReplaceWord(text, machineName, MachineMark);
        return CrashReport.Scrub(text, []);
    }

    /// <summary>То же с данными этой машины.</summary>
    public static string SanitizeHere(string? text) =>
        Sanitize(
            text,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.UserName,
            Environment.MachineName);

    /// <summary>Заголовок issue по отчёту об аварии: тип исключения и начало сообщения.</summary>
    public static string TitleFrom(string? report)
    {
        string? type = null;
        string? message = null;
        foreach (var raw in (report ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (type is null && line.StartsWith("Исключение: ", StringComparison.Ordinal))
            {
                type = line["Исключение: ".Length..].Split('.')[^1];
            }
            else if (type is not null && message is null && line.StartsWith("Сообщение: ", StringComparison.Ordinal))
            {
                message = line["Сообщение: ".Length..];
            }
        }

        var title = type is null
            ? "Crash"
            : "Crash: " + type + (string.IsNullOrWhiteSpace(message) ? "" : " — " + message);
        return title.Length <= 120 ? title : title[..117].TrimEnd() + "…";
    }

    /// <summary>Тело issue: место для рассказа, сведения о среде и отчёт в блоке кода.</summary>
    public static string Body(string version, string? report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### What happened").AppendLine();
        sb.AppendLine("<!-- What were you doing when it went wrong? -->").AppendLine();
        sb.AppendLine("### Environment").AppendLine();
        sb.Append("- Version: ").AppendLine(version);
        sb.Append("- Windows: ").AppendLine(Environment.OSVersion.VersionString);
        sb.Append("- Process: ").AppendLine(Environment.Is64BitProcess ? "x64" : "x86");

        if (!string.IsNullOrWhiteSpace(report))
        {
            // Забор длиннее любой серии обратных кавычек в самом отчёте — иначе строка из стека
            // закрыла бы блок кода посреди текста.
            var fence = new string('`', Math.Max(3, LongestRun(report, '`') + 1));
            sb.AppendLine().AppendLine("### Report").AppendLine();
            sb.Append(fence).AppendLine("text");
            sb.AppendLine(report.TrimEnd());
            sb.AppendLine(fence);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Ссылка на форму нового issue. Не влезает — тело уходит в <see cref="BugReportLink.ClipboardBody"/>,
    /// а в ссылке остаётся <see cref="PasteNote"/>.
    /// </summary>
    public static BugReportLink Link(string title, string body, int limit = MaxUrlLength)
    {
        var url = Compose(title, body);
        return url.Length <= limit
            ? new BugReportLink(url, null)
            : new BugReportLink(Compose(title, PasteNote), body);
    }

    /// <summary>Последний отчёт из журнала аварий; <c>null</c> — журнала нет или он пуст.</summary>
    public static string? LastCrash(string? log)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return null;
        }

        var start = log.LastIndexOf(CrashHeader, StringComparison.Ordinal);
        var last = (start >= 0 ? log[start..] : log).Trim();
        return last.Length == 0 ? null : last;
    }

    private static string Compose(string title, string body) =>
        UpdateChecker.RepositoryUrl + "/issues/new?title=" + Uri.EscapeDataString(title ?? "") +
        "&body=" + Uri.EscapeDataString(body ?? "");

    private static string ReplaceWord(string text, string? word, string mark)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Trim().Length < 3)
        {
            return text;
        }

        // Границы — не буквы и не цифры: «ivan» в «C:\Users\ivan\» заменяется, в «ivanov» — нет.
        var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(word.Trim()) + @"(?![\p{L}\p{N}])";
        return Regex.Replace(text, pattern, mark, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static int LongestRun(string text, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        return best;
    }
}
