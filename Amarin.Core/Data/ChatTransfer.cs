using System.Text.Json;

namespace Amarin.Core;

/// <summary>Чем кончилась отправка одной переписки в другой профиль.</summary>
internal enum ChatTransferResult
{
    /// <summary>Переписка легла в профиль, и он её видит в своём списке.</summary>
    Sent,

    /// <summary>Профиля больше нет (удалили, пока было открыто меню).</summary>
    NoProfile,

    /// <summary>Список чатов того профиля не читается — писать туда нельзя, иначе он бы пропал.</summary>
    TargetUnreadable,

    /// <summary>Переписку не удалось прочитать здесь или записать там — в профиле её нет.</summary>
    Failed
}

/// <summary>Итог по одной переписке: откуда она и под каким идентификатором легла там.</summary>
internal readonly record struct ChatTransferOutcome(string SourceId, ChatTransferResult Result, string? ChatId)
{
    public bool Sent => Result == ChatTransferResult.Sent;
}

/// <summary>Переписка для отправки: как её прочитать и что человек в ней недописал.</summary>
/// <param name="Load">Чтение — уже в фоне: с диска для закрытого чата, живая сессия для открытого.</param>
/// <param name="Draft">Неотправленное в поле ввода; при переносе уезжает вместе с перепиской.</param>
internal sealed record ChatTransferItem(string SourceId, Func<ChatSession?> Load, ChatDraftContent? Draft = null);

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
/// Пакет пишется одним хранилищем и одной записью описи, а проверяется свежим хранилищем на той
/// же папке: переписка считается перенесённой, только если она есть и файлом, и строкой в
/// списке того профиля. До 1.33.0 каждая переписка открывала своё хранилище, переписывала опись
/// и ждала диск на потоке окна — триста выбранных чатов замораживали окно надолго, а опись,
/// которая не прочиталась, молча заменялась описью из одного чата.
/// </para>
/// <para>
/// То, что принадлежит профилю, а не переписке, в копию не едет: машина чата (C10), ключ чата,
/// отметка задачи расписания, выбор инструкций (у того профиля своя библиотека — чужие
/// идентификаторы оставили бы чат вовсе без инструкций). Папки и теги тоже не едут: раскладка у
/// профиля своя. Время изменения — нынешнее: переписка встаёт наверх списка того профиля и не
/// попадает сразу под его правило хранения старых чатов.
/// </para>
/// </remarks>
internal static class ChatTransfer
{
    /// <param name="targetRoot">Папка данных профиля-получателя (<see cref="ProfileStore.DataRootFor"/>).</param>
    /// <param name="keepIds">
    /// Сохранить идентификаторы, если там они свободны, — это перенос: ссылки на чат (задачи,
    /// отчёты) остаются верны. Копия всегда получает новый: отправленная дважды переписка —
    /// две переписки, и вторая не затирает первую, которую там уже могли продолжить.
    /// </param>
    public static IReadOnlyList<ChatTransferOutcome> Send(
        IReadOnlyList<ChatTransferItem> items,
        string targetRoot,
        bool keepIds,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrWhiteSpace(targetRoot) || !Directory.Exists(targetRoot))
        {
            return [.. items.Select(item => new ChatTransferOutcome(item.SourceId, ChatTransferResult.NoProfile, null))];
        }

        var encrypt = DataBundleImporter.EncryptsAtRest(targetRoot);
        var target = new ChatStore(targetRoot) { Encrypt = () => encrypt };
        if (!target.IndexReadable())
        {
            return [.. items.Select(item => new ChatTransferOutcome(item.SourceId, ChatTransferResult.TargetUnreadable, null))];
        }

        var taken = target.List().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var drafts = new DraftStore(targetRoot, () => encrypt);
        var written = new List<(string SourceId, string ChatId)>();
        var outcomes = new List<ChatTransferOutcome>(items.Count);
        foreach (var item in items)
        {
            if (Prepare(item, keepIds, taken, now) is not { } copy)
            {
                outcomes.Add(new ChatTransferOutcome(item.SourceId, ChatTransferResult.Failed, null));
                continue;
            }

            try
            {
                target.Save(copy);
                if (item.Draft is { IsEmpty: false } draft)
                {
                    drafts.Save(copy.Id, draft);
                }

                taken.Add(copy.Id);
                written.Add((item.SourceId, copy.Id));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                outcomes.Add(new ChatTransferOutcome(item.SourceId, ChatTransferResult.Failed, null));
            }
        }

        try
        {
            target.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Что успело лечь, покажет сверка ниже.
        }

        // Сверка свежим хранилищем: фоновая запись глотает ошибки и пробует позже, а этого
        // «позже» у временного хранилища не будет. Переписка без строки в описи там невидима.
        var check = new ChatStore(targetRoot) { Encrypt = () => encrypt };
        var listed = check.List().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (sourceId, chatId) in written)
        {
            var landed = listed.Contains(chatId) && check.TryLoad(chatId) is not null;
            outcomes.Add(new ChatTransferOutcome(sourceId, landed ? ChatTransferResult.Sent : ChatTransferResult.Failed, landed ? chatId : null));
        }

        return outcomes;
    }

    /// <summary>Копия переписки для того профиля; null — прочитать или скопировать не вышло.</summary>
    private static ChatSession? Prepare(ChatTransferItem item, bool keepIds, HashSet<string> taken, DateTime now)
    {
        ChatSession? copy;
        try
        {
            copy = item.Load() is { } session ? Snapshot(session) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            // Список переписки поменялся под сериализацией или файл не прочитался — эта
            // переписка не уезжает, остальные пакета едут.
            return null;
        }

        if (copy is null)
        {
            return null;
        }

        if (!keepIds || taken.Contains(copy.Id))
        {
            copy.Id = Guid.NewGuid().ToString("N");
        }

        copy.UpdatedAt = now;
        if (copy.Profile is { InstructionIds: not null } profile)
        {
            profile.InstructionIds = null;
            if (profile.IsEmpty)
            {
                copy.Profile = null;
            }
        }

        return copy;
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
