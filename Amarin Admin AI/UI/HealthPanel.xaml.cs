using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

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

    public HealthPanel()
    {
        InitializeComponent();
        SmoothScroll.SetIsEnabled(CardsScroll, true);
    }

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

        Focus();
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
        UpdatedText.Text = at is { } time ? " · " + Updated(time, DateTime.Now, _format()) : "";
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

    /// <summary>Снимает идущий сбор — панель закрыли.</summary>
    internal void Cancel()
    {
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
