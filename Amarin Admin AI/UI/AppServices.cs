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

    /// <summary>
    /// Инструкции пользователя. Init-only, как и журнал трат: ссылку на библиотеку держат движок
    /// чата и инструмент <c>read_instruction</c>, поэтому смена профиля переводит её на другую
    /// папку, а не подменяет объект.
    /// </summary>
    public required InstructionLibrary Instructions { get; init; }

    /// <summary>Ключи Venice активного профиля.</summary>
    public required ApiKeyStore KeyStore { get; set; }

    /// <summary>
    /// Собственный журнал трат. Init-only: ссылка на него роздана всем копиям
    /// <see cref="AgentOptions"/>, подменять надо корень внутри, а не сам объект.
    /// </summary>
    public required SpendLedger Ledger { get; init; }

    /// <summary>
    /// Ключ, которым платят прямо сейчас. Общий на программу и на все копии
    /// <see cref="AgentOptions"/>, поэтому init-only: подменять надо содержимое, а не сам объект.
    /// </summary>
    public required ApiKeyProvider Keys { get; init; }

    /// <summary>
    /// Остатки всех ключей. Одна книга на программу: её наполняют все клиенты, а складывает
    /// сумму плашка в композере.
    /// </summary>
    public required BalanceBook Balances { get; init; }

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

    /// <summary>
    /// То же для OPENROUTER_API_KEY. Без <c>required</c>: пустая строка — обычное положение
    /// дел, эту переменную заводят единицы.
    /// </summary>
    public string OpenRouterEnvironmentKey { get; init; } = "";

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
        // Отметка профиля, а не ключа: переписки общие, а какой ключ за них платил, в них не
        // записано. Пока отметка стояла у ключа, каждый заведённый позже ключ забирал себе всю
        // чужую историю, и графики двух ключей совпадали до цента.
        if (Settings.SpendBackfilledAt is not null)
        {
            return;
        }

        try
        {
            // Переписки отдаются ленивой последовательностью, а не списком: их бывают сотни,
            // и файл чата бывает в мегабайты — собрать их все в память разом дороже, чем
            // прочитать диск второй раз в единственном за всю жизнь профиля проходе.
            Ledger.RepairDuplicateBackfills(ReadSavedSessions());
            Ledger.Backfill(KeyStore.ActiveSecret(), ReadSavedSessions());

            var stamp = DateTime.Now;
            Settings.SpendBackfilledAt = stamp;

            // Через Update, а не Save: зовут это из фонового потока, и записать сюда свою копию
            // настроек целиком значило бы затереть то, что человек в это же время менял в окне.
            SettingsStore.Update(settings => settings.SpendBackfilledAt = stamp);
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
    /// <remarks>
    /// Единственная точка, через которую проходят все три способа сменить выбранный ключ:
    /// выбор кружком на странице, добавление (новый ключ сразу становится выбранным) и
    /// удаление (выбранным становится следующий годный). Вместе с выбранным сюда же едет
    /// и весь список: из него слоты моделей достают назначенные им ключи, и список обязан
    /// смениться тем же присваиванием — иначе слот успел бы найти уже удалённый ключ.
    /// <para>
    /// Моделей эта смена больше не касается. До версии 1.23.0 активный ключ задавал провайдера
    /// всем девяти слотам разом, и здесь же выбор прятался в тайник до возвращения прежнего
    /// ключа. Теперь провайдер живёт в самом идентификаторе модели, у каждого слота свой,
    /// и менять при смене ключа нечего.
    /// </para>
    /// </remarks>
    public void ApplyActiveKey()
    {
        // Вместе с выбранным — ключ Venice: рисование картинок и чтение страниц умеет только он,
        // и при выбранном ключе OpenRouter взять его больше неоткуда.
        Keys.Use(KeyStore.ActiveCredential(), KeyStore.VeniceCredential(), KeyStore.Handles());
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
        Instructions.UseRoot(dataRoot);
        Settings = SettingsStore.Load();

        // Ключи у профиля свои, поэтому вместе с настройками переезжает и хранилище: иначе
        // человек, сменивший профиль, продолжал бы платить чужим ключом.
        KeyStore = new ApiKeyStore(dataRoot, EnvironmentKey, OpenRouterEnvironmentKey);
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
