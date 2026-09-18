using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Страница «Key &amp; Info»: остаток, траты по дням, разбивка по моделям и список ключей.
/// </summary>
/// <remarks>
/// Отдельным <see cref="UserControl"/>, а не ещё одной простынёй в разметке главного окна:
/// та и без того на четыре тысячи строк.
/// <para>
/// График строится по собственному журналу трат программы (<see cref="SpendLedger"/>): в него
/// пишет единственная точка учёта денег, через которую проходят ответы, придуманные заголовки
/// чатов, скрытые сводки, поиск в сети, чтение страниц, картинки, агент и SynGuard. Журнал
/// самого Venice полнее, но отдаётся только админ-ключу, а для запросов к моделям человек
/// держит обычный; когда ключ всё-таки админский, берутся данные Venice.
/// </para>
/// <para>
/// Ключ Venice до версии 1.22.0 программа хранить отказывалась намеренно. Теперь хранит —
/// зашифрованным средствами Windows (<see cref="DataProtector"/>): расшифровать его сможет
/// только та учётная запись, под которой его вводили.
/// </para>
/// </remarks>
public partial class SettingsKeyPage : UserControl
{
    private AppServices? _services;
    private SpendService? _spend;

    /// <summary>Отменяет предыдущую загрузку: человек щёлкает отрезки быстрее, чем отвечает сеть.</summary>
    private CancellationTokenSource? _loading;

    /// <summary>Ключи, раскрытые «глазом». Сбрасывается при каждом уходе со страницы.</summary>
    private readonly HashSet<string> _revealed = new(StringComparer.Ordinal);

    /// <summary>Строка «потрачено · остаток» у каждого ключа: заполняется, когда ответит сеть.</summary>
    private readonly Dictionary<string, TextBlock> _keyStats = new(StringComparer.Ordinal);

    private SpendPeriod _period = SpendPeriod.Week;

    public SettingsKeyPage()
    {
        InitializeComponent();

        // Та же плавная прокрутка, что у боковой колонки и ленты чата.
        SmoothScroll.SetIsEnabled(KeyPageScroll, true);

        // Раскрытый ключ не должен пережить уход со страницы: настройки закрывают и уходят
        // от компьютера, а ключ так и остался бы на экране.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                _revealed.Clear();
            }
        };
    }

    /// <summary>Ставится главным окном, когда службы уже собраны.</summary>
    internal void Attach(AppServices services)
    {
        _services = services;
        var root = services.Profiles.DataRootFor(services.ProfileRegistry.ActiveProfileId);
        _spend = new SpendService(services.Venice, new SpendHistoryStore(root), services.Ledger)
        {
            ResolveModels = () => services.Models.Cached
        };
    }

    /// <summary>
    /// Зовётся при заходе именно на эту страницу, а не при открытии настроек: сеть и обход
    /// диска ради страницы, куда человек чаще всего не заходит, платить не должны.
    /// </summary>
    internal void Activate()
    {
        RefreshKeyRows();
        Detached.Run(BackfillThenReloadAsync(), "spend_report");
    }

    /// <summary>
    /// Один раз переносит в журнал трат цены, уже записанные в переписках, и только потом
    /// строит отчёт.
    /// </summary>
    /// <remarks>
    /// Иначе у обновившегося человека график был бы пуст, хотя цена каждого ответа давно лежит
    /// в его же файлах чатов. Обход диска делается ровно однажды — отметка в журнале закрывает
    /// эту дверь навсегда.
    /// </remarks>
    private async Task BackfillThenReloadAsync()
    {
        if (_services is { } services)
        {
            var secret = services.KeyStore.ActiveSecret();
            await Task.Run(() =>
            {
                try
                {
                    services.Ledger.Backfill(secret, ReadSavedSessions(services));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Перенос — удобство, а не обязанность: не вышло, значит график начнётся
                    // с ближайшего ответа.
                }
            }).ConfigureAwait(true);
        }

        await ReloadAsync(force: false).ConfigureAwait(true);
    }

    private static IEnumerable<ChatSession> ReadSavedSessions(AppServices services)
    {
        foreach (var entry in services.ChatStore.List())
        {
            if (services.ChatStore.TryLoad(entry.Id) is { } session)
            {
                yield return session;
            }
        }
    }

    // ───────────────────────── траты ─────────────────────────

    private void Period_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse<SpendPeriod>(tag, out var period) ||
            period == _period)
        {
            return;
        }

        _period = period;
        if (IsLoaded && _services is not null)
        {
            Detached.Run(ReloadAsync(force: false), "spend_report");
        }
    }

    private void SpendRefreshButton_Click(object sender, RoutedEventArgs e) =>
        Detached.Run(ReloadAsync(force: true), "spend_report");

    private async Task ReloadAsync(bool force)
    {
        if (_services is null || _spend is null)
        {
            return;
        }

        // Прежнюю загрузку отменяем, но источник не трогаем: его дочитает тот, кто его завёл.
        _loading?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loading = cancellation;

        var secret = _services.KeyStore.ActiveSecret();
        ShowLoading();

        try
        {
            var report = await _spend
                .GetReportAsync(secret, _period, force, cancellation.Token)
                .ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            ShowReport(report);
            await ShowBalanceAsync(secret, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowLoading()
    {
        SpendTotalCaption.Text = Loc.Get(SpendPeriods.LabelKey(_period));
        Chart.SetSeries([], Loc.Get("S.Spend.Loading"), DateFormat);
    }

    private void ShowReport(SpendReport report)
    {
        SpendTotalCaption.Text = Loc.Get(SpendPeriods.LabelKey(_period));
        SpendTotalUsd.Text = FormatUsd(report.TotalUsd);
        SpendTotalCredits.Text = Loc.Format(
            "S.Spend.TotalCredits",
            SpendReport.ToCredits(report.TotalUsd).ToString("0.##", CultureInfo.InvariantCulture));

        Chart.SetSeries(report.Points, EmptyNote(report), DateFormat);
        BuildModelRows(report);
        ShowSourceNote(report.Status);
    }

    /// <summary>
    /// Откуда взяты цифры. Молчит, когда данные из журнала самого Venice, — это и есть
    /// ожидаемое положение дел, объяснять нечего.
    /// </summary>
    private void ShowSourceNote(SpendStatus status)
    {
        var key = status switch
        {
            SpendStatus.Local => "S.Spend.SourceLocal",
            SpendStatus.Stale => "S.Spend.Stale",
            _ => null
        };

        SpendSourceNote.Text = key is null ? "" : Loc.Get(key);
        SpendSourceNote.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string EmptyNote(SpendReport report) => report.Status switch
    {
        SpendStatus.NoKey => Loc.Get("S.Spend.NoKey"),
        SpendStatus.Failed => Loc.Get("S.Spend.Error"),
        _ => Loc.Get("S.Spend.Empty")
    };

    private void BuildModelRows(SpendReport report)
    {
        var rows = new List<UIElement>();
        var top = report.Models.Count > 0 ? report.Models.Max(row => row.Usd) : 0m;

        foreach (var row in report.Models)
        {
            rows.Add(BuildModelRow(row, top));
        }

        ModelRows.ItemsSource = rows;
        ModelsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildModelRow(SpendModelRow row, decimal top)
    {
        var name = new TextBlock
        {
            Text = row.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");

        var amount = new TextBlock
        {
            Text = FormatUsd(row.Usd),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        amount.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");

        // Полоса доли: глазами она отвечает на «на что ушло больше» быстрее, чем четыре
        // знака после запятой в столбце цифр.
        var fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = top > 0 ? Math.Max(2, 220 * (double)(row.Usd / top)) : 2
        };
        fill.SetResourceReference(Border.BackgroundProperty, "Accent.Fill");

        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 7, 0, 0),
            Child = fill
        };
        track.SetResourceReference(Border.BackgroundProperty, "Bg.Raised");

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(name);
        Grid.SetColumn(amount, 1);
        head.Children.Add(amount);

        var column = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        column.Children.Add(head);
        column.Children.Add(track);

        // Сырой sku — в подсказку: по нему человек поймёт, за что списали, если наш разбор
        // промахнулся, и сможет переслать строку как есть.
        if (!string.IsNullOrWhiteSpace(row.Detail))
        {
            column.ToolTip = row.Detail;
        }

        return column;
    }

    // ───────────────────────── остаток ─────────────────────────

    private async Task ShowBalanceAsync(string secret, CancellationToken cancellationToken)
    {
        if (_services is null || string.IsNullOrWhiteSpace(secret))
        {
            BalanceUsdValue.Text = "—";
            BalanceCreditsValue.Text = "—";
            BalanceNote.Text = Loc.Get("S.Spend.NoKey");
            return;
        }

        try
        {
            var limits = await _services.Venice
                .GetRateLimitsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            var usd = (limits.Balances?.Usd ?? 0m) + (limits.Balances?.BundledCredits ?? 0m);
            BalanceUsdValue.Text = FormatUsd(usd);
            BalanceCreditsValue.Text = SpendReport
                .ToCredits(usd)
                .ToString("0.##", CultureInfo.InvariantCulture);
            BalanceNote.Text = BuildBalanceNote(limits);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is VeniceApiException or HttpRequestException)
        {
            // Остаток из заголовков прошлого ответа — лучше, чем прочерк; он же лежит на плашке.
            var cached = _services.Venice.LastBalance?.Usd;
            BalanceUsdValue.Text = cached is { } value ? FormatUsd(value) : "—";
            BalanceCreditsValue.Text = cached is { } known
                ? SpendReport.ToCredits(known).ToString("0.##", CultureInfo.InvariantCulture)
                : "—";
            BalanceNote.Text = Loc.Get("S.Spend.Error");
        }
    }

    private static string BuildBalanceNote(VeniceRateLimitsData limits)
    {
        var parts = new List<string>();
        if (!limits.AccessPermitted)
        {
            parts.Add(Loc.Get("S.Key.NoAccess"));
        }

        if (limits.Balances?.Diem is > 0 and { } diem)
        {
            parts.Add(Loc.Format("S.Key.Balance.Diem", diem.ToString("0.##", CultureInfo.InvariantCulture)));
        }

        return string.Join("  ·  ", parts);
    }

    // ───────────────────────── ключи ─────────────────────────

    internal void RefreshKeyRows()
    {
        if (_services is null)
        {
            return;
        }

        _keyStats.Clear();
        var rows = new List<UIElement>();
        var entries = _services.KeyStore.List();
        foreach (var entry in entries)
        {
            rows.Add(BuildKeyRow(entry));
        }

        KeyRows.ItemsSource = rows;
        Detached.Run(LoadKeyStatsAsync(entries), "key_stats");

        // Менять ключ на середине хода нельзя: половина запросов ушла бы с одним ключом,
        // половина с другим, а неверный новый ключ убил бы ход отказом, который читается
        // как сетевой сбой.
        KeysBusyNote.Visibility = TurnsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool TurnsRunning => Window.GetWindow(this) is MainWindow owner && owner.HasRunningTurns;

    /// <summary>
    /// Строка ключа: кружок выбора, название с меткой источника, сам ключ под ним, справа —
    /// деньги и кнопки.
    /// </summary>
    /// <remarks>
    /// Две строки текста, а не четыре. Раньше название, ключ, источник и статистика стояли
    /// столбиком друг под другом, и карточка читалась как стена: глазу не за что зацепиться,
    /// а высота росла вдвое против нужной. Источник теперь метка рядом с названием, деньги —
    /// справа, где их и ищут.
    /// </remarks>
    private UIElement BuildKeyRow(VeniceKeyEntry entry)
    {
        var revealed = _revealed.Contains(entry.Id);

        var choose = new RadioButton
        {
            Style = (Style)FindResource("KeyChoice"),
            IsChecked = entry.IsActive,
            Margin = new Thickness(0, 0, 10, 0),
            IsEnabled = !entry.IsBroken && !TurnsRunning,
            ToolTip = Loc.Get(entry.IsActive ? "S.Key.Active" : "S.Key.MakeActive")
        };
        choose.Checked += (_, _) => MakeActive(entry);

        var label = new TextBlock
        {
            Text = entry.Label,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Bright");

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(label);

        if (entry.Source == VeniceKeySource.Environment)
        {
            // В плашке одно слово: полная фраза её распирает и обрезается многоточием,
            // а объяснение уходит в подсказку, где на него есть место.
            var badgeText = new TextBlock { Text = Loc.Get("S.Key.BadgeEnvironment"), FontSize = 10 };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
            var badge = new Border
            {
                Style = (Style)FindResource("KeyBadge"),
                Child = badgeText,
                ToolTip = Loc.Get("S.Key.FromEnvironment")
            };
            head.Children.Add(badge);
        }

        // Моноширинный: маска из точек и сам ключ иначе прыгают по ширине при раскрытии.
        var value = new TextBlock
        {
            Text = entry.IsBroken
                ? Loc.Get("S.Key.Unreadable")
                : revealed ? entry.Secret : entry.Masked,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        value.SetResourceReference(
            TextBlock.ForegroundProperty, entry.IsBroken ? "Status.Danger" : "Text.Faint");

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(head);
        text.Children.Add(value);

        // Своя статистика у каждого ключа: траты за выбранный отрезок и остаток на нём.
        // Справа и в одну строку — так она не спорит с названием за первую строку карточки.
        var stats = new TextBlock
        {
            Text = entry.IsBroken ? "" : Loc.Get("S.Key.StatsLoading"),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0)
        };
        stats.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");
        _keyStats[entry.Id] = stats;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!entry.IsBroken)
        {
            var eye = new Button
            {
                Style = (Style)FindResource("KeyIconButton"),
                Content = revealed ? "🙈" : "👁",
                ToolTip = Loc.Get(revealed ? "S.Key.Hide" : "S.Key.Show")
            };
            eye.Click += (_, _) => ToggleReveal(entry.Id);
            buttons.Children.Add(eye);
        }

        if (entry.CanRemove)
        {
            var remove = new Button
            {
                Style = (Style)FindResource("KeyIconButton"),
                Content = "🗑",
                ToolTip = Loc.Get("S.Common.Delete")
            };
            remove.Click += (_, _) => RemoveKey(entry);
            buttons.Children.Add(remove);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(choose);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(stats, 2);
        grid.Children.Add(stats);
        Grid.SetColumn(buttons, 3);
        grid.Children.Add(buttons);

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 8, 9),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid
        };

        // Активный ключ виден рамкой и фоном посветлее: галочку в кружке легко пропустить,
        // когда ключей несколько и все подписаны похоже.
        card.SetResourceReference(
            Border.BackgroundProperty, entry.IsActive ? "Bg.Selected" : "Bg.Card");
        card.SetResourceReference(
            Border.BorderBrushProperty, entry.IsActive ? "Accent.Fill" : "Border.Default");
        return card;
    }

    /// <summary>
    /// Дозаполняет строки ключей: сколько по каждому потрачено за выбранный отрезок и сколько
    /// на нём осталось.
    /// </summary>
    /// <remarks>
    /// По ключу на запрос, по очереди, а не разом: ключей у человека единицы, а Venice считает
    /// частоту обращений. Отказ по одному ключу гасит только его строку — остальные свои цифры
    /// покажут.
    /// </remarks>
    private async Task LoadKeyStatsAsync(IReadOnlyList<VeniceKeyEntry> entries)
    {
        if (_services is null || _spend is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Secret is not { Length: > 0 } secret ||
                !_keyStats.TryGetValue(entry.Id, out var line))
            {
                continue;
            }

            var parts = new List<string>();
            try
            {
                var report = await _spend
                    .GetReportAsync(secret, _period, force: false, CancellationToken.None)
                    .ConfigureAwait(true);
                if (report.Status is SpendStatus.Ready or SpendStatus.Stale or SpendStatus.Local)
                {
                    parts.Add(Loc.Format("S.Key.StatsSpent", FormatUsd(report.TotalUsd)));
                }

                var limits = await _services.Venice
                    .GetRateLimitsAsync(secret, CancellationToken.None)
                    .ConfigureAwait(true);
                var left = (limits.Balances?.Usd ?? 0m) + (limits.Balances?.BundledCredits ?? 0m);
                parts.Add(Loc.Format("S.Key.StatsLeft", FormatUsd(left)));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is VeniceApiException or HttpRequestException)
            {
            }

            // Строку могли пересобрать, пока ждали сеть: пишем только в ту, что ещё на экране.
            if (_keyStats.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, line))
            {
                line.Text = parts.Count > 0
                    ? string.Join("  ·  ", parts)
                    : Loc.Get("S.Key.StatsUnknown");
            }
        }
    }

    private void ToggleReveal(string id)
    {
        if (!_revealed.Add(id))
        {
            _revealed.Remove(id);
        }

        RefreshKeyRows();
    }

    private void MakeActive(VeniceKeyEntry entry)
    {
        if (_services is null || entry.IsActive || TurnsRunning)
        {
            return;
        }

        _services.KeyStore.SetActive(entry.Id);
        _services.ApplyActiveKey();

        // У другого ключа свой остаток и свой доступ к моделям: и плашка, и каталог врут,
        // пока их не сбросить.
        if (Window.GetWindow(this) is MainWindow owner)
        {
            owner.OnActiveKeyChanged();
        }

        RefreshKeyRows();
        Detached.Run(ReloadAsync(force: true), "spend_report");
    }

    private void RemoveKey(VeniceKeyEntry entry)
    {
        if (Window.GetWindow(this) is not MainWindow owner)
        {
            return;
        }

        owner.AskKeyRemoval(entry.Label, () =>
        {
            if (_services is null)
            {
                return;
            }

            _services.KeyStore.Remove(entry.Id);
            _services.ApplyActiveKey();
            owner.OnActiveKeyChanged();
            RefreshKeyRows();
            Detached.Run(ReloadAsync(force: true), "spend_report");
        });
    }

    private void AddKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow owner)
        {
            owner.OpenKeyDialog();
        }
    }

    /// <summary>Зовётся окном, когда человек добавил ключ через модалку.</summary>
    internal void OnKeyAdded()
    {
        RefreshKeyRows();
        Detached.Run(ReloadAsync(force: true), "spend_report");
    }

    /// <summary>
    /// Наполняет страницу готовыми данными, без сети и без служб — для снимка в тесте.
    /// </summary>
    /// <remarks>
    /// Иначе увидеть страницу целиком можно было бы только запустив программу, а её окно
    /// вылезет поверх того, чем человек занят.
    /// </remarks>
    internal void ShowForShot(SpendReport report, IReadOnlyList<VeniceKeyEntry> keys)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(keys);

        BalanceCreditsValue.Text = "371";
        BalanceUsdValue.Text = "$3.71";
        ShowReport(report);

        _keyStats.Clear();
        KeyRows.ItemsSource = keys.Select(BuildKeyRow).ToList();
        foreach (var entry in keys.Where(item => !item.IsBroken))
        {
            _keyStats[entry.Id].Text = Loc.Format("S.Key.StatsSpent", "$0.88") + "  ·  " +
                                       Loc.Format("S.Key.StatsLeft", "$3.71");
        }
    }

    /// <summary>Наводит курсор на точку графика — для снимка в тесте.</summary>
    internal void HoverForShot(int index) => Chart.HoverForShot(index);

    private DateFormat DateFormat => _services?.Settings.DateFormat ?? DateFormat.DayMonthShort;

    /// <summary>Доллары так же, как их показывает график: один формат на всю страницу.</summary>
    internal static string FormatUsd(decimal value) => SpendReport.FormatUsd(value);
}
