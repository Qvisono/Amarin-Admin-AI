using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>
/// Панель «Состояние ПК» (C4): итог одной строкой и пять областей списком — диски, система,
/// защита, стабильность, обновления — по порогам <see cref="HealthRules"/>, с «Разобраться».
/// </summary>
/// <remarks>
/// Открывается сразу с прошлым снимком (<see cref="HealthCache"/>) и обновляет его, если тот
/// старше <see cref="StaleAfter"/>: поиск обновлений занимает до пары минут, и пустая панель всё
/// это время была бы хуже вчерашних цифр с честной подписью «обновлено вчера».
/// </remarks>
public partial class HealthPanel : UserControl
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private HealthCache? _cache;
    private Func<DateFormat> _format = () => DateFormat.DayMonthShort;
    private CancellationTokenSource? _collect;
    private Dictionary<HealthArea, HealthCard?> _cards = [];
    private bool _refreshing;

    /// <summary>
    /// Шаг живых плиток. Чаще — дёргалось бы число, реже — плитка переставала бы казаться живой.
    /// </summary>
    internal static readonly TimeSpan LiveStep = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _live = new() { Interval = LiveStep };
    private SystemLoadMeter? _meter;

    public HealthPanel()
    {
        InitializeComponent();
        SmoothScroll.SetIsEnabled(CardsScroll, true);
        _live.Tick += (_, _) => ShowLive();
    }

    /// <summary>Источник живых плиток; null — настоящий замер. Тесты дают свой.</summary>
    internal Func<SystemLoad>? LiveSource { get; set; }

    /// <summary>«Разобраться»: хозяин открывает новый чат с контекстом карточки в поле.</summary>
    internal event Action<string>? AskRequested;

    /// <summary>Панель просит закрыть себя (крестик, Esc, щелчок мимо).</summary>
    internal event Action? CloseRequested;

    /// <summary>Пробы; null — живые. Тесты дают свои.</summary>
    internal IReadOnlyList<Func<CancellationToken, Task<HealthCard>>>? Probes { get; set; }

    internal void Attach(HealthCache cache, Func<DateFormat> format)
    {
        _cache = cache;
        _format = format;
    }

    /// <summary>Показывает прошлый снимок и обновляет его, если он устарел.</summary>
    internal void Open()
    {
        var last = _cache?.Last;
        if (last is not null)
        {
            _cards = last.Cards.ToDictionary(card => card.Area, card => (HealthCard?)card);
            Render(last.At);
        }

        if (last is null || DateTime.Now - last.At > StaleAfter)
        {
            Refresh();
        }

        // Первый замер ЦП — только точка отсчёта, поэтому плитки снимаются сразу и потом по шагу:
        // через две секунды число уже настоящее.
        ShowLive();
        _live.Start();
        Focus();
    }

    /// <summary>
    /// Ставит плашку под её кнопку: правый край — по правому краю кнопки, верх — под ней.
    /// Координаты — в системе самой панели, она лежит во весь слой окна.
    /// </summary>
    internal void Place(Rect button)
    {
        var right = Math.Max(8, ActualWidth - button.Right);
        var top = button.Bottom + 4;
        Card.Margin = new Thickness(0, top, right, 0);
        Card.MaxHeight = Math.Max(200, Math.Min(560, ActualHeight - top - 12));
    }

    private void ShowLive()
    {
        // Замер не бросает: чего прочесть не удалось, приходит null, и плитка показывает прочерк.
        var load = LiveSource?.Invoke() ?? (_meter ??= new SystemLoadMeter()).Take();

        ShowTile(CpuValue, CpuBar, load.CpuPercent, HealthStatus.Ok);
        ShowTile(MemoryValue, MemoryBar, load.MemoryPercent,
            load.MemoryPercent >= HealthRules.MemoryAttentionLoad ? HealthStatus.Attention : HealthStatus.Ok);

        if (load.SystemDrive is { } drive)
        {
            DiskLabel.Text = Loc.Format("S.Health.Live.Free", drive.Name);
            DiskValue.Text = HealthRules.Size(drive.FreeBytes);
            Tone(DiskValue, DiskBar, HealthRules.DriveStatus(drive));
            Fill(DiskBar, 1 - drive.FreePercent / 100);
        }
        else
        {
            DiskLabel.Text = Loc.Format("S.Health.Live.Free", "");
            DiskValue.Text = "-";
            Fill(DiskBar, 0);
        }
    }

    private static void ShowTile(TextBlock value, Border bar, double? percent, HealthStatus status)
    {
        value.Text = percent is { } shown ? Math.Round(shown).ToString(CultureInfo.InvariantCulture) + "%" : "-";
        Tone(value, bar, status);
        Fill(bar, (percent ?? 0) / 100);
    }

    /// <summary>Цвет — только у того, что требует внимания, как и у строк разделов.</summary>
    private static void Tone(TextBlock value, Border bar, HealthStatus status)
    {
        var key = status switch
        {
            HealthStatus.Problem => "Status.Danger",
            HealthStatus.Attention => "Status.Warning",
            _ => null
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, key ?? "Text.Primary");
        bar.SetResourceReference(Border.BackgroundProperty, key ?? "Text.Dim");
    }

    private static void Fill(Border bar, double fraction)
    {
        if (bar.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = Math.Clamp(fraction, 0, 1);
        }
    }

    /// <summary>
    /// Собирает снимок заново; разделы обновляются по мере готовности. Прежние значения стоят,
    /// пока не придут новые: сброс всех строк в «Проверяю…» схлопывал панель, и она потом
    /// дёргалась, вырастая обратно.
    /// </summary>
    internal void Refresh()
    {
        Cancel();
        var cancel = _collect = new CancellationTokenSource();
        foreach (var area in Enum.GetValues<HealthArea>())
        {
            _cards.TryAdd(area, null);
        }

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        Render(null);
        Detached.Run(CollectAsync(cancel), "health_collect");
    }

    private async Task CollectAsync(CancellationTokenSource cancel)
    {
        try
        {
            var report = await HealthCollector.CollectAsync(
                card => Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(cancel, _collect))
                    {
                        _cards[card.Area] = card;
                        Render(null);
                    }
                }),
                cancel.Token,
                Probes);

            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(cancel, _collect))
                {
                    return;
                }

                _cache?.Store(report);
                _cards = report.Cards.ToDictionary(card => card.Area, card => (HealthCard?)card);
                _refreshing = false;
                Render(report.At);
                RefreshButton.IsEnabled = true;
            });
        }
        catch (OperationCanceledException)
        {
            // Закрыли панель или нажали «Обновить» ещё раз — этот сбор больше не нужен.
        }
    }

    /// <summary>Строки областей по порядку; ещё не готовые — «проверяю».</summary>
    internal static IReadOnlyList<HealthCardRow> Rows(IReadOnlyDictionary<HealthArea, HealthCard?> cards) =>
        Enum.GetValues<HealthArea>()
            .Select((area, index) => new HealthCardRow(area, cards.GetValueOrDefault(area), first: index == 0))
            .ToList();

    /// <summary>
    /// Итог под заголовком: пока проверка идёт — «Проверяю…», потом худший статус; при внимании
    /// и проблеме — какие разделы, чтобы не искать их глазами по списку.
    /// </summary>
    internal static HealthSummary Summarize(IReadOnlyDictionary<HealthArea, HealthCard?> cards)
    {
        var ready = cards.Values.Where(card => card is not null).Select(card => card!).ToList();
        if (ready.Count == 0 || ready.Count < cards.Count)
        {
            return new HealthSummary("Loading", Loc.Get("S.Health.Checking"));
        }

        // Худший статус, но «не проверено» не перекрывает найденную проблему.
        var found = ready.Where(card => card.Status != HealthStatus.Unknown).Select(card => card.Status).DefaultIfEmpty(HealthStatus.Unknown).Max();
        var overall = found is HealthStatus.Ok && ready.Any(card => card.Status == HealthStatus.Unknown) ? HealthStatus.Unknown : found;
        if (overall is HealthStatus.Attention or HealthStatus.Problem)
        {
            var areas = ready
                .Where(card => card.Status is HealthStatus.Attention or HealthStatus.Problem)
                .OrderByDescending(card => card.Status)
                .ThenBy(card => card.Area)
                .Select(card => Loc.Get(HealthRules.TitleKey(card.Area)).ToLowerInvariant());
            return new HealthSummary(overall.ToString(), Loc.Format("S.Health.Summary.Issues", string.Join(", ", areas)));
        }

        return new HealthSummary(overall.ToString(), Loc.Get("S.Health.Summary." + overall));
    }

    private void Render(DateTime? at)
    {
        Cards.ItemsSource = Rows(_cards);
        Summary.DataContext = _refreshing ? new HealthSummary("Loading", Loc.Get("S.Health.Checking")) : Summarize(_cards);

        // Итог словами — подсказкой у точки: строки разделов и так говорят, где замечание, а
        // повтор их фразой под заголовком только удлинял шапку.
        if (_refreshing)
        {
            UpdatedText.Text = Loc.Get("S.Health.Checking");
        }
        else if (at is { } time)
        {
            UpdatedText.Text = Updated(time, DateTime.Now, _format());
        }
    }

    /// <summary>«проверено в 01:12» сегодня, полная дата — раньше: дата у свежей проверки только удлиняла строку итога.</summary>
    internal static string Updated(DateTime at, DateTime now, DateFormat format) =>
        at.Date == now.Date
            ? Loc.Format("S.Health.UpdatedAt", ChatFormat.Clock(at))
            : Loc.Format("S.Health.Updated", ChatFormat.DateTimeShort(at, format));

    private void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HealthArea area } && _cards.GetValueOrDefault(area) is { } card)
        {
            AskRequested?.Invoke(HealthRules.Context(card));
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();

    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        }
    }

    /// <summary>Снимает идущий сбор и живые плитки — панель закрыли.</summary>
    internal void Cancel()
    {
        _live.Stop();
        var cancel = _collect;
        _collect = null;
        _refreshing = false;
        if (cancel is null)
        {
            return;
        }

        try
        {
            cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancel.Dispose();
        RefreshButton.IsEnabled = true;
    }
}

/// <summary>Раздел панели в том виде, в каком его рисует шаблон.</summary>
internal sealed class HealthCardRow(HealthArea area, HealthCard? card, bool first = false)
{
    public HealthArea Area { get; } = area;

    public string Title { get; } = Loc.Get(HealthRules.TitleKey(area));

    public Visibility DividerVisibility { get; } = first ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Имя значения <see cref="HealthStatus"/>; «Loading», пока идёт проверка.</summary>
    public string Tone { get; } = card is null ? "Loading" : card.Status.ToString();

    /// <summary>
    /// Итог раздела справа от имени. Пока идёт проверка — пусто: «Проверяю…» уже стоит строкой
    /// под именем.
    /// </summary>
    public string Status { get; } = card is null ? "" : Loc.Get("S.Health.Status." + card.Status);

    /// <summary>Значок раздела, сетка 16: диск, микросхема, щит, пульс, стрелка в лоток.</summary>
    public Geometry Glyph { get; } = Glyphs.Get(area switch
    {
        HealthArea.Disks => "M2.5,4 L13.5,4 L13.5,12 L2.5,12 Z M2.5,8.5 L13.5,8.5 M11,10.3 L11.01,10.3",
        HealthArea.System => "M4.5,4.5 L11.5,4.5 L11.5,11.5 L4.5,11.5 Z M6.5,2 L6.5,4.5 M9.5,2 L9.5,4.5 M6.5,11.5 L6.5,14 M9.5,11.5 L9.5,14 M2,6.5 L4.5,6.5 M2,9.5 L4.5,9.5 M11.5,6.5 L14,6.5 M11.5,9.5 L14,9.5",
        HealthArea.Security => "M8,1.8 L13,3.6 L13,7.6 C13,10.7 10.9,13.1 8,14.2 C5.1,13.1 3,10.7 3,7.6 L3,3.6 Z",
        HealthArea.Stability => "M1.5,8 L4.5,8 L6,4.5 L8.5,12 L10,8 L14.5,8",
        _ => "M8,2 L8,10 M4.8,7 L8,10.2 L11.2,7 M2.5,11.5 L2.5,13.5 L13.5,13.5 L13.5,11.5"
    });

    /// <summary>
    /// Строки раздела. Пока проверка идёт — одна «Проверяю…»; у снимка прежней версии строк нет,
    /// и показываются его факты.
    /// </summary>
    public IReadOnlyList<HealthItemRow> Items { get; } =
        card is null ? [HealthItemRow.Loading()]
        : card.Items.Count > 0 ? card.Items.Select(item => new HealthItemRow(item)).ToList()
        : card.Facts.Select(fact => new HealthItemRow(new HealthItem { Label = fact, Status = card.Status })).ToList();

    /// <summary>«Разобраться» — когда есть о чём: внимание или проблема.</summary>
    public Visibility AskVisibility { get; } =
        card is { Status: HealthStatus.Attention or HealthStatus.Problem } ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Строка раздела: подпись, полоска (у дисков) и значение.</summary>
internal sealed class HealthItemRow
{
    public HealthItemRow(HealthItem item, string? tone = null)
    {
        Label = item.Label;
        Value = item.Value;
        Tone = tone ?? item.Status.ToString();
        if (item.Used is { } used)
        {
            used = Math.Clamp(used, 0, 1);
            BarVisibility = Visibility.Visible;
            UsedWidth = new GridLength(used, GridUnitType.Star);
            FreeWidth = new GridLength(1 - used, GridUnitType.Star);
        }

        LabelSpan = Value.Length == 0 && item.Used is null ? 3 : 1;
    }

    public static HealthItemRow Loading() => new(new HealthItem { Label = Loc.Get("S.Health.Checking") }, "Loading");

    public string Label { get; }

    public string Value { get; }

    public string Tone { get; }

    public int LabelSpan { get; }

    public Visibility BarVisibility { get; } = Visibility.Collapsed;

    public GridLength UsedWidth { get; } = new(0, GridUnitType.Star);

    public GridLength FreeWidth { get; } = new(1, GridUnitType.Star);
}

/// <summary>Итог под заголовком: цвет точки и фраза.</summary>
internal sealed record HealthSummary(string Tone, string Title);
