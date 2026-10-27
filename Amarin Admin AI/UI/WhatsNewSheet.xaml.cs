using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Перевод заметок для «Что нового»: язык интерфейса и чем переводить.
/// </summary>
/// <param name="Name">Название языка на нём самом — подпись кнопки.</param>
/// <param name="RussianFlag">Нарисовать у кнопки флаг России; у языков, переведённых ИИ, флага нет.</param>
/// <param name="Translate">Перевод моделью; <c>null</c> — ключа нет, кнопка недоступна.</param>
internal sealed record WhatsNewTranslation(
    string Name,
    bool RussianFlag,
    Func<ReleaseNotesOutline, CancellationToken, Task<ReleaseNotesOutline?>>? Translate);

/// <summary>
/// «Что нового» (1.37.0): заметки к версии поверх окна, раз на версию — сцена «Занавес», над ней
/// карточка с колонкой разделов и переключателем языка заметок.
/// </summary>
/// <remarks>
/// Колонка разделов строится из «##» заметок (<see cref="ReleaseNotesOutline"/>). Подсвечен в ней
/// раздел под мышью, а когда мышь вне заметок — раздел у верха прокрутки. Каждый раздел — свой
/// блок, а не кусок одного документа: так у раздела есть границы, по которым его узнаёт мышь и
/// к которым листает колонка. Создаётся при первом показе: на первом кадре её не видно.
/// </remarks>
public partial class WhatsNewSheet : UserControl
{
    /// <summary>Длительность доезда к разделу по щелчку в колонке.</summary>
    private static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(260);

    private readonly List<RadioButton> _tocItems = [];
    private readonly List<FrameworkElement> _sections = [];
    private ReleaseNotesOutline _original = new("", []);
    private ReleaseNotesOutline? _translated;
    private WhatsNewTranslation? _translation;
    private CancellationTokenSource? _translating;
    private bool _choosingLanguage;
    private int _highlighted = -1;

    /// <summary>
    /// Раздел, выбранный щелчком в колонке. Держится, пока человек сам не листнёт заметки или не
    /// поведёт по ним мышью: последние разделы до верха не доезжают, и прокрутка отдала бы
    /// подсветку последнему, а не тому, что выбрали.
    /// </summary>
    private int _pinned = -1;

    private double _glideFrom;
    private double _glideTo;
    private int _glideTarget = -1;
    private DateTime _glideStart;

    public WhatsNewSheet()
    {
        InitializeComponent();
        Curtain.Ticked += RevealCard;
        NotesScroll.PreviewMouseWheel += (_, _) =>
        {
            // Человек листает сам: доезд и выбранный щелчком раздел уступают ему.
            StopGlide();
            _pinned = -1;
        };
        NotesScroll.ScrollChanged += (_, _) => UpdateHighlight();
        NotesScroll.MouseMove += (_, e) =>
        {
            _pinned = -1;
            HighlightAt(e.GetPosition(NotesPanel).Y);
        };
        NotesScroll.MouseLeave += (_, _) => UpdateHighlight();
    }

    /// <summary>Карточку закрыли — «Понятно», Esc или щелчком мимо — и её уход кончился.</summary>
    internal event Action? Closed;

    /// <summary>Подсвеченный в колонке раздел; −1 — разделов нет.</summary>
    internal int HighlightedSection => _highlighted;

    /// <param name="translation">Перевод на язык интерфейса; <c>null</c> — интерфейс по-английски, переключателя нет.</param>
    internal void Show(ReleaseVersion version, string notes, WhatsNewTranslation? translation)
    {
        _original = ReleaseNotesOutline.Parse(notes);
        _translated = null;
        _translation = translation;
        CancelTranslation();
        _pinned = -1;

        VersionText.Text = "v" + version;
        SetupLanguages();
        Render(_original);
        NotesScroll.ScrollToHome();
        TocScroll.ScrollToHome();
        UpdateHighlight();

        IsHitTestVisible = true;
        Visibility = Visibility.Visible;

        // Место карточки сцене нужно знать, пока лист открыт; подписка на раскладку окна
        // снимается вместе с листом, иначе менеджер раскладки держал бы его после закрытия.
        Card.LayoutUpdated -= Card_LayoutUpdated;
        Card.LayoutUpdated += Card_LayoutUpdated;

        // Прокрутка — та же, что в ленте и колонке чатов: инерция колеса и средняя кнопка.
        SmoothScroll.SetIsEnabled(NotesScroll, true);
        SmoothScroll.SetPanScroll(NotesScroll, true);
        SmoothScroll.SetIsEnabled(TocScroll, true);
        Curtain.Start();
        Dispatcher.BeginInvoke(() => OkButton.Focus(), DispatcherPriority.Input);
    }

    /// <summary>
    /// Закрывает лист. С анимациями — уходом: карточка гаснет и чуть оседает, линии втягиваются
    /// вниз, фон гаснет; мышь и клавиши уже отпущены окну, а <see cref="Closed"/> приходит в конце.
    /// </summary>
    internal void Close()
    {
        if (Visibility != Visibility.Visible || Curtain.LeaveAt is not null)
        {
            return;
        }

        CancelTranslation();
        StopGlide();
        if (UiMotion.Enabled && IsVisible && Curtain.Running)
        {
            IsHitTestVisible = false;
            Curtain.Leave();
            return;
        }

        Finish();
    }

    /// <summary>Уход начат и ещё не кончен.</summary>
    internal bool Leaving => Curtain.LeaveAt is not null && Visibility == Visibility.Visible;

    private void Finish()
    {
        Card.LayoutUpdated -= Card_LayoutUpdated;

        // Хук плавной прокрутки держит кадры окна, пока идёт инерция: снимаем его вместе с листом.
        SmoothScroll.SetIsEnabled(NotesScroll, false);
        SmoothScroll.SetPanScroll(NotesScroll, false);
        SmoothScroll.SetIsEnabled(TocScroll, false);
        Curtain.Stop();
        Curtain.SetGlass(null, 0);
        Curtain.Opacity = 1;
        IsHitTestVisible = true;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    /// <summary>Подсвечивает раздел под точкой <paramref name="y"/> стопки заметок — так их читает мышь.</summary>
    internal void HighlightAt(double y)
    {
        if (_glideTarget >= 0 || _sections.Count == 0)
        {
            return;
        }

        var index = 0;
        for (var i = 0; i < _sections.Count; i++)
        {
            if (TopOf(_sections[i]) <= y)
            {
                index = i;
            }
        }

        Highlight(index);
    }

    /// <summary>Листает заметки к разделу <paramref name="index"/>, как щелчок по пункту колонки.</summary>
    internal void GoToSection(int index)
    {
        if (index < 0 || index >= _sections.Count)
        {
            return;
        }

        Highlight(index);
        _pinned = index;
        var target = Math.Clamp(TopOf(_sections[index]) - 2, 0, NotesScroll.ScrollableHeight);
        if (!UiMotion.Enabled || !IsVisible)
        {
            NotesScroll.ScrollToVerticalOffset(target);
            return;
        }

        StopGlide();
        _glideFrom = NotesScroll.VerticalOffset;
        _glideTo = target;
        _glideTarget = index;
        _glideStart = DateTime.UtcNow;
        CompositionTarget.Rendering += Glide;
    }

    // ---- Разделы ----

    private void Render(ReleaseNotesOutline outline)
    {
        var offset = NotesScroll.VerticalOffset;

        // Без разделов колонки нет, и всё — вступление ли, сплошной ли текст — идёт заметками.
        var hasSections = outline.Sections.Count > 0;
        TocBorder.Visibility = hasSections ? Visibility.Visible : Visibility.Collapsed;
        TocColumn.Width = hasSections ? new GridLength(164) : new GridLength(0);

        Lead.Children.Clear();
        Lead.Visibility = hasSections && outline.Lead.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Lead.Visibility == Visibility.Visible)
        {
            AddBlocks(Lead, outline.Lead, "WhatsNew.Text3");
        }

        _tocItems.Clear();
        _sections.Clear();
        TocPanel.Children.Clear();
        NotesPanel.Children.Clear();
        _highlighted = -1;

        if (!hasSections)
        {
            AddBlocks(NotesPanel, outline.Lead, "WhatsNew.Text2");
            return;
        }

        for (var i = 0; i < outline.Sections.Count; i++)
        {
            var section = outline.Sections[i];
            var index = i;

            var item = new RadioButton
            {
                Content = section.Title,
                Style = (Style)FindResource("WhatsNewTocItem"),
                ToolTip = section.Title
            };
            item.Click += (_, _) => GoToSection(index);
            _tocItems.Add(item);
            TocPanel.Children.Add(item);

            // .notes section: заголовок со звездой (h3), под ним пункты.
            var block = new StackPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 2, 0, 6) };
            block.Children.Add(Heading(section.Title, first: i == 0));
            AddBlocks(block, section.Body, "WhatsNew.Text2");
            _sections.Add(block);
            NotesPanel.Children.Add(block);
        }

        // Смена языка меняет текст, а не место чтения.
        NotesScroll.UpdateLayout();
        NotesScroll.ScrollToVerticalOffset(Math.Min(offset, NotesScroll.ScrollableHeight));
    }

    private static void AddBlocks(Panel panel, string markdown, string textKey)
    {
        foreach (var element in ReleaseNotesBlocks.Build(ReleaseNotesText.Compact(markdown), textKey))
        {
            panel.Children.Add(element);
        }
    }

    private FrameworkElement Heading(string title, bool first)
    {
        var star = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("WhatsNew.StarShape"),
            Width = 8,
            Height = 8,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        star.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Curtain.Star");

        var text = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "WhatsNew.Text1");

        var row = new DockPanel { Margin = new Thickness(0, first ? 2 : 8, 0, 5) };
        DockPanel.SetDock(star, Dock.Left);
        row.Children.Add(star);
        row.Children.Add(text);
        return row;
    }

    private double TopOf(FrameworkElement section) => section.TranslatePoint(default, NotesPanel).Y;

    /// <summary>
    /// Мышь над заметками — раздел под ней; иначе раздел у верха прокрутки, а в самом низу —
    /// последний: короткие последние разделы до верха не доезжают.
    /// </summary>
    private void UpdateHighlight()
    {
        if (_glideTarget >= 0 || _sections.Count == 0 || (_pinned >= 0 && !NotesScroll.IsMouseOver))
        {
            return;
        }

        if (NotesScroll.IsMouseOver)
        {
            HighlightAt(Mouse.GetPosition(NotesPanel).Y);
            return;
        }

        if (NotesScroll.ScrollableHeight > 0 && NotesScroll.VerticalOffset >= NotesScroll.ScrollableHeight - 2)
        {
            Highlight(_sections.Count - 1);
            return;
        }

        var index = 0;
        for (var i = 0; i < _sections.Count; i++)
        {
            if (TopOf(_sections[i]) - 12 <= NotesScroll.VerticalOffset)
            {
                index = i;
            }
        }

        Highlight(index);
    }

    private void Highlight(int index)
    {
        if (index == _highlighted || index < 0 || index >= _tocItems.Count)
        {
            return;
        }

        _highlighted = index;
        _tocItems[index].IsChecked = true;
        _tocItems[index].BringIntoView();
    }

    private void Glide(object? sender, EventArgs e)
    {
        var p = Math.Min(1, (DateTime.UtcNow - _glideStart).TotalMilliseconds / GlideTime.TotalMilliseconds);
        var eased = 1 - Math.Pow(1 - p, 3);
        NotesScroll.ScrollToVerticalOffset(_glideFrom + (_glideTo - _glideFrom) * eased);
        if (p >= 1)
        {
            var target = _glideTarget;
            StopGlide();
            Highlight(target);
        }
    }

    private void StopGlide()
    {
        CompositionTarget.Rendering -= Glide;
        _glideTarget = -1;
    }

    // ---- Появление ----

    /// <summary>Карточка проявляется и поднимается после того, как линии сцены начали расти.</summary>
    private void RevealCard(double t)
    {
        if (Curtain.LeaveAt is { } leave)
        {
            Depart(t - leave);
            return;
        }

        var p = Math.Clamp((t - .9) / .75, 0, 1);
        p = 1 - Math.Pow(1 - p, 3);
        Card.Opacity = p;
        CardShift.Y = (1 - p) * 14;
        CardScale.ScaleX = CardScale.ScaleY = .985 + .015 * p;
        Curtain.Opacity = 1;
        UpdateGlass();
    }

    /// <summary>
    /// Уход: карточка за 0,22 с гаснет, оседает на 10 точек и чуть сжимается — обратное появлению;
    /// линии сцены тем временем стекают вниз, а с 0,25 с гаснет и сам фон, открывая окно.
    /// </summary>
    private void Depart(double elapsed)
    {
        if (elapsed >= WhatsNewCurtain.LeaveTime)
        {
            Finish();
            return;
        }

        var q = Math.Clamp(elapsed / .22, 0, 1);
        q = q * q * (3 - 2 * q);
        Card.Opacity = 1 - q;
        CardShift.Y = q * 10;
        CardScale.ScaleX = CardScale.ScaleY = 1 - .03 * q;
        Curtain.Opacity = 1 - Math.Clamp((elapsed - .25) / (WhatsNewCurtain.LeaveTime - .25), 0, 1);
        UpdateGlass();
    }

    /// <summary>
    /// Стекло карточки (backdrop-filter концепта): под ней та же сцена, размытая. Кисть смотрит на
    /// то место сцены, над которым лежит карточка, — и пока она проявляется и поднимается.
    /// </summary>
    private void Card_LayoutUpdated(object? sender, EventArgs e) => UpdateGlass();

    private void UpdateGlass()
    {
        if (Card.ActualWidth <= 0 || Card.ActualHeight <= 0 || !Curtain.IsVisible)
        {
            return;
        }

        Curtain.SetGlass(Card.TransformToVisual(Curtain).TransformBounds(new Rect(Card.RenderSize)), Card.Opacity);
    }

    /// <summary>Карточка — не шире 680 и не выше 600, с полями 24 по бокам и 150 по высоте, как в концепте.</summary>
    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Card.Width = Math.Max(320, Math.Min(680, e.NewSize.Width - 48));
        Card.MaxHeight = Math.Max(260, Math.Min(600, e.NewSize.Height - 150));
    }



    // ---- Язык заметок ----

    private void SetupLanguages()
    {
        _choosingLanguage = true;
        try
        {
            LanguageSwitch.Visibility = _translation is null ? Visibility.Collapsed : Visibility.Visible;
            LanguageSwitch.IsHitTestVisible = true;
            EnglishButton.IsChecked = true;
            LanguageStatus.Text = "";
            SetBusy(false);

            if (_translation is null)
            {
                return;
            }

            NativeButton.Content = _translation.Name;
            NativeButton.Tag = _translation.RussianFlag ? FindResource("Flag.Ru") : null;
            NativeButton.IsEnabled = _translation.Translate is not null;
            if (_translation.Translate is null)
            {
                NativeButton.SetResourceReference(ToolTipProperty, "S.Updates.WhatsNewNeedsKeyHint");
                LanguageStatus.Text = Loc.Get("S.Updates.WhatsNewNeedsKey");
            }
            else
            {
                NativeButton.ClearValue(ToolTipProperty);
            }
        }
        finally
        {
            _choosingLanguage = false;
        }
    }

    private void EnglishButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_choosingLanguage)
        {
            Render(_original);
            UpdateHighlight();
        }
    }

    private void NativeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_choosingLanguage || _translation?.Translate is not { } translate)
        {
            return;
        }

        if (_translated is { } done)
        {
            Render(done);
            UpdateHighlight();
            return;
        }

        Detached.Run(TranslateAsync(translate), "whats_new_translate");
    }

    private async Task TranslateAsync(Func<ReleaseNotesOutline, CancellationToken, Task<ReleaseNotesOutline?>> translate)
    {
        CancelTranslation();
        var cancellation = new CancellationTokenSource();
        _translating = cancellation;

        SetBusy(true);
        LanguageStatus.Text = Loc.Get("S.Updates.WhatsNewTranslating");

        ReleaseNotesOutline? result;
        try
        {
            result = await translate(_original, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Пока шёл перевод, лист закрыли или показали заново.
        if (!ReferenceEquals(_translating, cancellation) || cancellation.IsCancellationRequested)
        {
            return;
        }

        _translating = null;
        SetBusy(false);
        if (result is null)
        {
            LanguageStatus.Text = Loc.Get("S.Updates.WhatsNewTranslateFailed");
            Choose(EnglishButton);
            return;
        }

        _translated = result;
        LanguageStatus.Text = "";
        Render(result);
        UpdateHighlight();
    }

    /// <summary>Отмечает кнопку, не запуская её обработчик: текст уже тот, что нужен.</summary>
    private void Choose(RadioButton button)
    {
        _choosingLanguage = true;
        try
        {
            button.IsChecked = true;
        }
        finally
        {
            _choosingLanguage = false;
        }
    }

    /// <summary>Пока идёт перевод, заметки приглушены, а язык не переключить: ответ лёг бы поверх выбора.</summary>
    private void SetBusy(bool busy)
    {
        // .notes.busy концепта: приглушение за четверть секунды.
        var opacity = busy ? .35 : 1;
        Split.IsHitTestVisible = !busy;
        if (UiMotion.Enabled && IsVisible)
        {
            Split.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(250)));
        }
        else
        {
            Split.BeginAnimation(OpacityProperty, null);
            Split.Opacity = opacity;
        }

        LanguageSwitch.IsHitTestVisible = !busy;
    }

    private void CancelTranslation()
    {
        _translating?.Cancel();
        _translating = null;
        SetBusy(false);
    }

    // ---- Закрытие ----

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Sheet_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter)
        {
            e.Handled = true;
            Close();
        }
    }
}
