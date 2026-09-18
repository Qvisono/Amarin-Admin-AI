using Amarin.Core;

namespace Amarin.UI;

internal sealed class AppServices : IDisposable
{
    public required AgentOptions Options { get; init; }

    // Settable, not init-only: switching profiles re-roots both stores in place.
    public required AppSettingsStore SettingsStore { get; set; }

    public required AppSettings Settings { get; set; }

    public required ChatStore ChatStore { get; set; }

    /// <summary>Заготовки основного промпта. Тоже на профиль — как и сам основной промпт.</summary>
    public required PromptLibrary Prompts { get; set; }

    /// <summary>Ключи Venice активного профиля.</summary>
    public required VeniceKeyStore KeyStore { get; set; }

    /// <summary>
    /// Собственный журнал трат. Init-only: ссылка на него роздана всем копиям
    /// <see cref="AgentOptions"/>, подменять надо корень внутри, а не сам объект.
    /// </summary>
    public required SpendLedger Ledger { get; init; }

    /// <summary>
    /// Ключ, которым платят прямо сейчас. Общий на программу и на все копии
    /// <see cref="AgentOptions"/>, поэтому init-only: подменять надо содержимое, а не сам объект.
    /// </summary>
    public required VeniceKeyProvider Keys { get; init; }

    public required ProfileStore Profiles { get; init; }

    public required ProfileRegistry ProfileRegistry { get; set; }

    public required HttpClient Http { get; init; }

    public required HttpClient DownloadHttp { get; init; }

    public required VeniceClient Venice { get; init; }

    public required VeniceModelListCache Models { get; init; }

    public required ChatEngine Chat { get; init; }

    public required ChatTitleGenerator Titles { get; init; }

    public required ChatSummaryGenerator Summaries { get; init; }

    public required ConfirmationQueue Confirmations { get; init; }

    public string? StartupPrompt { get; init; }

    /// <summary>Ключ из VENICE_API_KEY — он общий для всех профилей и не меняется на ходу.</summary>
    public required string EnvironmentKey { get; init; }

    public void ReloadSettings() => Settings = SettingsStore.Load();

    /// <summary>
    /// Разовый перенос цен, уже записанных в переписках, в журнал трат.
    /// </summary>
    /// <remarks>
    /// График показывает, сколько ушло с ключа, а не сколько лежит на диске: удалённая переписка
    /// денег не возвращает. Живой учёт от чатов и не зависит — он пишет в <c>usage/</c> в момент
    /// списания, — а вот этот перенос читает их файлы, и раньше его делала только страница
    /// «Key &amp; Info» по первому заходу. Пока туда не зашли, цены старых ответов лежали лишь
    /// в самих переписках, и удалённый до первого захода чат уносил свои деньги с графика
    /// навсегда. Поэтому перенос делается на запуске, не дожидаясь, что человек откроет страницу.
    /// <para>
    /// Ходит по всем файлам чатов, поэтому зовётся из фонового потока. Отметка в журнале
    /// закрывает эту дверь навсегда — обход случается ровно один раз за жизнь профиля.
    /// </para>
    /// </remarks>
    public void BackfillSpendLedger()
    {
        try
        {
            Ledger.Backfill(KeyStore.ActiveSecret(), ReadSavedSessions());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Перенос — удобство, а не обязанность: не вышло сейчас, попробуем при заходе
            // на страницу трат.
        }
    }

    private IEnumerable<ChatSession> ReadSavedSessions()
    {
        foreach (var entry in ChatStore.List())
        {
            if (ChatStore.TryLoad(entry.Id) is { } session)
            {
                yield return session;
            }
        }
    }

    /// <summary>
    /// Переносит выбор человека из хранилища в держатель ключа и заодно обновляет список
    /// секретов, которые вырезаются из отчёта об аварии.
    /// </summary>
    public void ApplyActiveKey()
    {
        Keys.Use(KeyStore.ActiveSecret());
        CrashHandler.Secrets = KeyStore.AllSecrets();
    }

    /// <summary>
    /// Points the settings and chat stores at another profile's directory. The engine keeps
    /// reading settings through the same <c>Func&lt;AppSettings&gt;</c>, so nothing else has
    /// to be rebuilt — but the caller must persist the current chat first.
    /// </summary>
    public void UseProfile(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        // Прежнее хранилище пишет в фоне, а через мгновение на него уже никто не сошлётся:
        // всё, что оно не успело положить на диск, пропало бы вместе с ним.
        ChatStore.Flush();

        Directory.CreateDirectory(Path.Combine(dataRoot, "chats"));
        SettingsStore = new AppSettingsStore(dataRoot);
        ChatStore = new ChatStore(dataRoot);
        Prompts = new PromptLibrary(dataRoot);
        Settings = SettingsStore.Load();

        // Ключи у профиля свои, поэтому вместе с настройками переезжает и хранилище: иначе
        // человек, сменивший профиль, продолжал бы платить чужим ключом.
        KeyStore = new VeniceKeyStore(dataRoot, EnvironmentKey);
        KeyStore.Load();
        Ledger.UseRoot(dataRoot);
        ApplyActiveKey();
    }

    public void Dispose()
    {
        Http.Dispose();
        DownloadHttp.Dispose();
    }
}
