using System.Text;

namespace Amarin.Core;

/// <summary>Что в виде списка чатов зависит не от самих чатов: запрос, режим поиска, сортировка, фильтр, день.</summary>
/// <param name="ContentSearch">Состояние поиска «по смыслу» — числом: перечисление живёт в окне.</param>
/// <param name="Today">День: после полуночи «Сегодня» обязано стать «Вчера» и без правок.</param>
internal readonly record struct ChatListView(
    string Query,
    bool ByContent,
    int ContentSearch,
    ChatSort Sort,
    string? TagFilter,
    bool ArchiveExpanded,
    DateTime Today);

/// <summary>
/// Слепок состава списка чатов: совпал с прошлым — панель не пересобирается, а признаки на уже
/// стоящих строках правятся поштучно.
/// </summary>
/// <remarks>
/// <para>
/// Открытый чат, идущие ходы и метки «ответ готов» в слепок не входят намеренно — их и нет среди
/// входов: они правятся признаками строк, а не пересборкой панели. До 1.30.0 слепок собирало окно
/// из своих полей, и то, что переключение чата не пересобирает список, проверял оконный тест через
/// отражение.
/// </para>
/// <para>
/// Пишется в переданный <see cref="StringBuilder"/>, а не возвращает строку: список обновляют по
/// несколько раз в секунду во время ответа, и на тысяче чатов слепок — сотня килобайт мусора за раз.
/// </para>
/// </remarks>
internal static class ChatListSignature
{
    public static void Append(
        StringBuilder builder,
        ChatListView view,
        IReadOnlyList<ChatIndexEntry> items,
        ChatOrganizer.State organize)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(organize);

        builder.Append(view.Query)
            .Append('|').Append(view.ByContent ? '1' : '0')
            .Append('|').Append(view.ContentSearch)
            .Append('|').Append((int)view.Sort)
            .Append('|').Append(view.TagFilter)
            .Append('|').Append(view.ArchiveExpanded ? '1' : '0')
            .Append('|').Append(view.Today.Ticks);
        foreach (var folder in organize.Folders)
        {
            builder.Append("|f").Append(folder.Id).Append('~').Append(folder.Name).Append('~').Append(folder.Collapsed ? '1' : '0');
        }

        foreach (var tag in organize.Tags)
        {
            builder.Append("|t").Append(tag.Id).Append('~').Append(tag.Color);
        }

        foreach (var item in items)
        {
            builder.Append('|')
                .Append(item.Id).Append('~')
                .Append(item.Title).Append('~')
                .Append(item.UpdatedAt.Ticks).Append('~')
                .Append(item.IsPinned ? '1' : '0')

                // Цена — всегда, а не только при сортировке по ней: она стоит в подсказке строки (E3).
                .Append('~').Append(item.TotalCost);

            if (organize.Chats.TryGetValue(item.Id, out var placement))
            {
                builder.Append('~').Append(placement.FolderId).Append('~');
                foreach (var tag in placement.Tags)
                {
                    builder.Append(tag).Append(',');
                }

                builder.Append('~').Append(placement.Archived ? '1' : '0');
            }
        }
    }
}
