using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Выбор модели и ключа к ней: провайдеры слева, модели посередине, ключи справа.
/// </summary>
/// <remarks>
/// До версии 1.23.0 панель показывала модели одного провайдера — того, чей ключ был активен, —
/// и о ключах не знала вовсе. Теперь слот выбирается из моделей любого провайдера, у которого
/// есть ключ, и к модели тут же выбирается ключ: заголовки чатов человек может отдать
/// бесплатной модели одного провайдера, пока разговор идёт у другого.
/// <para>
/// Вкладки «Рекомендованы» и «Все» убраны: списка теперь два измерения (провайдер и ключ),
/// и третье съело бы и место, и понятность. Сверху остался «Авто» — он не модель, а просьба
/// выбрать её маршрутизатором, и провайдера у него нет.
/// </para>
/// </remarks>
public partial class ModelPickerPanel : UserControl
{
    public static readonly DependencyProperty AllowAutoProperty =
        DependencyProperty.Register(
            nameof(AllowAuto),
            typeof(bool),
            typeof(ModelPickerPanel),
            new PropertyMetadata(true, OnAllowAutoChanged));

    /// <summary>
    /// Ключ строки подсказки у «Авто».
    /// </summary>
    /// <remarks>
    /// У разных слотов «Авто» значит разное: в чате — «маршрутизатор сам выберет модель»,
    /// у поиска в интернете — «искать там же, где идёт разговор». Ключ, а не готовый текст:
    /// подсказка обязана меняться вместе с языком интерфейса.
    /// </remarks>
    public static readonly DependencyProperty AutoTipKeyProperty =
        DependencyProperty.Register(
            nameof(AutoTipKey),
            typeof(string),
            typeof(ModelPickerPanel),
            new PropertyMetadata("S.Models.AutoTip", OnAutoTipKeyChanged));

    /// <summary>
    /// Пауза перед пересборкой списка после нажатия клавиши.
    /// </summary>
    /// <remarks>
    /// Человек печатает быстрее, чем строится список из сотен моделей. Без паузы каждый символ
    /// отправлял в мусор всю предыдущую сборку — набранное слово стоило столько же, сколько
    /// столько же полных перестроек.
    /// </remarks>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(140);

    private readonly Dictionary<LlmProvider, CatalogState> _catalogs = [];

    /// <summary>
    /// Ключ, выбранный у каждого провайдера. Помнится по провайдерам, а не одним полем: человек
    /// ходит по столбцу провайдеров, и выбранный у одного ключ не должен молча стать выбором
    /// у другого — у другого этого ключа нет вовсе.
    /// </summary>
    private readonly Dictionary<LlmProvider, string?> _keyChoice = [];

    private readonly Dictionary<LlmProvider, RadioButton> _providerItems = [];
    private readonly List<ModelPickerRow> _rows = [];

    private readonly string _providerGroup = "PickerProviders_" + Guid.NewGuid().ToString("N");
    private readonly string _keyGroup = "PickerKeys_" + Guid.NewGuid().ToString("N");

    private DispatcherTimer? _searchTimer;
    private DispatcherOperation? _pendingRebuild;

    private IReadOnlyList<ApiKeyEntry> _keys = [];
    private ModelBinding _selected = ModelBinding.Empty;
    private LlmProvider _shown = LlmProvider.Venice;

    /// <summary>
    /// Из чего собран показанный сейчас список моделей.
    /// </summary>
    /// <remarks>
    /// <c>Version = -1</c> значит «не собран ничем»: у настоящего каталога счётчик растёт
    /// с нуля, и совпасть эти состояния не могут.
    /// </remarks>
    private (LlmProvider Provider, string Search, bool Vision, bool Code, int Version) _built = Nothing;

    private static (LlmProvider Provider, string Search, bool Vision, bool Code, int Version) Nothing =>
        (default, "", false, false, -1);

    private bool _suppressProviderEvent;
    private bool _suppressKeyEvent;
    private bool _rebuilding;

    public ModelPickerPanel()
    {
        InitializeComponent();
        BindAutoLogo();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => Invalidate();
    }

    public bool AllowAuto
    {
        get => (bool)GetValue(AllowAutoProperty);
        set => SetValue(AllowAutoProperty, value);
    }

    public string AutoTipKey
    {
        get => (string)GetValue(AutoTipKeyProperty);
        set => SetValue(AutoTipKeyProperty, value);
    }

    public string SelectedModelId => _selected.ModelId;

    public string? SelectedKeyId => _selected.KeyId;

    /// <summary>Человек выбрал модель. Хозяин закрывает плашку: выбор сделан.</summary>
    public event EventHandler<ModelBinding>? ModelPicked;

    /// <summary>
    /// Человек сменил модели ключ.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="ModelPicked"/>, потому что закрывать плашку здесь нельзя: выбор
    /// ключа — половина дела, и человек обычно тут же выбирает под него модель. Пока оба
    /// действия шли одним событием, плашка захлопывалась от нажатия на ключ.
    /// </remarks>
    public event EventHandler<ModelBinding>? KeyPicked;

    /// <summary>У показанного провайдера нет ключа, и человек просит его завести.</summary>
    public event EventHandler? AddKeyRequested;

    /// <summary>
    /// Человек открыл столбец этого провайдера — хозяину пора подвезти его каталог.
    /// </summary>
    /// <remarks>
    /// Событием, а не запросом изнутри: панель не знает ни про ключи, ни про сеть, а каталогов
    /// теперь несколько, и грузить их все разом ради одного открытого попапа незачем.
    /// </remarks>
    public event EventHandler<LlmProvider>? ProviderShown;

    /// <summary>
    /// Ставит выбор, не трогая показанный столбец провайдеров.
    /// </summary>
    /// <remarks>
    /// Не трогая — потому что зовут это и когда плашка открыта: каталог доезжает, хозяин
    /// раздаёт полям текущие привязки, и прежняя редакция на этом месте возвращала столбец
    /// к провайдеру выбранной модели. Со стороны это выглядело так, будто переключение
    /// провайдера срабатывает через раз.
    /// </remarks>
    public void SetSelected(string modelId, string? keyId)
    {
        _selected = new ModelBinding(modelId ?? "", keyId);

        if (!VeniceModelCatalog.IsAuto(_selected.ModelId) &&
            !string.IsNullOrWhiteSpace(_selected.ModelId))
        {
            _keyChoice[_selected.Provider] = keyId;
        }

        RefreshMarks();
    }

    /// <summary>
    /// Открывает плашку на провайдере выбранной модели.
    /// </summary>
    /// <remarks>
    /// Зовётся ровно при открытии попапа: человек чаще смотрит, что стоит сейчас, чем ищет
    /// замену у соседа. У «Авто» провайдера нет — остаёмся там, где были.
    /// </remarks>
    public void ShowSelectedProvider()
    {
        if (!VeniceModelCatalog.IsAuto(_selected.ModelId) &&
            !string.IsNullOrWhiteSpace(_selected.ModelId))
        {
            _shown = _selected.Provider;
        }

        Invalidate();
    }

    /// <summary>Ключи программы — из них собирается правый столбец.</summary>
    public void SetKeys(IReadOnlyList<ApiKeyEntry> keys)
    {
        _keys = keys ?? [];
        Invalidate();
    }

    /// <summary>Имя ключа по его идентификатору. <c>null</c> — такого ключа больше нет.</summary>
    public string? LabelOf(string? keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            return null;
        }

        foreach (var key in _keys)
        {
            if (key.Id.Equals(keyId, StringComparison.Ordinal))
            {
                return key.Label;
            }
        }

        return null;
    }

    public void ShowLoading(LlmProvider provider)
    {
        var state = State(provider);
        state.Ready = false;
        state.Status = Loc.Get("S.Common.Loading");
        state.Version++;
        Invalidate();
    }

    public void SetCatalog(
        LlmProvider provider,
        IReadOnlyList<VeniceModelInfo> models,
        string? error = null)
    {
        var state = State(provider);
        var next = models ?? [];

        // Тот же каталог второй раз — не повод пересобирать список: хозяин раздаёт его всем
        // плашкам сразу, и открытая перестраивалась бы по разу на каждую раздачу.
        if (state.Ready &&
            ReferenceEquals(state.Models, next) &&
            string.Equals(state.Status, error, StringComparison.Ordinal))
        {
            return;
        }

        state.Models = next;
        state.Ready = true;
        state.Status = error;
        state.Version++;
        Invalidate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SmoothScroll.SetIsEnabled(KeyScroll, true);
        if (FindScroll(ModelItems) is { } scroll)
        {
            SmoothScroll.SetIsEnabled(scroll, true);
        }

        ThemeManager.EffectiveThemeChanged += OnThemeChanged;
        ApplyAllowAuto();
        Invalidate();
    }

    /// <summary>
    /// Снимает всё отложенное: плашку убрали с экрана, и доделывать ей нечего.
    /// </summary>
    /// <remarks>
    /// И пересборка, и отсчёт паузы поиска живут в очереди диспетчера — общей на всё окно.
    /// Оставленные там, они срабатывали бы уже после того, как плашки не стало.
    /// </remarks>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.EffectiveThemeChanged -= OnThemeChanged;

        _searchTimer?.Stop();
        if (_pendingRebuild is { Status: DispatcherOperationStatus.Pending } pending)
        {
            pending.Abort();
        }

        _pendingRebuild = null;
    }

    /// <summary>
    /// Логотипы строк разрешены по живым ресурсам и держат объект прежней темы — после смены
    /// список надо собрать заново.
    /// </summary>
    private void OnThemeChanged()
    {
        _built = Nothing;
        Invalidate();
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer found)
        {
            return found;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static void OnAllowAutoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModelPickerPanel panel)
        {
            panel.ApplyAllowAuto();
        }
    }

    private static void OnAutoTipKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModelPickerPanel panel)
        {
            panel.ApplyAutoTip();
        }
    }

    private void ApplyAllowAuto()
    {
        if (AutoItem is null)
        {
            return;
        }

        AutoItem.Visibility = AllowAuto ? Visibility.Visible : Visibility.Collapsed;
        BindAutoLogo();
        ApplyAutoTip();
    }

    /// <summary>
    /// Через ссылку на ресурс, а не готовой строкой: перевод интерфейса меняет словарь на лету,
    /// и присвоенный текст остался бы на прежнем языке.
    /// </summary>
    /// <remarks>
    /// Меняется содержимое подсказки, а не сама подсказка: объект <see cref="ToolTip"/> несёт
    /// размещение сбоку от плашки, и строка на его месте встала бы под «Авто», закрыв список.
    /// </remarks>
    private void ApplyAutoTip()
    {
        if (AutoTip is not null && !string.IsNullOrWhiteSpace(AutoTipKey))
        {
            AutoTip.SetResourceReference(ContentControl.ContentProperty, AutoTipKey);
        }
    }

    /// <summary>Зазор между плашкой и подсказкой строки, в единицах интерфейса.</summary>
    private const double SideTipGap = 8;

    /// <summary>
    /// Ставит подсказку строки сбоку от плашки, а не под строкой.
    /// </summary>
    /// <remarks>
    /// Общее правило программы — подсказка по центру под целью (<c>UiScale.PlaceUnderTarget</c>).
    /// В списке моделей оно вредно: карточка ложится на соседние строки как раз там, куда едет
    /// курсор. Колбэк ставится до показа, и раз он задан, общее правило подсказку не трогает.
    /// </remarks>
    private void ModelItem_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement row || row.ToolTip is not ToolTip tip)
        {
            return;
        }

        // Подсказка не в дереве строки, и данные ей передаются явно: у переиспользованного
        // контейнера она иначе могла бы показать модель, которая стояла в нём до прокрутки.
        if (!ReferenceEquals(tip.DataContext, row.DataContext))
        {
            tip.DataContext = row.DataContext;
        }

        tip.PlacementTarget = row;
        tip.Placement = PlacementMode.Custom;
        tip.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            // Строка могла уже уехать из дерева (список пересобран под курсором) — тогда
            // сдвиг от рамки не посчитать, и подсказка встаёт у самой строки справа.
            var left = row.IsDescendantOf(PanelFrame)
                ? row.TranslatePoint(new Point(0, 0), PanelFrame).X
                : 0;
            var frameWidth = row.IsDescendantOf(PanelFrame) ? PanelFrame.ActualWidth : row.ActualWidth;
            return PlaceBeside(popupSize, targetSize, left, row.ActualWidth, frameWidth);
        };
    }

    /// <summary>
    /// Два места для подсказки строки: справа от плашки и, если там не хватит экрана, слева.
    /// </summary>
    /// <remarks>
    /// Размеры WPF передаёт в пикселях окна подсказки, а сдвиги строки в плашке известны
    /// в единицах интерфейса. Множитель между ними берётся из самой строки — её ширина есть
    /// в обеих системах. Так он учитывает и DPI монитора, и подделанный DPI масштаба
    /// интерфейса, не спрашивая ни того, ни другого. Выбор из двух мест делает WPF: он берёт
    /// первое, которое помещается на экран.
    /// </remarks>
    /// <param name="rowLeft">Левый край строки от левого края плашки, в единицах интерфейса.</param>
    /// <param name="rowWidth">Ширина строки в единицах интерфейса.</param>
    /// <param name="frameWidth">Ширина плашки в единицах интерфейса.</param>
    internal static CustomPopupPlacement[] PlaceBeside(
        Size popupSize,
        Size targetSize,
        double rowLeft,
        double rowWidth,
        double frameWidth)
    {
        var scale = rowWidth > 0 && targetSize.Width > 0 ? targetSize.Width / rowWidth : 1;
        var y = (targetSize.Height - popupSize.Height) / 2;
        var right = (frameWidth - rowLeft + SideTipGap) * scale;
        var left = -(rowLeft + SideTipGap) * scale - popupSize.Width;
        return
        [
            new CustomPopupPlacement(new Point(right, y), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(new Point(left, y), PopupPrimaryAxis.Vertical)
        ];
    }

    private void BindAutoLogo()
    {
        if (AutoLogo is not null && TryFindResource("Auto") is ImageSource source)
        {
            ThemeImages.Assign(AutoLogo, "Auto", source);
            ModelBrand.ApplyLogoBox(AutoLogo, "Auto");
        }
    }

    private CatalogState State(LlmProvider provider)
    {
        if (!_catalogs.TryGetValue(provider, out var state))
        {
            state = new CatalogState();
            _catalogs[provider] = state;
        }

        return state;
    }

    private bool HasKey(LlmProvider provider)
    {
        foreach (var key in _keys)
        {
            if (key.Provider == provider && !key.IsBroken)
            {
                return true;
            }
        }

        return false;
    }

    private void AutoItem_Click(object sender, RoutedEventArgs e) => Pick(VeniceModelCatalog.AutoId, null);

    private void AddKeyButton_Click(object sender, RoutedEventArgs e) =>
        AddKeyRequested?.Invoke(this, EventArgs.Empty);

    private void ModelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            Pick(id, _keyChoice.GetValueOrDefault(_shown));
        }
    }

    /// <summary>
    /// Пересобирает список не на каждый символ, а когда человек перестал печатать.
    /// </summary>
    private void ModelSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = SearchDelay
        };

        _searchTimer.Tick -= OnSearchSettled;
        _searchTimer.Tick += OnSearchSettled;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OnSearchSettled(object? sender, EventArgs e)
    {
        _searchTimer?.Stop();
        Invalidate();
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e) => Invalidate();

    private void ShowProvider(LlmProvider provider)
    {
        if (_shown == provider)
        {
            return;
        }

        _shown = provider;

        // Поиск и фильтры принадлежали прежнему списку: у соседнего провайдера свои названия
        // моделей, и оставленное слово чаще всего не находит там ничего.
        if (ModelSearchBox is not null && ModelSearchBox.Text.Length > 0)
        {
            ModelSearchBox.Text = "";
        }

        Invalidate();

        // Синхронно: раздача уже разложенного каталога ничего не стоит, а отложенный вызов
        // в хозяина срабатывал бы после того, как плашку закрыли.
        ProviderShown?.Invoke(this, provider);
    }

    private void Pick(string id, string? keyId)
    {
        _selected = new ModelBinding(id, keyId);
        RefreshMarks();
        ModelPicked?.Invoke(this, _selected);
    }

    /// <summary>
    /// Человек выбрал ключ. Если показанная модель этого же провайдера — выбор применяется
    /// к ней сразу; иначе он запомнится и достанется той модели, которую выберут следующей.
    /// </summary>
    private void PickKey(string? keyId)
    {
        _keyChoice[_shown] = keyId;

        if (!VeniceModelCatalog.IsAuto(_selected.ModelId) &&
            !string.IsNullOrWhiteSpace(_selected.ModelId) &&
            _selected.Provider == _shown)
        {
            _selected = new ModelBinding(_selected.ModelId, keyId);
            KeyPicked?.Invoke(this, _selected);
        }
    }

    /// <summary>
    /// Помечает столбцы к пересборке и собирает их одним кадром.
    /// </summary>
    /// <remarks>
    /// Через диспетчер, а не сразу: хозяин за одно открытие плашки успевает позвать
    /// <see cref="SetKeys"/>, <see cref="SetCatalog"/> и <see cref="SetSelected"/> по разу на
    /// каждого провайдера, и прежняя редакция перестраивала все три столбца на каждый такой
    /// вызов. Здесь они сливаются в одну пересборку.
    /// <para>
    /// Панель живёт внутри <see cref="System.Windows.Controls.Primitives.Popup"/>, и в
    /// настройках таких полей восемь. Невидимая не собирается вовсе: сотни строк создавались бы
    /// там, где их никто не видит.
    /// </para>
    /// </remarks>
    private void Invalidate()
    {
        if (_rebuilding || _pendingRebuild is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        _pendingRebuild = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Rebuild));
    }

    /// <summary>Собирает столбцы прямо сейчас. Для тестов и первого показа.</summary>
    internal void RebuildNow()
    {
        _pendingRebuild?.Abort();
        _pendingRebuild = null;
        Rebuild();
    }

    private void Rebuild()
    {
        _pendingRebuild = null;
        if (_rebuilding || !IsVisible)
        {
            return;
        }

        _rebuilding = true;
        try
        {
            RebuildProviders();
            RebuildModels();
            RebuildKeys();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>
    /// Столбец провайдеров: кнопки заводятся один раз, дальше им лишь обновляют состояние.
    /// </summary>
    /// <remarks>
    /// Пересоздание переключало провайдера само: новая кнопка со снятым <c>IsChecked</c>
    /// поднимала <c>Checked</c> прямо внутри обработчика удаляемой соседки, и нажатие
    /// срабатывало через раз.
    /// </remarks>
    private void RebuildProviders()
    {
        if (ProviderItems is null)
        {
            return;
        }

        if (ProviderItems.Children.Count == 0)
        {
            foreach (var spec in ProviderSpec.All)
            {
                var provider = spec.Provider;
                var name = new TextBlock
                {
                    Text = spec.Name,
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                var item = new RadioButton
                {
                    Style = (Style)FindResource("ProviderItem"),
                    GroupName = _providerGroup,
                    Content = name
                };

                item.Checked += (_, _) =>
                {
                    if (!_suppressProviderEvent)
                    {
                        ShowProvider(provider);
                    }
                };

                _providerItems[provider] = item;
                ProviderItems.Children.Add(item);
            }
        }

        _suppressProviderEvent = true;
        try
        {
            foreach (var spec in ProviderSpec.All)
            {
                if (!_providerItems.TryGetValue(spec.Provider, out var item))
                {
                    continue;
                }

                var hasKey = HasKey(spec.Provider);
                item.IsChecked = spec.Provider == _shown;
                item.ToolTip = hasKey ? null : Loc.Format("S.Models.NoKeyHint", spec.Name);

                // Провайдер без ключа не прячется, а гаснет: увидев его бледным, человек
                // поймёт, что модели там есть, но платить за них пока нечем.
                if (item.Content is TextBlock label)
                {
                    label.SetResourceReference(
                        TextBlock.ForegroundProperty,
                        hasKey ? "Text.Tertiary" : "Text.Faint");
                }
            }
        }
        finally
        {
            _suppressProviderEvent = false;
        }
    }

    private void RebuildModels()
    {
        if (ModelItems is null || NoKeyPane is null)
        {
            return;
        }

        if (!HasKey(_shown))
        {
            NoKeyPane.Visibility = Visibility.Visible;
            ModelItems.Visibility = Visibility.Collapsed;
            SearchRow.Visibility = Visibility.Collapsed;
            AutoItem.Visibility = Visibility.Collapsed;
            NoKeyText.Text = Loc.Format("S.Models.NoKeyHint", ProviderSpec.For(_shown).Name);
            _built = Nothing;
            return;
        }

        NoKeyPane.Visibility = Visibility.Collapsed;
        ModelItems.Visibility = Visibility.Visible;
        SearchRow.Visibility = Visibility.Visible;
        AutoItem.Visibility = AllowAuto ? Visibility.Visible : Visibility.Collapsed;

        var state = State(_shown);
        var search = ModelSearchBox?.Text ?? "";
        var vision = VisionChip?.IsChecked == true;
        var code = CodeChip?.IsChecked == true;
        var want = (_shown, search, vision, code, state.Version);

        // Ничего не изменилось — список уже такой, какой нужен. Это и есть вся разница между
        // «плашка открывается мгновенно» и «плашка думает полсекунды».
        if (_built == want)
        {
            RefreshMarks();
            return;
        }

        _built = want;
        _rows.Clear();

        if (!state.Ready)
        {
            ShowStatus(state.Status ?? Loc.Get("S.Common.Loading"));
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.Status) && state.Models.Count == 0)
        {
            ShowStatus(state.Status);
            return;
        }

        foreach (var model in VeniceModelCatalog.FilterAllTab(state.Models, search, vision, code))
        {
            _rows.Add(ModelPickerRow.Build(this, model));
        }

        if (_rows.Count == 0)
        {
            ShowStatus(
                string.IsNullOrWhiteSpace(search) && !vision && !code
                    ? Loc.Get("S.Models.Empty")
                    : Loc.Get("S.Common.NothingFound"));
            return;
        }

        ModelStatus.Visibility = Visibility.Collapsed;
        ModelItems.ItemsSource = null;
        ModelItems.ItemsSource = _rows;
        RefreshMarks();
    }

    private void ShowStatus(string text)
    {
        ModelItems.ItemsSource = null;
        ModelStatus.Text = text;
        ModelStatus.Visibility = Visibility.Visible;
    }

    private void RebuildKeys()
    {
        if (KeyItems is null)
        {
            return;
        }

        KeyItems.Children.Clear();
        var chosen = _keyChoice.GetValueOrDefault(_shown);

        _suppressKeyEvent = true;
        try
        {
            KeyItems.Children.Add(CreateKeyItem(
                null,
                Loc.Get("S.Models.KeyDefault"),
                Loc.Get("S.Models.KeyDefaultTip"),
                string.IsNullOrWhiteSpace(chosen)));

            foreach (var key in _keys)
            {
                if (key.Provider != _shown || key.IsBroken)
                {
                    continue;
                }

                KeyItems.Children.Add(CreateKeyItem(
                    key.Id,
                    key.Label,
                    key.Masked,
                    string.Equals(key.Id, chosen, StringComparison.Ordinal)));
            }
        }
        finally
        {
            _suppressKeyEvent = false;
        }
    }

    /// <summary>Обновляет галочки, не трогая сами списки.</summary>
    private void RefreshMarks()
    {
        if (AutoCheck is not null)
        {
            AutoCheck.Visibility = VeniceModelCatalog.IsAuto(_selected.ModelId)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        foreach (var row in _rows)
        {
            row.IsSelected = row.Id.Equals(_selected.ModelId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private RadioButton CreateKeyItem(string? id, string label, string tooltip, bool selected)
    {
        var name = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        name.SetResourceReference(
            TextBlock.ForegroundProperty,
            selected ? "Text.Bright" : "Text.Muted");

        var item = new RadioButton
        {
            Style = (Style)FindResource("KeyItem"),
            GroupName = _keyGroup,
            Content = name,
            IsChecked = selected,
            ToolTip = tooltip
        };

        item.Checked += (_, _) =>
        {
            if (!_suppressKeyEvent)
            {
                PickKey(id);
            }
        };

        return item;
    }

    /// <summary>Каталог одного провайдера: что загружено и что сказать, если не загрузилось.</summary>
    private sealed class CatalogState
    {
        public IReadOnlyList<VeniceModelInfo> Models { get; set; } = [];

        public bool Ready { get; set; }

        public string? Status { get; set; }

        /// <summary>Растёт при каждой настоящей смене каталога — по нему видно, что пересобирать.</summary>
        public int Version { get; set; }
    }
}
