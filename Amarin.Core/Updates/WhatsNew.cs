namespace Amarin.Core;

/// <summary>
/// «Что нового» после обновления (H4): заметки к версии показываются один раз.
/// </summary>
/// <remarks>
/// Заметки вшиты в exe ресурсом из <c>.github/release-notes/v&lt;версия&gt;.md</c> — тем же файлом,
/// из которого <c>release.yml</c> делает страницу релиза. Сеть для этого не нужна: окно
/// показывается при первом запуске новой версии, а GitHub в этот момент может и не отвечать.
/// </remarks>
internal static class WhatsNew
{
    /// <summary>Имя ресурса — задано в csproj (<c>LogicalName</c>).</summary>
    public const string ResourceName = "Amarin.ReleaseNotes.md";

    /// <summary>
    /// Показывать ли заметки этого запуска.
    /// </summary>
    /// <param name="lastSeen">Версия, чьи заметки человек уже видел (<see cref="AppSettings.LastSeenVersion"/>).</param>
    /// <param name="usedBefore">
    /// Программой пользовались до этого запуска. Нужен, когда <paramref name="lastSeen"/> пуст:
    /// так выглядит и новая установка, где заметки к только что скачанному ни к чему, и первое
    /// обновление на версию, которая это поле завела, — там они как раз к месту.
    /// </param>
    public static bool ShouldShow(string? lastSeen, ReleaseVersion current, bool usedBefore) =>
        ReleaseVersion.Parse(lastSeen) is { } seen ? current > seen : usedBefore;

    /// <summary>Заметки, вшитые в эту сборку; <c>null</c> — их нет (сборка без файла заметок).</summary>
    public static string? Embedded()
    {
        using var stream = typeof(WhatsNew).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return UpdateChecker.TrimNotes(reader.ReadToEnd());
    }
}
