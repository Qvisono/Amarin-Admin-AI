using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Общая плашка выбора модели, ключа и параметров размышления.
/// </summary>
public partial class ModelPickerPanel : UserControl
{
    private const double CompactLayoutWidth = 440;

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
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(75);

    private readonly Dictionary<LlmProvider, CatalogState> _catalogs = [];

    /// <summary>
    /// Ключ, выбранный у каждого провайдера. Помнится по провайдерам, а не одним полем: человек
    /// ходит по столбцу провайдеров, и выбранный у одного ключ не должен молча стать выбором
    /// у другого — у другого этого ключа нет вовсе.
    /// </summary>
    private readonly Dictionary<LlmProvider, string?> _keyChoice = [];

    private readonly Dictionary<LlmProvider, RadioButton> _providerItems = [];
    private readonly Dictionary<string, ImageSource?> _rowLogos = new(StringComparer.Ordinal);
    private readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ModelPickerRow> _rows = [];
    private ModelPickerRow? _markedRow;

    private readonly string _providerGroup = "PickerProviders_" + Guid.NewGuid().ToString("N");
    private readonly string _keyGroup = "PickerKeys_" + Guid.NewGuid().ToString("N");

    private DispatcherTimer? _searchTimer;
    private DispatcherOperation? _pendingRebuild;

    private IReadOnlyList<ApiKeyEntry> _keys = [];
    private ModelBinding _selected = ModelBinding.Empty;
    private LlmProvider _shown = LlmProvider.Venice;

    private string _variant = "";
    private string _route = "";
    private string[] _catalogVariants = [];
    private (LlmProvider Provider, string Search, bool Vision, bool Code, bool Favorites, string Variant, string Route,
        int Version, OrganizationPolicy Policy)? _built;
    private (LlmProvider Provider, string? KeyId, IReadOnlyList<ApiKeyEntry> Keys)? _builtKeys;
    private ReasoningChoice? _reasoning;
    private IReadOnlyList<string> _efforts = [];
    private IReadOnlyList<string> _speeds = [];
    private bool _withTools = true;
    private ThemePresetInfo? _logoTheme;
    private string? _language;

    private bool _suppressProviderEvent;
    private bool _suppressKeyEvent;
    private bool _updatingFilters;
    private bool _updatingChoice;
    private bool _rebuilding;
    private bool _compactLayout;

    public ModelPickerPanel()
    {
        InitializeComponent();
        EffortSlider.PreviewChanged += EffortSlider_PreviewChanged;
        BuildRouteFilters();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                CloseSelectors();
                EffortSlider.CancelPreview();
            }

            Invalidate();
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shadow = PanelFrame.Margin;
        var horizontalShadow = shadow.Left + shadow.Right;
        var size = new Size(
            Math.Min(availableSize.Width, PanelFrame.MaxWidth + horizontalShadow),
            Math.Min(availableSize.Height, PanelFrame.MaxHeight + shadow.Top + shadow.Bottom));

        // Popup сначала меряет без ограничений, затем повторяет замер в доступной области экрана.
        var compact = size.Width - horizontalShadow < CompactLayoutWidth;
        if (_compactLayout != compact)
        {
            _compactLayout = compact;
            SetCompactLayout(compact);
        }

        base.MeasureOverride(size);
        return size;
    }

    private void SetCompactLayout(bool compact)
    {
        SidebarColumn.Width = new GridLength(compact ? 0 : 128);
        Grid.SetColumnSpan(SelectorFields, compact ? 2 : 1);
        Grid.SetColumnSpan(SelectorMenus, compact ? 2 : 1);

        Grid.SetColumnSpan(ProviderField, compact ? 1 : 2);
        ProviderField.Margin = compact ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 0, 6);
        Grid.SetRow(KeyField, compact ? 0 : 1);
        Grid.SetColumn(KeyField, compact ? 1 : 0);
        Grid.SetColumnSpan(KeyField, compact ? 1 : 2);
        KeyField.Margin = compact ? new Thickness(4, 0, 0, 0) : default;

        Grid.SetColumnSpan(ProviderMenu, compact ? 1 : 2);
        ProviderMenu.Margin = compact ? new Thickness(0, 0, 4, 0) : default;
        Grid.SetColumn(KeyMenu, compact ? 1 : 0);
        Grid.SetColumnSpan(KeyMenu, compact ? 1 : 2);
        KeyMenu.Margin = compact ? new Thickness(4, 0, 0, 0) : default;

        Grid.SetRow(ModelsPane, compact ? 1 : 0);
        Grid.SetRowSpan(ModelsPane, compact ? 1 : 2);
        Grid.SetColumn(ModelsPane, compact ? 0 : 1);
        Grid.SetColumnSpan(ModelsPane, compact ? 2 : 1);
        ModelsPane.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
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

    public ReasoningChoice? SelectedReasoning => _reasoning;

    /// <summary>Чей столбец моделей сейчас перед глазами.</summary>
    public LlmProvider ShownProvider => _shown;

    /// <summary>Модель выбрана. Для ручной модели плашка остаётся открытой.</summary>
    public event EventHandler<ModelBinding>? ModelPicked;

    public event EventHandler<ReasoningChoice>? ReasoningChanged;

    public event EventHandler<string>? FavoriteToggled;

    /// <summary>
    /// Человек сменил модели ключ.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="ModelPicked"/>, потому что закрывать плашку здесь нельзя: выбор
    /// ключа — половина дела, и человек обычно тут же выбирает под него модель. Пока оба
    /// действия шли одним событием, плашка захлопывалась от нажатия на ключ.
    /// </remarks>
    public event EventHandler<ModelBinding>? KeyPicked;

    /// <summary>
    /// У показанного провайдера нет ключа, и человек просит его завести. Провайдер едет с
    /// событием: окно ключа открывается сразу на нём.
    /// </summary>
    public event EventHandler<LlmProvider>? AddKeyRequested;

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
        var next = new ModelBinding(modelId ?? "", keyId);
        if (next == _selected)
        {
            return;
        }

        EffortSlider.CancelPreview();
        _selected = next;
        if (!VeniceModelCatalog.IsAuto(_selected.ModelId) &&
            !string.IsNullOrWhiteSpace(_selected.ModelId))
        {
            _keyChoice[_selected.Provider] = keyId;
        }

        RefreshMarks();
        Invalidate();
    }

    /// <summary>Слоты без настроек размышления передают null.</summary>
    public void SetReasoning(ReasoningChoice? choice, bool withTools = true)
    {
        if (_reasoning == choice && _withTools == withTools)
        {
            return;
        }

        EffortSlider.CancelPreview();
        _reasoning = choice;
        _withTools = withTools;
        Invalidate();
    }

    public void SetFavorites(IReadOnlySet<string> favorites)
    {
        if (_favorites.SetEquals(favorites))
        {
            return;
        }

        _favorites.Clear();
        _favorites.UnionWith(favorites);
        RefreshFavorites();
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
        CloseSelectors();
        EffortSlider.CancelPreview();
        if (!VeniceModelCatalog.IsAuto(_selected.ModelId) &&
            !string.IsNullOrWhiteSpace(_selected.ModelId))
        {
            _shown = _selected.Provider;
            _route = ModelRef.OpenRouterRouting(_selected.ModelId);
            if (_variant.Length != 0)
            {
                _variant = ModelRef.OpenRouterCatalogVariant(_selected.ModelId);
            }
        }

        Invalidate();
    }

    /// <summary>Ключи программы — из них собирается меню ключей.</summary>
    public void SetKeys(IReadOnlyList<ApiKeyEntry> keys)
    {
        if (ReferenceEquals(_keys, keys))
        {
            return;
        }

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

    /// <remarks>
    /// Уже загруженный список новая попытка не прячет: модели остаются на месте, пока идёт
    /// запрос, — так просил человек, и так не мигает открытая плашка.
    /// </remarks>
    public void ShowLoading(LlmProvider provider)
    {
        var state = State(provider);
        if (state.Ready && state.Error is null)
        {
            return;
        }

        state.Ready = false;
        state.Error = null;
        state.Version++;
        Invalidate();
    }

    public void SetCatalog(LlmProvider provider, IReadOnlyList<VeniceModelInfo> models)
    {
        var state = State(provider);
        var next = models ?? [];

        // Тот же каталог второй раз — не повод пересобирать список: хозяин раздаёт его всем
        // плашкам сразу, и открытая перестраивалась бы по разу на каждую раздачу.
        if (state.Ready && state.Error is null && ReferenceEquals(state.Models, next))
        {
            return;
        }

        state.Models = next;
        state.Rows.Clear();
        state.Variants = null;
        state.Ready = true;
        state.Error = null;
        state.Version++;
        Invalidate();
    }

    /// <summary>
    /// Список не загрузился: вместо моделей — причина и кнопка «Повторить».
    /// </summary>
    /// <remarks>
    /// Модели, загруженные раньше, остаются: отказ при повторной попытке не повод их прятать.
    /// </remarks>
    public void SetCatalogError(LlmProvider provider, string error)
    {
        var state = State(provider);
        if (state.Models.Count > 0)
        {
            state.Ready = true;
            state.Error = null;
            state.Version++;
            Invalidate();
            return;
        }

        state.Ready = true;
        state.Error = error;
        state.Version++;
        Invalidate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.EffectiveThemeChanged += OnThemeChanged;
        LanguageManager.LanguageChanged += OnLanguageChanged;
        ApplyAllowAuto();
        if (_logoTheme != ThemeManager.Current)
        {
            OnThemeChanged();
        }

        if (_language != LanguageManager.Current)
        {
            OnLanguageChanged();
        }

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
        LanguageManager.LanguageChanged -= OnLanguageChanged;

        _searchTimer?.Stop();
        if (_pendingRebuild is { Status: DispatcherOperationStatus.Pending } pending)
        {
            pending.Abort();
        }

        _pendingRebuild = null;
    }

    private void OnThemeChanged()
    {
        _logoTheme = ThemeManager.Current;
        _rowLogos.Clear();
        foreach (var row in _rows)
        {
            row.RefreshTheme();
        }
    }

    private void OnLanguageChanged()
    {
        _language = LanguageManager.Current;
        foreach (var catalog in _catalogs.Values)
        {
            foreach (var row in catalog.Rows.Values)
            {
                row.RefreshLanguage();
            }
        }

        _built = null;
        _builtKeys = null;
        _speeds = [];
        Invalidate();
    }

    internal ImageSource? RowLogo(string key)
    {
        if (!_rowLogos.TryGetValue(key, out var logo))
        {
            logo = TryFindResource(key) as ImageSource;
            _rowLogos.Add(key, logo);
        }

        return logo;
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

        AutoItem.Visibility = AllowAuto && FavoritesChip?.IsChecked != true
            ? Visibility.Visible : Visibility.Collapsed;
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

        // Данные берём при показе: контейнер мог перейти к другой модели, а текст нужен только при наведении.
        if (row.DataContext is ModelPickerRow modelRow)
        {
            tip.Content = modelRow.Tooltip;
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
        AddKeyRequested?.Invoke(this, _shown);

    private void ModelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && OrganizationPolicy.Current.AllowsModel(id))
        {
            Pick(id, _keyChoice.GetValueOrDefault(_shown));
        }
    }

    private void FavoriteStar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: ModelPickerRow row })
        {
            return;
        }

        if (!_favorites.Add(row.FavoriteId))
        {
            _favorites.Remove(row.FavoriteId);
        }

        RefreshFavorites();
        FavoriteToggled?.Invoke(this, row.FavoriteId);
    }

    private void RefreshFavorites()
    {
        foreach (var row in _rows)
        {
            row.IsFavorite = _favorites.Contains(row.FavoriteId);
        }

        if (FavoritesChip?.IsChecked == true)
        {
            _built = null;
            Invalidate();
        }
    }

    private void Selector_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ProviderSelector))
        {
            KeySelector.IsChecked = false;
        }
        else
        {
            ProviderSelector.IsChecked = false;
        }

        ShowSelectorMenus();
        if (ProviderMenu.IsVisible)
        {
            FocusSelectedItem(ProviderItems);
        }
        else if (KeyMenu.IsVisible)
        {
            FocusSelectedItem(KeyItems);
        }
    }

    private static void FocusSelectedItem(Panel items) =>
        (items.Children.OfType<RadioButton>().FirstOrDefault(item => item.IsChecked == true)
            ?? items.Children.OfType<RadioButton>().FirstOrDefault())?.Focus();

    private void ShowSelectorMenus()
    {
        ProviderMenu.Visibility = ProviderSelector.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        KeyMenu.Visibility = KeySelector.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseSelectors()
    {
        ProviderSelector.IsChecked = false;
        KeySelector.IsChecked = false;
        ShowSelectorMenus();
    }

    private void Panel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target &&
            !ProviderMenu.IsAncestorOf(target) && !ProviderSelector.IsAncestorOf(target) &&
            !KeyMenu.IsAncestorOf(target) && !KeySelector.IsAncestorOf(target))
        {
            CloseSelectors();
        }
    }

    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (ProviderMenu.IsVisible || KeyMenu.IsVisible))
        {
            var toggle = ProviderMenu.IsVisible ? ProviderSelector : KeySelector;
            CloseSelectors();
            toggle.Focus();
            e.Handled = true;
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
        RebuildResults();
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e) => RebuildResults();

    private void BuildRouteFilters()
    {
        RouteFilter.Items.Add(FilterItem("", "S.Models.RouteDefault"));
        foreach (var route in ModelRef.OpenRouterRoutingVariants)
        {
            var item = FilterItem(route);
            var tip = route switch
            {
                ModelRef.NitroVariant => "S.Models.FilterNitroTip",
                ModelRef.FloorVariant => "S.Models.VariantFloor",
                ModelRef.ExactoVariant => "S.Models.VariantExacto",
                _ => null
            };
            if (tip is not null)
            {
                item.SetResourceReference(ToolTipProperty, tip);
            }

            RouteFilter.Items.Add(item);
        }

        RouteFilter.SelectedIndex = 0;
        VariantFilter.Items.Add(FilterItem("", "S.Models.VariantAll"));
        VariantFilter.SelectedIndex = 0;
    }

    private static ComboBoxItem FilterItem(string value, string? resource = null)
    {
        var item = new ComboBoxItem { Tag = value, Content = value };
        if (resource is not null)
        {
            item.SetResourceReference(ContentControl.ContentProperty, resource);
        }

        return item;
    }

    private void VariantFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFilters || VariantFilter is null || RouteFilter is null)
        {
            return;
        }

        _variant = FilterValue(VariantFilter);
        _route = FilterValue(RouteFilter);
        RebuildResults();
    }

    private static string FilterValue(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string value } ? value : "";

    private void RebuildResults()
    {
        if (IsVisible && !_rebuilding && _pendingRebuild is not { Status: DispatcherOperationStatus.Pending })
        {
            RebuildModels();
        }
    }

    private void ShowProvider(LlmProvider provider)
    {
        if (_shown == provider)
        {
            return;
        }

        EffortSlider.CancelPreview();
        _shown = provider;
        _variant = "";
        _route = "";

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
        var binding = new ModelBinding(id, keyId);
        if (_selected != binding)
        {
            EffortSlider.CancelPreview();
            _selected = binding;
        }

        RefreshMarks();
        RefreshChoice();
        ModelPicked?.Invoke(this, _selected);
    }

    private void RefreshChoice()
    {
        if (ChoicePane is null)
        {
            return;
        }

        var id = _selected.ModelId;
        if (_reasoning is not { } choice || id.Length == 0 || VeniceModelCatalog.IsAuto(id) ||
            _selected.Provider != _shown || !HasKey(_shown))
        {
            EffortSlider.CancelPreview();
            ChoicePane.Visibility = Visibility.Collapsed;
            return;
        }

        var model = ChoiceModel();
        var effective = ReasoningPolicy.Effective(choice, model, _withTools);
        var canDisable = ReasoningPolicy.CanDisable(model);
        var efforts = ReasoningPolicy.VisibleEffortOptions(model, _withTools);
        if (!_efforts.SequenceEqual(efforts, StringComparer.Ordinal))
        {
            EffortSlider.CancelPreview();
            _efforts = efforts;
        }
        var speeds = ModelSpeeds.For(id);
        ChoicePane.Visibility = canDisable || _efforts.Count > 0 || speeds.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        _updatingChoice = true;
        try
        {
            ThinkingControl.Visibility = canDisable ? Visibility.Visible : Visibility.Collapsed;
            ThinkingToggle.IsChecked = !effective.DisableThinking;
            EffortControl.Visibility = _efforts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_efforts.Count > 0)
            {
                var effort = ReasoningPolicy.ClampEffort(effective.Effort, model, _withTools);
                var index = 0;
                while (index < _efforts.Count - 1 && !string.Equals(_efforts[index], effort, StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                }

                EffortSlider.SetLevel(index, _efforts.Count);
            }

            RefreshChoiceLabels(choice, model);
            RefreshSpeed(speeds, ModelSpeeds.Pick(choice, id));
        }
        finally
        {
            _updatingChoice = false;
        }
    }

    private VeniceModelInfo? ChoiceModel() => _markedRow?.Model ??
        ReasoningPolicy.Find(State(_shown).Models, ModelRef.WithoutOpenRouterRouting(_selected.ModelId));

    private void RefreshChoiceLabels(ReasoningChoice choice, VeniceModelInfo? model)
    {
        var preview = EffortSlider.PreviewIndex;
        var index = preview ?? (int)EffortSlider.Value;
        var effort = (uint)index < (uint)_efforts.Count ? _efforts[index] : null;
        if (preview is not null && effort is not null)
        {
            choice = choice with { DisableThinking = false, Effort = effort };
        }

        ChoiceModelLabel.Text = ModelPickerField.ModelLabel(_selected.ModelId, choice, model, _withTools);
        EffortValue.Text = ReasoningPolicy.EffortLabel(effort);
    }

    private void RefreshSpeed(IReadOnlyList<string> speeds, string? selected)
    {
        SpeedPicker.Visibility = speeds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (speeds.Count == 0)
        {
            return;
        }

        if (!_speeds.SequenceEqual(speeds))
        {
            _speeds = speeds;
            SpeedPicker.Items.Clear();
            foreach (var speed in speeds)
            {
                SpeedPicker.Items.Add(new ComboBoxItem { Content = ModelSpeeds.Label(speed), Tag = speed });
            }

            SpeedPicker.Items.Insert(speeds[0] == ModelSpeeds.Flex ? 1 : 0,
                new ComboBoxItem { Content = ModelSpeeds.Label(null), Tag = "" });
        }

        SelectFilter(SpeedPicker, selected ?? "");
    }

    private void EffortSlider_PreviewChanged(object? sender, EventArgs e)
    {
        if (!_updatingChoice && _reasoning is { } choice)
        {
            RefreshChoiceLabels(choice, ChoiceModel());
        }
    }

    private void EffortSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var index = (int)Math.Round(e.NewValue);
        if (!_updatingChoice && _reasoning is { } choice && (uint)index < (uint)_efforts.Count)
        {
            UpdateChoice(choice with { DisableThinking = false, Effort = _efforts[index] });
        }
    }

    private void ThinkingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_updatingChoice && _reasoning is { } choice)
        {
            UpdateChoice(choice with { DisableThinking = ThinkingToggle.IsChecked != true });
        }
    }

    private void SpeedPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingChoice && _reasoning is { } choice && SpeedPicker is { SelectedIndex: >= 0 })
        {
            var speed = FilterValue(SpeedPicker);
            UpdateChoice(choice with { Speed = speed.Length == 0 ? null : speed });
        }
    }

    private void UpdateChoice(ReasoningChoice choice)
    {
        if (_reasoning == choice)
        {
            return;
        }

        _reasoning = choice;
        RefreshChoice();
        ReasoningChanged?.Invoke(this, choice);
    }

    /// <summary>
    /// Человек выбрал ключ. Если показанная модель этого же провайдера — выбор применяется
    /// к ней сразу; иначе он запомнится и достанется той модели, которую выберут следующей.
    /// </summary>
    private void PickKey(string? keyId)
    {
        _keyChoice[_shown] = keyId;
        RefreshKeyLabel(keyId);

        if (!VeniceModelCatalog.IsAuto(_selected.ModelId) &&
            !string.IsNullOrWhiteSpace(_selected.ModelId) &&
            _selected.Provider == _shown)
        {
            EffortSlider.CancelPreview();
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
            RefreshChoice();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>
    /// Провайдеры столбца: встроенные и свои серверы, к которым есть ключ; сначала те, чем
    /// можно платить, потом бледные.
    /// </summary>
    /// <remarks>
    /// Встроенных с 1.34.0 дюжина, и у человека с одним-двумя ключами нужные терялись бы среди
    /// остальных. Порядок внутри каждой половины — порядок <see cref="ProviderSpec.All"/>, свои
    /// серверы — за встроенными.
    /// </remarks>
    private List<ProviderSpec> ProviderOrder()
    {
        var specs = new List<ProviderSpec>(ProviderSpec.All);
        foreach (var key in _keys)
        {
            if (key.Provider.IsCustom && key.Endpoint is { } endpoint &&
                specs.All(spec => spec.Provider != key.Provider))
            {
                specs.Add(ProviderSpec.ForCustom(key.Provider, endpoint));
            }
        }

        // Провайдера, запрещённого политикой организации (1.37.0), в столбце нет вовсе.
        specs.RemoveAll(spec => !OrganizationPolicy.Current.AllowsProvider(spec.Provider));

        return [.. specs.Where(spec => HasKey(spec.Provider)), .. specs.Where(spec => !HasKey(spec.Provider))];
    }

    private string NameOf(LlmProvider provider) =>
        ProviderOrder().FirstOrDefault(spec => spec.Provider == provider)?.Name ?? ProviderSpec.For(provider).Name;

    /// <summary>
    /// Столбец провайдеров: кнопка заводится один раз на провайдера, дальше ей лишь обновляют
    /// состояние и место.
    /// </summary>
    /// <remarks>
    /// Пересоздание переключало провайдера само: новая кнопка со снятым <c>IsChecked</c>
    /// поднимала <c>Checked</c> прямо внутри обработчика удаляемой соседки, и нажатие
    /// срабатывало через раз. Поэтому новый ключ добавляет недостающую кнопку, удалённый свой
    /// сервер убирает свою, а живые кнопки только переставляются.
    /// </remarks>
    private void RebuildProviders()
    {
        if (ProviderItems is null)
        {
            return;
        }

        var order = ProviderOrder();

        // Свой сервер исчезает вместе с последним ключом. Был показан он — показываем первый
        // в списке, иначе столбец моделей остался бы ничьим.
        if (order.Count > 0 && order.All(spec => spec.Provider != _shown))
        {
            _shown = order[0].Provider;
            _variant = "";
            _route = "";
        }

        ProviderLabel.Text = order.FirstOrDefault(spec => spec.Provider == _shown)?.Name ?? "";
        foreach (var gone in _providerItems.Keys.Where(provider => order.All(spec => spec.Provider != provider)).ToList())
        {
            ProviderItems.Children.Remove(_providerItems[gone]);
            _providerItems.Remove(gone);
        }

        _suppressProviderEvent = true;
        try
        {
            for (var index = 0; index < order.Count; index++)
            {
                var spec = order[index];
                if (!_providerItems.TryGetValue(spec.Provider, out var item))
                {
                    item = CreateProviderItem(spec.Provider);
                    _providerItems[spec.Provider] = item;
                }

                if (index >= ProviderItems.Children.Count || !ReferenceEquals(ProviderItems.Children[index], item))
                {
                    ProviderItems.Children.Remove(item);
                    ProviderItems.Children.Insert(index, item);
                }

                var hasKey = HasKey(spec.Provider);
                item.IsChecked = spec.Provider == _shown;
                item.ToolTip = hasKey ? null : Loc.Format("S.Models.NoKeyHint", spec.Name);

                // Провайдер без ключа не прячется, а гаснет: увидев его бледным, человек
                // поймёт, что модели там есть, но платить за них пока нечем.
                if (item.Content is TextBlock label)
                {
                    label.Text = spec.Name;
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

    private RadioButton CreateProviderItem(LlmProvider provider)
    {
        var item = new RadioButton
        {
            Style = (Style)FindResource("ProviderItem"),
            GroupName = _providerGroup,
            Content = new TextBlock
            {
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };

        item.Checked += (_, _) =>
        {
            if (!_suppressProviderEvent)
            {
                ShowProvider(provider);
            }
        };
        item.Click += (_, _) =>
        {
            CloseSelectors();
            ProviderSelector.Focus();
        };

        return item;
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
            VariantRow.Visibility = Visibility.Collapsed;
            AutoItem.Visibility = Visibility.Collapsed;
            StatusPane.Visibility = Visibility.Collapsed;
            NoKeyText.Text = Loc.Format("S.Models.NoKeyHint", NameOf(_shown));
            _built = null;
            return;
        }

        NoKeyPane.Visibility = Visibility.Collapsed;
        ModelItems.Visibility = Visibility.Visible;
        SearchRow.Visibility = Visibility.Visible;
        AutoItem.Visibility = AllowAuto && FavoritesChip.IsChecked != true
            ? Visibility.Visible : Visibility.Collapsed;

        var state = State(_shown);
        RefreshFilters(state);
        var search = ModelSearchBox.Text.Trim();
        var vision = VisionChip.IsChecked == true;
        var code = CodeChip.IsChecked == true;
        var favorites = FavoritesChip.IsChecked == true;
        var openRouter = _shown == LlmProvider.OpenRouter;
        var variant = openRouter ? _variant : "";
        var route = openRouter && variant != ModelRef.BatchVariant ? _route : "";
        var policy = OrganizationPolicy.Current;
        var want = (_shown, search, vision, code, favorites, variant, route, state.Version, policy);

        // Ничего не изменилось — список уже такой, какой нужен. Это и есть вся разница между
        // «плашка открывается мгновенно» и «плашка думает полсекунды».
        if (_built == want)
        {
            RefreshMarks();
            return;
        }

        _built = want;

        if (!state.Ready)
        {
            ShowStatus(Loc.Get("S.Common.Loading"));
            return;
        }

        if (state.Error is { } error)
        {
            ShowStatus(error, retry: true);
            return;
        }

        var rows = new List<ModelPickerRow>();
        foreach (var model in VeniceModelCatalog.FilterAllTab(state.Models, "", vision, code))
        {
            var id = model.Id;
            if (openRouter)
            {
                var catalogVariant = ModelRef.OpenRouterCatalogVariant(id);
                if (variant.Length != 0 && variant != catalogVariant)
                {
                    continue;
                }

                if (route.Length != 0)
                {
                    if (!VeniceModelCatalog.CanUseOpenRouterRouting(model))
                    {
                        continue;
                    }

                    id = ModelRef.WithOpenRouterRouting(id, route);
                }
                else if (ModelRef.OpenRouterRouting(id).Length != 0)
                {
                    // Старые кэши могли содержать заранее сделанных близнецов Nitro.
                    continue;
                }
            }

            if (!policy.AllowsModel(id) || (search.Length != 0 &&
                !id.Contains(search, StringComparison.OrdinalIgnoreCase) && !VeniceModelCatalog.MatchesSearch(model, search)))
            {
                continue;
            }

            var isFavorite = _favorites.Contains(ModelRef.WithoutOpenRouterRouting(id));
            if (favorites && !isFavorite)
            {
                continue;
            }

            if (!state.Rows.TryGetValue(id, out var row))
            {
                row = new ModelPickerRow(this, model, id);
                state.Rows.Add(id, row);
            }

            row.IsFavorite = isFavorite;
            rows.Add(row);
        }

        if (rows.Count == 0)
        {
            ShowStatus(
                search.Length == 0 && !vision && !code && !favorites && variant.Length == 0 && route.Length == 0
                    ? Loc.Get("S.Models.Empty")
                    : Loc.Get("S.Common.NothingFound"));
            return;
        }

        StatusPane.Visibility = Visibility.Collapsed;
        _rows = rows;
        ModelItems.ItemsSource = _rows;
        RefreshMarks();
    }

    private void RefreshFilters(CatalogState state)
    {
        var isOpenRouter = _shown == LlmProvider.OpenRouter;
        VariantRow.Visibility = isOpenRouter ? Visibility.Visible : Visibility.Collapsed;
        if (!isOpenRouter)
        {
            return;
        }

        var variants = state.Variants ??= state.Models
            .Select(model => ModelRef.OpenRouterCatalogVariant(model.Id))
            .Where(variant => variant.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        _updatingFilters = true;
        try
        {
            if (!ReferenceEquals(_catalogVariants, variants))
            {
                _catalogVariants = variants;
                VariantFilter.Items.Clear();
                VariantFilter.Items.Add(FilterItem("", "S.Models.VariantAll"));
                foreach (var variant in variants)
                {
                    VariantFilter.Items.Add(FilterItem(variant));
                }
            }

            if (state.Ready && !_catalogVariants.Contains(_variant, StringComparer.Ordinal))
            {
                _variant = "";
            }

            SelectFilter(VariantFilter, _variant);
            SelectFilter(RouteFilter, _route);
            VariantFilter.IsEnabled = variants.Length != 0;
            RouteFilter.IsEnabled = _variant != ModelRef.BatchVariant;
        }
        finally
        {
            _updatingFilters = false;
        }
    }

    private static void SelectFilter(ComboBox combo, string value)
    {
        if (FilterValue(combo) != value || combo.SelectedIndex < 0)
        {
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value))
                ?? combo.Items[0];
        }
    }

    private void ShowStatus(string text, bool retry = false)
    {
        _rows = [];
        ModelItems.ItemsSource = null;
        RefreshMarks();
        ModelStatus.Text = text;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        StatusPane.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Повтор загрузки списка показанного провайдера.
    /// </summary>
    /// <remarks>
    /// Кнопкой, потому что повторное нажатие на уже выбранного провайдера ничего не делает
    /// (<see cref="ShowProvider"/>), а закрывать и открывать плашку ради повтора человек не
    /// догадается. Сама плашка в сеть не ходит — просит хозяина тем же событием, что и смена
    /// провайдера.
    /// </remarks>
    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLoading(_shown);
        ProviderShown?.Invoke(this, _shown);
    }

    private void RebuildKeys()
    {
        if (KeyItems is null)
        {
            return;
        }

        var chosen = _keyChoice.GetValueOrDefault(_shown);
        var want = (_shown, chosen, _keys);
        if (_builtKeys == want)
        {
            return;
        }

        _builtKeys = want;
        KeyItems.Children.Clear();
        RefreshKeyLabel(chosen);
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

    private void RefreshKeyLabel(string? keyId) => KeyLabel.Text = LabelOf(keyId) ?? Loc.Get("S.Models.KeyDefault");

    /// <summary>Обновляет галочки, не трогая сами списки.</summary>
    private void RefreshMarks()
    {
        if (AutoCheck is not null)
        {
            AutoCheck.Visibility = VeniceModelCatalog.IsAuto(_selected.ModelId)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var row = State(_selected.Provider).Rows.GetValueOrDefault(_selected.ModelId);
        if (!ReferenceEquals(_markedRow, row))
        {
            _markedRow?.SetSelection(false);
            _markedRow = row;
        }

        row?.SetSelection(true);
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
        item.Click += (_, _) =>
        {
            CloseSelectors();
            KeySelector.Focus();
        };

        return item;
    }

    /// <summary>Каталог одного провайдера: что загружено и что сказать, если не загрузилось.</summary>
    private sealed class CatalogState
    {
        public IReadOnlyList<VeniceModelInfo> Models { get; set; } = [];
        public Dictionary<string, ModelPickerRow> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string[]? Variants { get; set; }

        /// <summary>Загрузка закончилась — удачей или отказом. Иначе идёт.</summary>
        public bool Ready { get; set; }

        /// <summary>Почему не загрузилось. Есть только при пустом списке.</summary>
        public string? Error { get; set; }

        /// <summary>Растёт при каждой настоящей смене каталога — по нему видно, что пересобирать.</summary>
        public int Version { get; set; }
    }
}
