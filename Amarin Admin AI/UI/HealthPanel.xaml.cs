using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Панель «Состояние ПК» (C4): пять карточек — диски, система, защита, стабильность,
/// обновления — с итогом по порогам <see cref="HealthRules"/> и кнопкой «Разобраться».
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

    /// <summary>Собирает снимок заново; карточки заполняются по мере готовности.</summary>
    internal void Refresh()
    {
        Cancel();
        var cancel = _collect = new CancellationTokenSource();
        _cards = Enum.GetValues<HealthArea>().ToDictionary(area => area, _ => (HealthCard?)null);
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
                Render(report.At);
                RefreshButton.IsEnabled = true;
            });
        }
        catch (OperationCanceledException)
        {
            // Закрыли панель или нажали «Обновить» ещё раз — этот сбор больше не нужен.
        }
    }

    /// <summary>Строки карточек в порядке областей; ещё не готовые — «проверяю».</summary>
    internal static IReadOnlyList<HealthCardRow> Rows(IReadOnlyDictionary<HealthArea, HealthCard?> cards) =>
        Enum.GetValues<HealthArea>()
            .Select(area => new HealthCardRow(area, cards.GetValueOrDefault(area)))
            .ToList();

    private void Render(DateTime? at)
    {
        var rows = Rows(_cards);
        Cards.ItemsSource = rows;

        var ready = _cards.Values.Where(card => card is not null).Select(card => card!).ToList();
        var overall = ready.Count == 0 ? HealthStatus.Unknown : ready.Max(card => card.Status);
        OverallPill.DataContext = new HealthCardRow(HealthArea.System,
            ready.Count == _cards.Count && ready.Count > 0 ? new HealthCard { Status = overall } : null);

        UpdatedText.Text = at is { } time
            ? Loc.Format("S.Health.Updated", ChatFormat.DateTimeShort(time, _format()))
            : Loc.Get("S.Health.Checking");
    }

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

/// <summary>Карточка в том виде, в каком её рисует шаблон.</summary>
internal sealed class HealthCardRow(HealthArea area, HealthCard? card)
{
    public HealthArea Area { get; } = area;

    public string Title { get; } = Loc.Get(HealthRules.TitleKey(area));

    /// <summary>Имя значения <see cref="HealthStatus"/> — по нему шаблон красит точку; «Loading», пока идёт проверка.</summary>
    public string Tone { get; } = card is null ? "Loading" : card.Status.ToString();

    public string StatusText { get; } = card is null ? Loc.Get("S.Health.Checking") : Loc.Get(HealthRules.StatusKey(card.Status));

    public IReadOnlyList<string> Facts { get; } = card?.Facts ?? [];

    /// <summary>«Разобраться» — когда есть о чём: внимание или проблема.</summary>
    public Visibility AskVisibility { get; } =
        card is { Status: HealthStatus.Attention or HealthStatus.Problem } ? Visibility.Visible : Visibility.Collapsed;
}
