using System.Text.Json;

namespace Amarin.Core;

/// <summary>Чем кончилась отправка чата в другой профиль.</summary>
internal enum ChatTransferResult
{
    /// <summary>Переписка легла в профиль.</summary>
    Sent,

    /// <summary>Профиля больше нет (удалили, пока было открыто меню).</summary>
    NoProfile,

    /// <summary>Диск не записал или запись не прочиталась обратно — в профиле ничего не появилось.</summary>
    Failed
}

/// <summary>Итог отправки: что вышло и под каким идентификатором переписка лежит там.</summary>
internal readonly record struct ChatTransferOutcome(ChatTransferResult Result, string? ChatId)
{
    public bool Sent => Result == ChatTransferResult.Sent;
}

/// <summary>
/// «Отправить» и «Переместить» в меню чата (1.33.0): переписка ложится в другой профиль программы.
/// </summary>
/// <remarks>
/// <para>
/// Пароль чужого профиля не спрашивается — тем же рассуждением, что у ключей
/// (<see cref="ApiKeyTransfer"/>): пароль закрывает вход в окно, а файлы всех профилей лежат в
/// папке одного пользователя Windows. Чужой профиль сейчас не открыт, поэтому в его папку пишет
/// своё, отдельное хранилище, и шифрует оно по настройке того профиля, а не этого: открытый
/// там чат иначе оказался бы открытым текстом среди зашифрованных.
/// </para>
/// <para>
/// Отправляется копия, а не тот же объект: идущий ход дописывает в живую сессию, и хранилище
/// другого профиля не должно получить её посреди правки. Копия снимается под замком сессии.
/// </para>
/// <para>
/// То, что принадлежит профилю, а не переписке, в копию не едет: машина чата (C10) — в
/// другом профиле её нет, и команды чата молча пошли бы на этот ПК вместо удалённого; ключ
/// чата — у профиля свои ключи, и чужой идентификатор там ничего не значит; отметка задачи
/// расписания — расписание тоже своё. Папки и теги тоже не едут: раскладка у профиля своя.
/// </para>
/// </remarks>
internal static class ChatTransfer
{
    /// <param name="session">Переписка; для открытого чата — живая сессия, а не копия с диска.</param>
    /// <param name="targetRoot">Папка данных профиля-получателя (<see cref="ProfileStore.DataRootFor"/>).</param>
    /// <param name="keepId">
    /// Сохранить идентификатор, если там он свободен, — это перенос: ссылки на чат (задачи,
    /// отчёты) остаются верны. Копия всегда получает новый: отправленная дважды переписка —
    /// две переписки, и вторая не затирает первую, которую там уже могли продолжить.
    /// </param>
    public static ChatTransferOutcome Send(ChatSession session, string targetRoot, bool keepId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(targetRoot) || !Directory.Exists(targetRoot))
        {
            return new ChatTransferOutcome(ChatTransferResult.NoProfile, null);
        }

        var copy = Snapshot(session);
        if (copy is null)
        {
            return new ChatTransferOutcome(ChatTransferResult.Failed, null);
        }

        var settings = new AppSettingsStore(targetRoot).Load();
        var target = new ChatStore(targetRoot) { Encrypt = () => settings.EncryptChats };
        if (!keepId || target.List().Any(entry => entry.Id == copy.Id) || target.TryLoad(copy.Id) is not null)
        {
            copy.Id = Guid.NewGuid().ToString("N");
        }

        try
        {
            target.Save(copy);
            target.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ChatTransferOutcome(ChatTransferResult.Failed, null);
        }

        // Сверка по диску, а не по отсутствию исключения: фоновая запись хранилища ошибки
        // глотает и пробует позже, а этого «позже» у временного хранилища не будет.
        return target.TryLoad(copy.Id) is null
            ? new ChatTransferOutcome(ChatTransferResult.Failed, null)
            : new ChatTransferOutcome(ChatTransferResult.Sent, copy.Id);
    }

    /// <summary>
    /// Глубокая копия переписки без того, что принадлежит этому профилю.
    /// </summary>
    /// <remarks>
    /// Через тот же JSON, которым чат ложится на диск: копия обязана быть ровно тем, что
    /// прочитает хранилище, а ручное копирование полей забыло бы новое поле, как уже бывало с
    /// копиями настроек. Ответы, застывшие на «пишется», закрываются сразу: продолжать их в
    /// другом профиле некому.
    /// </remarks>
    internal static ChatSession? Snapshot(ChatSession session)
    {
        string json;
        lock (session.Gate)
        {
            json = JsonSerializer.Serialize(session, AppJson.Options);
        }

        ChatSession? copy;
        try
        {
            copy = JsonSerializer.Deserialize<ChatSession>(json, AppJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (copy is null)
        {
            return null;
        }

        copy.TargetMachineId = null;
        copy.SelectedKeyId = null;
        copy.ScheduleJobId = null;
        _ = ChatEngine.CloseInterruptedReplies(copy);
        return copy;
    }
}
