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

    /// <summary>
    /// Показывать траты всех ключей разом или только выбранного.
    /// </summary>
    /// <remarks>
    /// Заводское — все: с версии 1.23.0 у каждого слота моделей свой ключ, и сумма по одному
    /// из них не отвечает на вопрос «сколько стоит программа».
    /// </remarks>
    private bool _allKeys = true;

    public SettingsKeyPage()
    {
        InitializeComponent();

        // Та же плавная прокрутка, что у боковой колонки и ленты чата.
        SmoothScroll.SetIsEnabled(KeyPageScroll, true);
        SmoothScroll.SetDragScroll(KeyPageScroll, true);

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
        _allKeys = services.Settings.SpendScopeAllKeys;
        ScopeAll.IsChecked = _allKeys;
        ScopeOne.IsChecked = !_allKeys;

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
    /// Дожидается разового переноса цен из переписок и только потом строит отчёт.
    /// </summary>
    /// <remarks>
    /// Обычно переносить уже нечего: это делается на запуске. Заход сюда — вторая попытка на
    /// случай, если та не удалась, и страховка для профиля, заведённого уже на ходу. Отметка
    /// в журнале закрывает дверь навсегда, поэтому лишним обходом диска это не станет.
    /// </remarks>
    private async Task BackfillThenReloadAsync()
    {
        if (_services is { } services)
        {
            await Task.Run(services.BackfillSpendLedger).ConfigureAwait(true);
        }

        await ReloadAsync(force: false).ConfigureAwait(true);
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

    private void Scope_Checked(object sender, RoutedEventArgs e)
    {
        var all = ReferenceEquals(sender, ScopeAll);
        if (all == _allKeys)
        {
            return;
        }

        _allKeys = all;
        if (!IsLoaded || _services is null)
        {
            return;
        }

        _services.Settings.SpendScopeAllKeys = all;
        _services.SettingsStore.Save(_services.Settings);
        Detached.Run(ReloadAsync(force: false), "spend_report");
    }

    private void SpendRefreshButton_Click(object sender, RoutedEventArgs e) =>
        Detached.Run(ReloadAsync(force: true), "spend_report");

    /// <summary>
    /// Ключи, по которым строится график в режиме «все».
    /// </summary>
    /// <remarks>
    /// Нерасшифрованные строки пропускаем: платить ими всё равно нечем, а журнал у них пустой.
    /// Повторы по одному и тому же секрету отсеет сам <see cref="SpendService"/> — там это
    /// знание и живёт.
    /// </remarks>
    private IReadOnlyList<ApiCredential> AllCredentials()
    {
        var list = new List<ApiCredential>();
        if (_services is null)
        {
            return list;
        }

        foreach (var entry in _services.KeyStore.List())
        {
            if (!entry.IsBroken)
            {
                list.Add(entry.Credential);
            }
        }

        return list;
    }

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

        ShowLoading();

        try
        {
            if (_allKeys)
            {
                var credentials = AllCredentials();
                var combined = await _spend
                    .GetReportAsync(credentials, _period, force, cancellation.Token)
                    .ConfigureAwait(true);

                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                ShowReport(combined);
                await ShowTotalBalanceAsync(credentials, cancellation.Token).ConfigureAwait(true);
                return;
            }

            var credential = _services.KeyStore.ActiveCredential();
            var report = await _spend
                .GetReportAsync(credential, _period, force, cancellation.Token)
                .ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            ShowReport(report);
            await ShowBalanceAsync(credential, cancellation.Token).ConfigureAwait(true);
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
    /// <remarks>
    /// Причина у собственного журнала бывает разная, и называть её надо честно: у Venice
    /// журнал есть, но открыт только админ-ключу, а у остальных провайдеров его нет вовсе.
    /// </remarks>
    private void ShowSourceNote(SpendStatus status)
    {
        var key = status switch
        {
            SpendStatus.Local => AnyProviderHasUsageHistory()
                ? "S.Spend.SourceLocal"
                : "S.Spend.SourceOnlyLocal",
            SpendStatus.Stale => "S.Spend.Stale",
            _ => null
        };

        SpendSourceNote.Text = key is null ? "" : Loc.Get(key);
        SpendSourceNote.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Есть ли среди показанных ключей хоть один, у чьего провайдера журнал трат вообще
    /// существует.
    /// </summary>
    /// <remarks>
    /// В режиме «все ключи» провайдер не один, и подпись «журнал открыт только админ-ключу»
    /// была бы враньём, если бы в сумме не было ни одного ключа Venice.
    /// </remarks>
    private bool AnyProviderHasUsageHistory()
    {
        if (_services is null)
        {
            return true;
        }

        if (!_allKeys)
        {
            return ProviderSpec.For(_services.KeyStore.ActiveProvider()).HasUsageHistory;
        }

        foreach (var credential in AllCredentials())
        {
            if (ProviderSpec.For(credential.Provider).HasUsageHistory)
            {
                return true;
            }
        }

        return false;
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

    private async Task ShowBalanceAsync(ApiCredential credential, CancellationToken cancellationToken)
    {
        if (_services is null || credential.IsEmpty)
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
            ShowLeftTile(credential.Provider, usd, limits.SpentUsd);
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
            if (cached is { } known)
            {
                ShowLeftTile(credential.Provider, known, spent: null);
            }
            else
            {
                BalanceCreditsValue.Text = "—";
            }

            BalanceNote.Text = Loc.Get("S.Spend.Error");
        }
    }

    /// <summary>
    /// Плашки остатка в режиме «все ключи»: сумма по всем, кредиты — по ключам Venice.
    /// </summary>
    /// <remarks>
    /// Кредиты — величина Venice со своим курсом к доллару; пересчитывать по нему остаток
    /// чужого провайдера значило бы показать число, которого нет нигде.
    /// <para>
    /// Опрос последовательный, как и в строках самих ключей: Venice ограничивает частоту, и
    /// веер по всем ключам сразу отвечал бы отказом ровно тогда, когда ключей стало много.
    /// Ключ, который не ответил, просто не попадает в сумму — прочерк вместо всей суммы был
    /// бы хуже.
    /// </para>
    /// </remarks>
    private async Task ShowTotalBalanceAsync(
        IReadOnlyList<ApiCredential> credentials,
        CancellationToken cancellationToken)
    {
        if (_services is null || credentials.Count == 0)
        {
            BalanceUsdValue.Text = "—";
            BalanceCreditsValue.Text = "—";
            BalanceNote.Text = Loc.Get("S.Spend.NoKey");
            return;
        }

        var total = 0m;
        var venice = 0m;
        var answered = false;

        foreach (var credential in credentials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var limits = await _services.Venice
                    .GetRateLimitsAsync(credential, cancellationToken)
                    .ConfigureAwait(true);

                var usd = (limits.Balances?.Usd ?? 0m) + (limits.Balances?.BundledCredits ?? 0m);
                total += usd;
                if (credential.Provider == LlmProvider.Venice)
                {
                    venice += usd;
                }

                answered = true;
            }
            catch (Exception exception) when (exception is VeniceApiException or HttpRequestException)
            {
            }
        }

        BalanceCreditsCaption.SetResourceReference(TextBlock.TextProperty, "S.Key.Balance.Credits");

        if (!answered)
        {
            BalanceUsdValue.Text = "—";
            BalanceCreditsValue.Text = "—";
            BalanceNote.Text = Loc.Get("S.Spend.Error");
            return;
        }

        BalanceUsdValue.Text = FormatUsd(total);
        BalanceCreditsValue.Text = SpendReport
            .ToCredits(venice)
            .ToString("0.##", CultureInfo.InvariantCulture);
        BalanceNote.Text = Loc.Format("S.Key.Balance.AllKeys", credentials.Count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Левая плашка: у Venice — остаток в кредитах, у остальных — сколько всего потрачено.
    /// </summary>
    /// <remarks>
    /// Кредиты — величина Venice со своим курсом к доллару, и пересчитывать по нему чужой счёт
    /// значило бы показать число, которого нет нигде. Правая плашка и так показывает остаток
    /// в долларах — повторять его слева незачем, а вот сколько с ключа ушло, видно больше нигде.
    /// </remarks>
    private void ShowLeftTile(LlmProvider provider, decimal usd, decimal? spent)
    {
        if (provider == LlmProvider.Venice)
        {
            BalanceCreditsCaption.SetResourceReference(
                TextBlock.TextProperty, "S.Key.Balance.Credits");
            BalanceCreditsValue.Text = SpendReport
                .ToCredits(usd)
                .ToString("0.##", CultureInfo.InvariantCulture);
            return;
        }

        BalanceCreditsCaption.SetResourceReference(TextBlock.TextProperty, "S.Key.Balance.Left");
        BalanceCreditsValue.Text = spent is { } value ? FormatUsd(value) : "—";
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

        // Убранный ключ окружения виден строкой под списком. Без неё вернуть его можно было бы
        // только через переменные среды Windows: заново вставить то же значение программа не
        // даёт — она не кладёт ключ из окружения себе на диск.
        HiddenKeyRows.ItemsSource = _services.KeyStore.HiddenEnvironment()
            .Select(BuildHiddenKeyRow)
            .ToList();

        // Удалять и добавлять ключи на середине хода нельзя: у слотов, которым ключ не
        // назначен, он подбирается по провайдеру, и исчезнувший ключ увёл бы следующий раунд
        // в отказ, который читается как сетевой сбой. Выбор кружком под запрет не попадает —
        // он давно уже только про траты.
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
    /// <para>
    /// Деньги стоят на второй строке, у маски ключа. На первой их место заняли название, обе
    /// плашки и три кнопки — в ширину настроек это уже не помещалось, и плашка «окружение»
    /// обрезалась до «окр».
    /// </para>
    /// </remarks>
    private UIElement BuildKeyRow(ApiKeyEntry entry)
    {
        var revealed = _revealed.Contains(entry.Id);

        var choose = new RadioButton
        {
            Style = (Style)FindResource("KeyChoice"),
            IsChecked = entry.IsActive,
            Margin = new Thickness(0, 0, 10, 0),
            IsEnabled = !entry.IsBroken,
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

        // Провайдер — у каждой строки, а не только у своих: две строки окружения различаются
        // лишь названием переменной, и с одного взгляда их не разобрать. Он же объясняет,
        // почему после выбора этого ключа поменялся весь список моделей.
        head.Children.Add(Badge(
            ProviderSpec.For(entry.Provider).Name, Loc.Get("S.Key.ProviderTip")));

        if (entry.Source == ApiKeySource.Environment)
        {
            // В плашке одно слово: полная фраза её распирает и обрезается многоточием,
            // а объяснение уходит в подсказку, где на него есть место.
            head.Children.Add(Badge(
                Loc.Get("S.Key.BadgeEnvironment"), Loc.Get("S.Key.FromEnvironment")));
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

        // Своя статистика у каждого ключа: траты за выбранный отрезок и остаток на нём.
        // Справа, но на второй строке, рядом с маской ключа: на первой её место заняли бы
        // название и плашки, и «окружение» обрезалось бы до «окр».
        var stats = new TextBlock
        {
            Text = entry.IsBroken ? "" : Loc.Get("S.Key.StatsLoading"),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 3, 0, 0)
        };
        stats.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");
        _keyStats[entry.Id] = stats;

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(value);
        Grid.SetColumn(stats, 1);
        bottom.Children.Add(stats);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(head);
        text.Children.Add(bottom);

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

        // Переименование есть у любой строки, включая окружение: маска у всех ключей одинаковая,
        // и между двумя рабочими человек выбирает по названию, а не по «vk-•••••••••ab12».
        var rename = new Button
        {
            Style = (Style)FindResource("KeyIconButton"),
            Content = "✎",
            ToolTip = Loc.Get("S.Key.Rename")
        };
        rename.Click += (_, _) => RenameKey(entry);
        buttons.Children.Add(rename);

        var remove = new Button
        {
            Style = (Style)FindResource("KeyIconButton"),
            Content = "🗑",
            ToolTip = Loc.Get("S.Common.Delete")
        };
        remove.Click += (_, _) => RemoveKey(entry);
        buttons.Children.Add(remove);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(choose);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(buttons, 2);
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
    /// Строка убранного ключа окружения: чего лишилась программа и кнопка «Вернуть».
    /// </summary>
    /// <remarks>
    /// Нарочно не карточка ключа: ни кружка выбора, ни глаза, ни трат — этим ключом программа
    /// не платит. Одна приглушённая строка, которая говорит, что переменная в Windows осталась
    /// на месте.
    /// </remarks>
    private UIElement BuildHiddenKeyRow(ApiKeyEntry entry)
    {
        var caption = new TextBlock
        {
            Text = Loc.Format("S.Key.Removed", entry.Label),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");

        var restore = new Button
        {
            Style = (Style)FindResource("AddKeyButton"),
            Content = Loc.Get("S.Key.Restore"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        restore.Click += (_, _) => RestoreKey(entry);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(caption);
        Grid.SetColumn(restore, 1);
        grid.Children.Add(restore);

        return new Border
        {
            Padding = new Thickness(12, 7, 8, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid
        };
    }

    /// <summary>Мелкая плашка рядом с названием ключа.</summary>
    private Border Badge(string text, string tip)
    {
        var caption = new TextBlock { Text = text, FontSize = 10 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        return new Border
        {
            Style = (Style)FindResource("KeyBadge"),
            Child = caption,
            ToolTip = tip
        };
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
    private async Task LoadKeyStatsAsync(IReadOnlyList<ApiKeyEntry> entries)
    {
        if (_services is null || _spend is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Secret is not { Length: > 0 } ||
                !_keyStats.TryGetValue(entry.Id, out var line))
            {
                continue;
            }

            var parts = new List<string>();
            try
            {
                var report = await _spend
                    .GetReportAsync(entry.Credential, _period, force: false, CancellationToken.None)
                    .ConfigureAwait(true);
                if (report.Status is SpendStatus.Ready or SpendStatus.Stale or SpendStatus.Local)
                {
                    parts.Add(Loc.Format("S.Key.StatsSpent", FormatUsd(report.TotalUsd)));
                }

                var limits = await _services.Venice
                    .GetRateLimitsAsync(entry.Credential, CancellationToken.None)
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

    /// <summary>
    /// Кружок в списке: чьи траты и чей остаток показывать.
    /// </summary>
    /// <remarks>
    /// Идущий ход этому больше не помеха. Прежде выбранный ключ задавал провайдера всем слотам
    /// сразу, и смена посреди хода уводила его следующий раунд на другой сервер; теперь ключ
    /// хода снят при его начале и с выбором на этой странице не связан.
    /// </remarks>
    private void MakeActive(ApiKeyEntry entry)
    {
        if (_services is null || entry.IsActive)
        {
            return;
        }

        _services.KeyStore.SetActive(entry.Id);
        _services.ApplyActiveKey();

        // Остаток на плашке принадлежит выбранному ключу — он и сбрасывается. Каталоги моделей
        // остаются: выбор ключа здесь не двигает ни один слот.
        if (Window.GetWindow(this) is MainWindow owner)
        {
            owner.OnSelectedKeyChanged();
        }

        RefreshKeyRows();
        Detached.Run(ReloadAsync(force: true), "spend_report");
    }

    private void RemoveKey(ApiKeyEntry entry)
    {
        if (Window.GetWindow(this) is not MainWindow owner)
        {
            return;
        }

        // Удаление меняет активный ключ — следующим годным становится другой, возможно чужого
        // провайдера. Посреди хода это увело бы его следующий раунд на другой сервер.
        if (TurnsRunning)
        {
            RefreshKeyRows();
            return;
        }

        // У строки окружения вопрос другой: переменную в Windows программа не трогает и
        // тронуть не может, она лишь перестаёт брать её ключ. Обещать удаление там, где его
        // не будет, — врать человеку в единственном месте, где он может передумать.
        owner.AskKeyRemoval(
            entry.Label,
            entry.RemovalOnlyHides ? "S.Key.DeleteDesc.Environment" : "S.Key.DeleteDesc",
            () =>
            {
                if (_services is null)
                {
                    return;
                }

                _services.KeyStore.Remove(entry.Id);
                _services.ApplyActiveKey();
                owner.OnActiveKeyChanged(entry.Provider);
                RefreshKeyRows();
                Detached.Run(ReloadAsync(force: true), "spend_report");
            });
    }

    /// <summary>
    /// Возвращает убранный ключ окружения.
    /// </summary>
    /// <remarks>
    /// Запрет на середине хода тот же, что и у удаления: слот без назначенного ключа берёт
    /// ключ по провайдеру, и вернувшаяся строка сменила бы его следующему раунду.
    /// </remarks>
    private void RestoreKey(ApiKeyEntry entry)
    {
        if (_services is null || TurnsRunning)
        {
            RefreshKeyRows();
            return;
        }

        _services.KeyStore.Restore(entry.Id);
        _services.ApplyActiveKey();
        if (Window.GetWindow(this) is MainWindow owner)
        {
            owner.OnActiveKeyChanged(entry.Provider);
        }

        RefreshKeyRows();
        Detached.Run(ReloadAsync(force: true), "spend_report");
    }

    /// <summary>
    /// Переименование строки списка.
    /// </summary>
    /// <remarks>
    /// Идущий ход этому не помеха: название не решает, чем платят, — в отличие от удаления и
    /// добавления, которые меняют ключ у слотов, его не назначивших.
    /// </remarks>
    private void RenameKey(ApiKeyEntry entry)
    {
        if (Window.GetWindow(this) is not MainWindow owner)
        {
            return;
        }

        owner.AskKeyRename(entry.Label, name =>
        {
            if (_services is null)
            {
                return;
            }

            _services.KeyStore.Rename(entry.Id, name);

            // Плашка остатка подписывает суммы названиями ключей — после переименования
            // в подсказке осталось бы прежнее.
            owner.OnSelectedKeyChanged();
            RefreshKeyRows();
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
    /// <param name="removed">
    /// Убранные ключи окружения — строки под списком. Пусто, если убирать было нечего.
    /// </param>
    internal void ShowForShot(
        SpendReport report,
        IReadOnlyList<ApiKeyEntry> keys,
        IReadOnlyList<ApiKeyEntry>? removed = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(keys);

        HiddenKeyRows.ItemsSource = (removed ?? []).Select(BuildHiddenKeyRow).ToList();

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
