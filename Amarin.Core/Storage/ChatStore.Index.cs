using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;
public sealed partial class ChatStore
{
    private void UpsertIndex(ChatSession session)
    {
        // Вне замка хранилища: цена считается под замком сессии, а брать их в обратном порядке
        // где-то ещё было бы приглашением к взаимной блокировке.
        var cost = ChatCost.Total(session);
        // У очень старых переписок даты начала нет — берём последнюю: иначе дозаполнение
        // описи (BackfillIndex) перечитывало бы такой чат на каждом запуске.
        var created = session.CreatedAt == default ? session.UpdatedAt : session.CreatedAt;
        lock (_gate)
        {
            var index = LoadIndexLocked();
            var entry = index.Items.FirstOrDefault(item => item.Id == session.Id);
            if (entry is null)
            {
                entry = new ChatIndexEntry { Id = session.Id };
                index.Items.Add(entry);
            }
            else if (entry.Title == session.Title &&
                     entry.UpdatedAt == session.UpdatedAt &&
                     entry.Summary == session.Summary &&
                     entry.CreatedAt == created &&
                     entry.TotalCost == cost)
            {
                // Опись уже описывает этот чат верно. Прежде она перезаписывалась при каждом
                // сохранении, то есть дважды в секунду на протяжении всего ответа.
                return;
            }

            entry.Title = session.Title;
            entry.UpdatedAt = session.UpdatedAt;
            entry.Summary = session.Summary;
            entry.CreatedAt = created;
            entry.TotalCost = cost;
            SaveIndexLocked(index);
        }
    }

    /// <summary>
    /// Опись чатов читается — или её ещё нет. False — файл есть, но не прочитался (занят, повреждён,
    /// зашифрован чужим ключом).
    /// </summary>
    /// <remarks>
    /// Обычная загрузка молча читает такую опись как пустую, и первая же запись положила бы на её
    /// место опись из одного чата: в профиле пропал бы список всех остальных. Кто пишет в чужой,
    /// не открытый сейчас профиль (перенос чата), обязан спросить это раньше.
    /// </remarks>
    internal bool IndexReadable()
    {
        if (!File.Exists(_indexFile))
        {
            return true;
        }

        try
        {
            return ReadText(_indexFile) is { } text && JsonSerializer.Deserialize<ChatIndex>(text, AppJson.Options) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private ChatIndex LoadIndexLocked()
    {
        if (_index is not null)
        {
            return _index;
        }

        if (!File.Exists(_indexFile))
        {
            return _index = new ChatIndex();
        }

        try
        {
            var text = ReadText(_indexFile);
            _index = text is null
                ? new ChatIndex()
                : JsonSerializer.Deserialize<ChatIndex>(text, AppJson.Options) ?? new ChatIndex();
        }
        catch
        {
            _index = new ChatIndex();
        }

        return _index;
    }

    private void SaveIndexLocked(ChatIndex index)
    {
        _index = index;
        _sorted = null;
        _indexDirty = true;
    }

    /// <summary>Текст описи для записи — в порядке показа, как и прежде. Под замком хранилища.</summary>
    private string SerializeIndexLocked()
    {
        var index = LoadIndexLocked();
        index.Items = [.. Sort(index.Items)];
        return JsonSerializer.Serialize(index, AppJson.Options) + Environment.NewLine;
    }

    private static List<ChatIndexEntry> Sort(IEnumerable<ChatIndexEntry> items) =>
    [
        .. items
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.UpdatedAt)
    ];
}
