using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.Fox;

/// <summary>
/// Переписка с Розой (1.36.0): маленькая панель у него над головой — прошлые разговоры пузырями и
/// поле, чтобы написать ему. Ставит и двигает её сцена (<c>FoxStage.PlaceChat</c>).
/// </summary>
/// <remarks>
/// <para>
/// Не окно поверх программы, а часть слоя Розы: без затемнения, чат под ней работает как прежде —
/// так попросил человек. Стили общие, по ключу (<c>DialogField</c>, <c>DialogInput</c>), как у поля
/// разговора над головой: своих сборке Розы не нужно.
/// </para>
/// <para>
/// Сказанное уходит тем же сигналом, что поле над головой (<c>FoxStage.Talk</c>): дружба, дневной
/// потолок и «думает» у них общие. В ожидании одно сообщение: модель Розы отвечает по одному, и
/// пара «сказал — ответил» не путается.
/// </para>
/// </remarks>
internal sealed class FoxChat : Border
{
    /// <summary>Ширина панели: маленькая, чтобы не закрывать ленту.</summary>
    public const double PanelWidth = 250;

    /// <summary>Высота ленты пузырей; дальше она прокручивается.</summary>
    private const double FeedHeight = 170;

    private readonly StackPanel _feed = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Func<string, bool> _talk;
    private readonly Random _random = new();
    private TextBlock? _empty;
    private TextBlock? _pending;

    /// <param name="history">Разговоры из памяти Розы, старые — первыми.</param>
    /// <param name="talk">Сказать Розе; <c>false</c> — модель не спросят, ответа не будет.</param>
    /// <param name="close">Крестик.</param>
    public FoxChat(IReadOnlyList<FoxExchange> history, Func<string, bool> talk, Action close)
    {
        _talk = talk;
        Width = PanelWidth;
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 6, 10, 10);
        SetResourceReference(BackgroundProperty, "Bg.Panel");
        SetResourceReference(BorderBrushProperty, "Border.Default");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Child = layout;

        var header = new DockPanel { LastChildFill = true };
        var cross = new Border
        {
            Child = new TextBlock { Text = "✕", FontSize = 11 },
            Padding = new Thickness(6, 2, 6, 3),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
            ToolTip = Loc.Get("S.Common.Close")
        };
        AutomationProperties.SetName(cross, Loc.Get("S.Common.Close"));
        ((TextBlock)cross.Child).SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        cross.MouseEnter += (_, _) => cross.SetResourceReference(BackgroundProperty, "Bg.Hover");
        cross.MouseLeave += (_, _) => cross.Background = System.Windows.Media.Brushes.Transparent;
        cross.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            close();
        };
        CloseButton = cross;
        DockPanel.SetDock(cross, Dock.Right);
        header.Children.Add(cross);
        var title = new TextBlock { Text = Loc.Get("S.Fox.Chat.Title"), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        header.Children.Add(title);
        layout.Children.Add(header);

        _scroll = new ScrollViewer
        {
            Content = _feed,
            MaxHeight = FeedHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 6, 0, 8),
            Focusable = false
        };
        Grid.SetRow(_scroll, 1);
        layout.Children.Add(_scroll);

        _input = new TextBox { MaxLength = FoxMemory.HumanLimit, Tag = Loc.Get("S.Fox.TalkHint"), FontSize = 12 };
        _input.SetResourceReference(StyleProperty, "DialogInput");
        _input.PreviewKeyDown += OnInputKey;
        var field = new Border { Child = _input, Height = 32 };
        field.SetResourceReference(StyleProperty, "DialogField");
        Grid.SetRow(field, 2);
        layout.Children.Add(field);

        foreach (var exchange in history)
        {
            _ = Add(exchange.Human, mine: true, exchange.At);
            _ = Add(exchange.Rosa, mine: false, exchange.At);
        }

        if (history.Count == 0)
        {
            _empty = new TextBlock { Text = Loc.Get("S.Fox.Chat.Empty"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            _empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
            _feed.Children.Add(_empty);
        }

        // Нажатие, которое внутри никто не взял, панель забирает себе. Иначе оно всплывало к корню
        // окна, а тот начинает им перетаскивание окна (WM_NCLBUTTONDOWN): Windows уводит мышь в свой
        // цикл перемещения, отпускание до крестика не доходит — панель не закрывалась, а окно ехало.
        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            FocusInput();
        };
        Loaded += (_, _) => _scroll.ScrollToEnd();
    }

    /// <summary>Крестик — тестам.</summary>
    internal FrameworkElement CloseButton { get; }

    /// <summary>Ждёт ли сказанное ответа — тестам.</summary>
    internal bool Waiting => _pending is not null;

    /// <summary>Пузыри ленты по порядку — тестам.</summary>
    internal IReadOnlyList<string> Lines =>
        [.. _feed.Children.OfType<Border>().Select(bubble => ((TextBlock)bubble.Child).Text)];

    /// <summary>Курсор — в поле, чтобы печатать сразу.</summary>
    public void FocusInput()
    {
        _ = _input.Focus();
        _ = Keyboard.Focus(_input);
    }

    /// <summary>
    /// Ответ на сказанное отсюда: реплика; «слова убежали», если модель не ответила; ничего, если
    /// он молча убежал. <c>false</c> — отсюда ничего не ждали: ответ на поле над головой.
    /// </summary>
    public bool Answered(FoxReply reply)
    {
        if (_pending is null)
        {
            return false;
        }

        if (reply.Failed)
        {
            _pending.Text = Loc.Get(FoxPhrases.Pick("Mute", _random));
        }
        else if (reply.Line.Length > 0)
        {
            _pending.Text = reply.Line;
        }
        else
        {
            _feed.Children.Remove((UIElement)_pending.Parent);
        }

        _pending = null;
        _scroll.ScrollToEnd();
        return true;
    }

    /// <summary>Отправить набранное — Enter, и тестам.</summary>
    internal void Send(string? text = null)
    {
        if (text is not null)
        {
            _input.Text = text;
        }

        var said = _input.Text.Trim();
        if (said.Length == 0 || _pending is not null)
        {
            return;
        }

        _input.Text = "";
        _ = Add(said, mine: true, DateTime.Now);
        if (_talk(said))
        {
            _pending = Add("…", mine: false, DateTime.Now);
        }
        else
        {
            _ = Add(Loc.Get("S.Fox.Chat.Tired"), mine: false, DateTime.Now);
        }

        _scroll.ScrollToEnd();
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Send();
        }
    }

    /// <summary>Пузырь: человек — справа, Роза — слева; когда сказано — в подсказке.</summary>
    private TextBlock Add(string text, bool mine, DateTime at)
    {
        if (_empty is not null)
        {
            _feed.Children.Remove(_empty);
            _empty = null;
        }

        var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        line.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var bubble = new Border
        {
            Child = line,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 4, 8, 5),
            Margin = mine ? new Thickness(30, 2, 0, 2) : new Thickness(0, 2, 30, 2),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            ToolTip = at == default ? null : at.ToString("g", CultureInfo.CurrentCulture)
        };
        bubble.SetResourceReference(BackgroundProperty, mine ? "Bg.Selected" : "Bg.Raised");
        _feed.Children.Add(bubble);
        return line;
    }
}
