using System.Diagnostics.CodeAnalysis;

namespace Amarin.Core;

/// <summary>
/// Действие, которому можно назначить сочетание клавиш.
/// </summary>
/// <param name="Id">Имя в <c>settings.json</c>. Переименовывать нельзя — назначения отвяжутся.</param>
/// <param name="DefaultGesture">Заводское сочетание.</param>
/// <param name="TitleKey">Ключ подписи для списка в настройках.</param>
/// <param name="DescriptionKey">Ключ пояснения под подписью.</param>
public sealed record HotkeyAction(string Id, string DefaultGesture, string TitleKey, string DescriptionKey);

/// <summary>
/// Сочетания клавиш: какие действия бывают, что назначено сейчас и как читается запись.
/// </summary>
/// <remarks>
/// <para>
/// Без WPF намеренно — как и весь <c>Core</c>. Здесь живёт только запись сочетания строкой
/// (<c>"Ctrl+Shift+N"</c>) и правила о ней; превращение в <c>ModifierKeys</c> и <c>Key</c> —
/// дело <c>UI/Hotkeys</c>. Благодаря этому разбор проверяется тестами без окна, как
/// <c>ChatZoomMath</c>.
/// </para>
/// <para>
/// Список действий один на программу. Второй разошёлся бы с ним молча — по той же причине,
/// по которой один <c>ModelSlots.All</c>.
/// </para>
/// </remarks>
public static class HotkeyMap
{
    /// <summary>Новый чат.</summary>
    public const string NewChat = "NewChat";

    /// <summary>Ответить на выделенный фрагмент ответа — прикрепить его к сообщению цитатой.</summary>
    /// <remarks>
    /// Срабатывает, только когда в ответе модели что-то выделено; иначе сочетание уходит дальше,
    /// как будто его и не назначали.
    /// </remarks>
    public const string ReplyToSelection = "ReplyToSelection";

    /// <summary>
    /// Все действия, в порядке показа в настройках.
    /// </summary>
    public static readonly IReadOnlyList<HotkeyAction> All =
    [
        new(NewChat, "Ctrl+F", "S.Hotkeys.NewChat", "S.Hotkeys.NewChatDesc"),
        new(ReplyToSelection, "Ctrl+R", "S.Hotkeys.Reply", "S.Hotkeys.ReplyDesc")
    ];

    /// <summary>Порядок модификаторов в записи. Он же порядок показа человеку.</summary>
    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];

    /// <summary>
    /// Без одного из этих сочетания не бывает.
    /// </summary>
    /// <remarks>
    /// Shift в этот список не входит: <c>Shift+N</c> — это просто заглавная «N», и отняв её у
    /// клавиатуры, мы отняли бы у человека букву. Win — тоже: его сочетания забирает себе
    /// Windows, и назначенное на него молча не сработало бы.
    /// </remarks>
    private static readonly string[] RequiredModifiers = ["Ctrl", "Alt"];

    public static HotkeyAction? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(action => string.Equals(action.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Что назначено действию сейчас: из настроек, а если там пусто или мусор — заводское.
    /// </summary>
    /// <remarks>
    /// Отсутствующий ключ значит «как с завода», поэтому <c>settings.json</c> прежних версий
    /// читается без миграции, а сброшенное сочетание просто убирается из словаря.
    /// </remarks>
    public static string Gesture(IReadOnlyDictionary<string, string>? assignments, string id)
    {
        var action = Find(id) ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Неизвестное действие.");

        if (assignments is not null &&
            assignments.TryGetValue(id, out var stored) &&
            TryParse(stored, out var normalized))
        {
            return normalized;
        }

        return action.DefaultGesture;
    }

    /// <summary>Сочетание записано так же, как заводское для этого действия.</summary>
    public static bool IsDefault(IReadOnlyDictionary<string, string>? assignments, string id) =>
        string.Equals(Gesture(assignments, id), Find(id)?.DefaultGesture, StringComparison.Ordinal);

    /// <summary>
    /// Разбирает запись сочетания и приводит её к каноническому виду.
    /// </summary>
    /// <remarks>
    /// Ctrl или Alt обязателен (см. <see cref="RequiredModifiers"/>): голая буква
    /// перехватывалась бы прямо во время набора сообщения, и печатать стало бы нечем. Порядок
    /// модификаторов канонизируется, иначе <c>Shift+Ctrl+N</c> и <c>Ctrl+Shift+N</c> считались
    /// бы разными назначениями.
    /// </remarks>
    public static bool TryParse(string? gesture, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var modifiers = new List<string>();
        string? key = null;

        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.Trim();
            var modifier = ModifierOrder.FirstOrDefault(
                item => item.Equals(part, StringComparison.OrdinalIgnoreCase) ||
                        Aliases(item).Contains(part, StringComparer.OrdinalIgnoreCase));
            if (modifier is not null)
            {
                if (!modifiers.Contains(modifier, StringComparer.Ordinal))
                {
                    modifiers.Add(modifier);
                }

                continue;
            }

            // Вторая невспомогательная часть — это уже не сочетание, а опечатка.
            if (key is not null)
            {
                return false;
            }

            key = part;
        }

        if (key is null || key.Length == 0 ||
            !modifiers.Any(item => RequiredModifiers.Contains(item, StringComparer.Ordinal)))
        {
            return false;
        }

        // Одиночный символ — вверх: буквы и цифры человек правит в файле как придётся, а
        // «Ctrl+f» рядом с «Ctrl+F» были бы двумя записями об одном и том же. Длинные имена
        // (NumPad1, Delete) оставляем как есть — их пишет сама программа по имени клавиши,
        // а сравнение всё равно не смотрит на регистр.
        if (key.Length == 1)
        {
            key = key.ToUpperInvariant();
        }

        var ordered = ModifierOrder.Where(item => modifiers.Contains(item, StringComparer.Ordinal));
        normalized = string.Join('+', ordered.Append(key));
        return true;
    }

    /// <summary>Как сочетание показывают человеку: те же части, но с пробелами вокруг плюсов.</summary>
    public static string Display(string gesture) =>
        string.IsNullOrWhiteSpace(gesture) ? "" : gesture.Replace("+", " + ", StringComparison.Ordinal);

    private static string[] Aliases(string modifier) => modifier switch
    {
        "Ctrl" => ["Control"],
        "Win" => ["Windows", "Meta", "Cmd"],
        _ => []
    };
}
