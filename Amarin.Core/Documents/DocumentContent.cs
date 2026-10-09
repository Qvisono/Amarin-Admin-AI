namespace Amarin.Core;

/// <summary>
/// Прочитанная часть документа: разделы из нумерованных единиц — строк текста, абзацев Word,
/// слайдов, страниц PDF или строк листа Excel — и сколько их всего.
/// </summary>
/// <remarks>
/// <para>
/// Номер единицы — то, на что модель потом ссылается в правке (<c>edit_document</c>): «вставить
/// после абзаца 12», «ячейка B7 листа «Итоги»». Поэтому нумерация здесь та же, что у правки, а не
/// своя для показа: пустые абзацы Word номер получают, хоть и не показываются, — иначе номера в
/// чтении и в правке разошлись бы на первом же пустом абзаце.
/// </para>
/// <para>
/// Читается только окно (<see cref="DocumentWindow"/>) и только то, что влезает в бюджет знаков:
/// модели нужна страница, а не книга, и стопятидесятистраничный PDF или лист на сто тысяч строк
/// иначе целиком лёг бы и в память, и в контекст.
/// </para>
/// </remarks>
internal sealed class DocumentContent
{
    public required DocumentKind Kind { get; init; }

    /// <summary>Как называется единица в подписях: line, paragraph, slide, page, row.</summary>
    public required string Unit { get; init; }

    /// <summary>Листы Excel; у остальных видов раздел один, без имени.</summary>
    public required IReadOnlyList<DocumentSection> Sections { get; init; }

    /// <summary>Сводка для заголовка: «180 paragraphs, 3 tables», «12 pages».</summary>
    public string Summary { get; init; } = "";

    /// <summary>Что модели важно знать о чтении: нет текстового слоя, лист пропущен.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <param name="Name">Имя листа Excel; null у остальных.</param>
/// <param name="Units">Показанные единицы — окно, а не весь раздел.</param>
/// <param name="Total">Сколько единиц в разделе всего.</param>
/// <param name="Extent">Занятая область листа (<c>A1:F120</c>); null у остальных.</param>
/// <param name="More">После окна в разделе есть ещё единицы: окно кончилось бюджетом или пределом, а не разделом.</param>
internal sealed record DocumentSection(
    string? Name,
    IReadOnlyList<DocumentUnit> Units,
    int Total,
    string? Extent = null,
    bool More = false);

/// <param name="Number">Номер, по которому на единицу ссылается правка.</param>
/// <param name="Text">Текст единицы; у строки Excel пусто — её содержимое в <paramref name="Cells"/>.</param>
/// <param name="Cells">Ячейки строки листа по номерам столбцов (A = 1); null у остальных.</param>
internal sealed record DocumentUnit(int Number, string Text, IReadOnlyList<SheetCell>? Cells = null);

/// <param name="Column">Номер столбца: A = 1.</param>
/// <param name="Value">Что видно в ячейке; у формулы — ещё и сама формула.</param>
internal sealed record SheetCell(int Column, string Value);

/// <summary>Какую часть документа показать.</summary>
/// <param name="Offset">Первая единица (1 — с начала); у Excel — первая строка листа.</param>
/// <param name="Limit">Сколько единиц показать не больше чем; null — сколько влезет в бюджет.</param>
/// <param name="Sheet">Лист Excel по имени; null — все листы по очереди.</param>
/// <param name="Range">Диапазон листа Excel (<c>A1:F50</c>); null — весь лист.</param>
/// <param name="Budget">Сколько знаков текста отдать самое большее.</param>
/// <param name="Part">
/// Какой кусок единицы <paramref name="Offset"/> показать, если она одна длиннее бюджета (огромная
/// таблица, абзац на десять страниц): 1 — начало, 2 — следующие <paramref name="Budget"/> знаков.
/// Без этого хвост такой единицы был недостижим — <paramref name="Offset"/> считает целые единицы.
/// </param>
internal sealed record DocumentWindow(
    int Offset = 1,
    int? Limit = null,
    string? Sheet = null,
    string? Range = null,
    int Budget = DocumentWindow.DefaultBudget,
    int Part = 1)
{
    /// <summary>
    /// Бюджет ответа инструмента: чуть меньше того, что уходит модели (<c>ChatToolPreview.ApiLimit</c>),
    /// — заголовок и подсказка «дальше» тоже должны поместиться.
    /// </summary>
    public const int DefaultBudget = 10_000;
}

/// <summary>Копит единицы окна, пока не кончится бюджет знаков.</summary>
internal sealed class UnitBudget(DocumentWindow window)
{
    private int _left = Math.Max(500, window.Budget);
    private int _taken;

    /// <summary>Окно исчерпано: набран <see cref="DocumentWindow.Limit"/> или кончился бюджет.</summary>
    public bool Full { get; private set; }

    /// <summary>Была единица, которую окно не взяло, — значит, дальше есть что читать.</summary>
    public bool More { get; private set; }

    /// <summary>Берёт единицу, если она влезает. Первая берётся всегда: пустое чтение бесполезно.</summary>
    /// <param name="size">Сколько знаков займёт единица на выводе.</param>
    public bool TryTake(int size)
    {
        if (Full)
        {
            More = true;
            return false;
        }

        if (window.Limit is { } limit && _taken >= limit)
        {
            Full = true;
            More = true;
            return false;
        }

        if (size > _left && _taken > 0)
        {
            Full = true;
            More = true;
            return false;
        }

        _left -= size;
        _taken++;
        return true;
    }

    /// <summary>Знаков осталось — чтобы первая, слишком длинная единица обрезалась по нему.</summary>
    public int Left => Math.Max(0, _left);

    /// <summary>
    /// Текст единицы, обрезанный по месту, которое было до неё: огромная таблица или строка не
    /// рвёт ответ. Место меряют до <see cref="TryTake"/> — после него оно уже занято этой единицей.
    /// </summary>
    public static string Fit(string text, int room)
    {
        var cap = Math.Max(200, room);
        return text.Length <= cap ? text : text[..cap] + $" … [+{text.Length - cap} chars]";
    }

    /// <summary>
    /// Текст единицы для показа: нужный кусок, если просили <see cref="DocumentWindow.Part"/>, и
    /// обрезанный по месту — с подсказкой, как прочитать следующий кусок.
    /// </summary>
    public static string Piece(string text, int number, int room, DocumentWindow window)
    {
        var size = PieceSize(window);
        var part = number == window.Offset ? Math.Max(1, window.Part) : 1;
        var skip = (long)(part - 1) * size;
        if (skip >= text.Length)
        {
            return part > 1 ? $"[part {part} is past the end: {number} has {text.Length} chars in all]" : text;
        }

        var rest = text[(int)skip..];
        var cap = Math.Max(200, Math.Min(room, size));
        if (rest.Length <= cap)
        {
            return rest;
        }

        return rest[..cap] + $" … [continues: read_file with offset={number}, limit=1, part={part + 1}]";
    }

    /// <summary>Длина куска: столько же, сколько бюджет окна, — первый кусок единицы ровно влезает в чтение.</summary>
    private static int PieceSize(DocumentWindow window) => Math.Max(500, window.Budget);

    /// <summary>
    /// Окно по номерам единиц, которые уже прочитаны целиком (текст, Word): начиная с
    /// <see cref="DocumentWindow.Offset"/>, пока берёт бюджет.
    /// </summary>
    public static DocumentSection Slice(
        IReadOnlyList<DocumentUnit> all,
        DocumentWindow window,
        Func<DocumentUnit, int> size,
        Func<DocumentUnit, bool>? hidden = null)
    {
        var budget = new UnitBudget(window);
        var shown = new List<DocumentUnit>();
        foreach (var unit in all)
        {
            if (unit.Number < window.Offset || hidden?.Invoke(unit) == true)
            {
                continue;
            }

            var room = budget.Left;
            if (!budget.TryTake(size(unit)))
            {
                break;
            }

            shown.Add(unit with { Text = Piece(unit.Text, unit.Number, room, window) });
        }

        return new DocumentSection(null, shown, all.Count, More: budget.More);
    }
}

/// <summary>Документ не читается или не правится: битый, защищён паролем, слишком большой.</summary>
/// <remarks>Текст — для модели: он уходит ей ответом инструмента и говорит, что делать дальше.</remarks>
internal sealed class DocumentException(string message) : Exception(message);
