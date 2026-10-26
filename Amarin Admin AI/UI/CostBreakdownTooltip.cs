using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Строка таблицы подсказки: подпись слева, значение столбцом справа.</summary>
internal readonly record struct TipRow(string Label, string Value);

/// <summary>Цвет заметки под таблицей подсказки.</summary>
internal enum TipNoteTone
{
    Dim,
    Warning,
    Danger
}

/// <summary>Заметка под таблицей подсказки: пояснение тусклым или предупреждение цветом.</summary>
internal readonly record struct TipNote(string Text, TipNoteTone Tone = TipNoteTone.Dim);

/// <summary>
/// Подробный счёт за единственной ценой в строке сведений сообщения.
/// </summary>
/// <remarks>
/// <para>
/// В итог входят разговор с моделью, каждый платный инструмент (рисование, чтение страниц) и
/// каждый вложенный агент хода. Ход с двумя картинками может стоить в двадцать раз дороже текста,
/// и по одному числу это не отличить от дорогой модели.
/// </para>
/// <para>
/// Собирается в коде, а не в XAML: число строк зависит от хода. Оформление повторяет подсказку-
/// предупреждение из <c>MainWindow.xaml</c>: прозрачный <see cref="ToolTip"/>, чей шаблон рисует
/// стрелку и карточку темы. Масштаб интерфейса применяет <c>UiScale</c> обработчиком класса на
/// <see cref="ToolTip.OpenedEvent"/>, так что регистрировать здесь ничего не нужно.
/// </para>
/// <para>
/// Ту же форму описывает неявный стиль <c>ToolTip</c> в <c>Resources.xaml</c> — разметку XAML
/// отсюда не переиспользовать. Меняя одно, меняйте и второе: три плашки, которые человек видит
/// рядом, обязаны совпадать. На это есть тест, сверяющий обе карточки по фону, скруглению,
/// полям и наличию стрелки.
/// </para>
/// </remarks>
internal static class CostBreakdownTooltip
{
    /// <summary>
    /// Готовая подсказка: оформление и строки разбивки.
    /// </summary>
    public static ToolTip Build(FrameworkElement host, ChatDisplayMessage message)
    {
        var tip = CreateEmpty();
        Fill(tip, host, message);
        return tip;
    }

    /// <summary>
    /// Подсказка с оформлением, но без строк.
    /// </summary>
    /// <remarks>
    /// Строк в разбивке до десятка, и сетка под них строилась для каждого ответа в ленте —
    /// хотя разворачивают её изредка. <see cref="Fill"/> вставляет их в тот момент, когда
    /// подсказку собрались показать; сама подсказка при этом существует сразу, иначе WPF
    /// не стал бы её показывать вовсе.
    /// </remarks>
    public static ToolTip CreateEmpty() => Chrome(content: null);

    /// <summary>Вставляет строки разбивки в подсказку, сделанную <see cref="CreateEmpty"/>.</summary>
    public static void Fill(ToolTip tip, FrameworkElement host, ChatDisplayMessage message)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(message);

        if (tip is CostTip card)
        {
            card.Card.Child = BuildRows(host, message);
        }
    }

    /// <summary>
    /// Готовые строки — у цены чата целиком (E3). Последняя строка — итог: жирным и под чертой,
    /// если над ней есть что складывать.
    /// </summary>
    public static void Fill(ToolTip tip, IReadOnlyList<(string Label, VeniceCost Cost)> lines)
    {
        ArgumentNullException.ThrowIfNull(tip);
        if (tip is not CostTip card || lines.Count == 0)
        {
            return;
        }

        var rows = new Grid();
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var line = 0;
        for (var i = 0; i < lines.Count - 1; i++)
        {
            AddRow(rows, ref line, lines[i].Label, lines[i].Cost, bold: false);
        }

        if (line > 1)
        {
            AddRule(rows, ref line);
        }

        if (line != 1)
        {
            AddRow(rows, ref line, lines[^1].Label, lines[^1].Cost, bold: true);
        }

        card.Card.Child = rows;
    }

    /// <summary>
    /// Таблица у плашек поля ввода (1.37.0): остаток и контекст. Сверху — итог, под
    /// чертой — строки с числами столбцом справа, ниже — заметки. Числа справа, а пояснения
    /// тусклые и отдельно: прежде доллары шли сразу за подписью, а объяснения — тем же цветом,
    /// что цифры, и глазу не за что было зацепиться.
    /// </summary>
    public static void Fill(ToolTip tip, TipRow total, IReadOnlyList<TipRow> rows, IReadOnlyList<TipNote> notes)
    {
        ArgumentNullException.ThrowIfNull(tip);
        if (tip is not CostTip card)
        {
            return;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var line = 0;
        AddRow(grid, ref line, total.Label, total.Value, bold: true);
        if (rows.Count > 0)
        {
            AddRule(grid, ref line);
            foreach (var row in rows)
            {
                AddRow(grid, ref line, row.Label, row.Value, bold: false);
            }
        }

        if (notes.Count > 0)
        {
            AddRule(grid, ref line);
            foreach (var note in notes)
            {
                var text = new TextBlock
                {
                    Text = note.Text,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 15,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, note.Tone switch
                {
                    TipNoteTone.Warning => "Status.Warning",
                    TipNoteTone.Danger => "Status.Danger",
                    _ => "Text.Faint"
                });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(text, line);
                Grid.SetColumnSpan(text, 2);
                grid.Children.Add(text);
                line++;
            }
        }

        card.Card.Child = grid;
    }

    private static void AddRule(Grid rows, ref int line)
    {
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 5, 0, 5),
            SnapsToDevicePixels = true
        };
        rule.SetResourceReference(Border.BackgroundProperty, "Border.Default");
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(rule, line);
        Grid.SetColumnSpan(rule, 2);
        rows.Children.Add(rule);
        line++;
    }

    private static UIElement BuildRows(FrameworkElement host, ChatDisplayMessage message)
    {
        var rows = new Grid { Margin = new Thickness(0) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var line = 0;

        // Порядок строк — хронология трат: сперва выбрали модель, потом с ней говорили.
        // Условие именно «цена записана», а не «выбрана Авто»: RequestedModelId равен "auto" и
        // у второго ответа хода, и у старых переписок, где цены маршрутизатора не сохранено.
        if (message.RouterCost is not null)
        {
            AddRow(rows, ref line, Loc.Get("S.Cost.Router"), message.RouterCost, bold: false);
        }

        var model = VeniceModelCatalog.GetDisplayName(
            message.ResolvedModelId ?? message.RequestedModelId ?? "");
        AddRow(rows, ref line, string.IsNullOrWhiteSpace(model) ? Loc.Get("S.Cost.Model") : model, message.ModelCost, bold: false);

        foreach (var round in message.ToolRounds)
        {
            foreach (var call in round.Calls)
            {
                if (call.NestedAgent is { Cost.HasData: true } agent)
                {
                    var name = string.IsNullOrWhiteSpace(agent.DisplayName)
                        ? Loc.Format("S.Agent.Label", agent.ModelId)
                        : Loc.Format("S.Agent.LabelNamed", agent.DisplayName);
                    AddRow(rows, ref line, name, agent.Cost, bold: false);
                    continue;
                }

                if (call.Cost is { HasData: true })
                {
                    AddRow(rows, ref line, call.Name, call.Cost, bold: false);
                }
            }
        }

        // После инструментов, потому что проверяет именно их, и по тому же строгому условию,
        // что заголовок: строка «Guard» появляется, когда за защиту заплачено, а не когда она
        // просто включена.
        if (message.GuardCost is { HasData: true })
        {
            AddRow(rows, ref line, Loc.Get("S.Cost.Guard"), message.GuardCost, bold: false);
        }

        // Строже строки маршрутизатора намеренно: та существует ради факта, что выбор был, а
        // эта — только потому, что потрачены деньги. «$0 Заголовок чата» под каждым ответом,
        // за который никто не платил, был бы шумом.
        if (message.TitleCost is { HasData: true })
        {
            AddRow(rows, ref line, Loc.Get("S.Cost.ChatTitle"), message.TitleCost, bold: false);
        }

        // По тому же условию: сводку дописывают после каждого ответа, и это отдельные деньги,
        // которых в счёте хода нет.
        if (message.SummaryCost is { HasData: true })
        {
            AddRow(rows, ref line, Loc.Get("S.Cost.Summary"), message.SummaryCost, bold: false);
        }

        // Сжатие контекста (D10) — тоже своими деньгами, прибавленными к итогу.
        if (message.CompactCost is { HasData: true })
        {
            AddRow(rows, ref line, Loc.Get("S.Cost.Compact"), message.CompactCost, bold: false);
        }

        // Одна строка — складывать нечего, итог под ней лишь повторил бы её.
        if (line > 1)
        {
            AddRule(rows, ref line);

            AddRow(rows, ref line, Loc.Get("S.Cost.Total"), message.Cost, bold: true);
        }

        return rows;
    }

    private static void AddRow(Grid rows, ref int line, string label, VeniceCost? cost, bool bold) =>
        // Нулевая цена — тоже строка: инструмент работал и ничего не взял, а это не то же,
        // что «не работал».
        AddRow(rows, ref line, label, ChatFormat.Cost(cost) is { Length: > 0 } value ? value : "$0", bold);

    private static void AddRow(Grid rows, ref int line, string label, string value, bool bold)
    {
        var name = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 190,
            Margin = new Thickness(0, 1, 14, 1),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, bold ? "Text.Secondary" : "Text.Muted");

        var price = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 1, 0, 1),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
        price.SetResourceReference(TextBlock.ForegroundProperty, bold ? "Text.Bright" : "Text.Secondary");

        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(name, line);
        Grid.SetRow(price, line);
        Grid.SetColumn(price, 1);
        rows.Children.Add(name);
        rows.Children.Add(price);
        line++;
    }

    /// <summary>
    /// Подсказка, помнящая свою карточку: в неё вставляются строки, когда подсказку показывают.
    /// </summary>
    private sealed class CostTip : ToolTip
    {
        public required Border Card { get; init; }
    }

    private static ToolTip Chrome(UIElement? content)
    {
        var card = new Border
        {
            MinWidth = 180,
            MaxWidth = 300,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = content
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg.Sheet");
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

        // По центру, как и у подсказок из неявного стиля: карточка встаёт по центру под своей
        // целью (UiScale.PlaceUnderTarget), и только из середины стрелка показывает на цену,
        // а не куда-то левее неё.
        var arrow = new System.Windows.Shapes.Path
        {
            Data = Glyphs.Get("M0,6 L6,0 L12,6 Z"),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, -1)
        };
        arrow.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Bg.Sheet");
        arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Border.Default");

        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        stack.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(arrow, 0);
        Grid.SetRow(card, 1);
        stack.Children.Add(arrow);
        stack.Children.Add(card);

        var tip = new CostTip
        {
            Card = card,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HasDropShadow = false,
            Content = stack
        };
        tip.Template = BuildTemplate();
        return tip;
    }

    /// <summary>
    /// Пустой шаблон: штатный рисует вокруг карточки свои фон и рамку — рамка удвоилась бы, а за
    /// стрелкой встал бы непрозрачный прямоугольник.
    /// </summary>
    private static ControlTemplate BuildTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        return new ControlTemplate(typeof(ToolTip)) { VisualTree = presenter };
    }
}
