using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Amarin.Core;

namespace Amarin.UI;

public partial class ModelPickerPanel : UserControl
{
    public static readonly DependencyProperty AllowAutoProperty =
        DependencyProperty.Register(
            nameof(AllowAuto),
            typeof(bool),
            typeof(ModelPickerPanel),
            new PropertyMetadata(true, OnAllowAutoChanged));

    private IReadOnlyList<VeniceModelInfo> _catalog = [];
    private string _selectedId = "";
    private string? _allStatus;
    private bool _catalogReady;
    private bool _listsDirty = true;

    public ModelPickerPanel()
    {
        InitializeComponent();
        BindAutoLogo();
        var group = "ModelPickerTabs_" + Guid.NewGuid().ToString("N");
        TabRecommended.GroupName = group;
        TabAll.GroupName = group;
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => EnsureLists();
    }

    public bool AllowAuto
    {
        get => (bool)GetValue(AllowAutoProperty);
        set => SetValue(AllowAutoProperty, value);
    }

    public string SelectedModelId => _selectedId;

    public event EventHandler<string>? ModelPicked;

    public void SetSelected(string modelId)
    {
        _selectedId = modelId ?? "";
        RefreshSelection();
    }

    public void ShowLoading()
    {
        _catalogReady = false;
        _allStatus = Loc.Get("S.Common.Loading");
        InvalidateLists();
    }

    public void SetCatalog(IReadOnlyList<VeniceModelInfo> models, string? error = null)
    {
        _catalog = models;
        _catalogReady = true;
        _allStatus = error;
        InvalidateLists();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SmoothScroll.SetIsEnabled(RecommendedScroll, true);
        SmoothScroll.SetIsEnabled(AllScroll, true);
        ApplyAllowAuto();
        InvalidateLists();
    }

    private static void OnAllowAutoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModelPickerPanel panel)
        {
            panel.ApplyAllowAuto();
            panel.RefreshSelection();
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
    }

    private void BindAutoLogo()
    {
        if (AutoLogo is not null && TryFindResource("Auto") is ImageSource source)
        {
            ThemeImages.Assign(AutoLogo, "Auto", source);
            ModelBrand.ApplyLogoBox(AutoLogo, "Auto");
        }
    }

    private void AutoItem_Click(object sender, RoutedEventArgs e) => Pick(VeniceModelCatalog.AutoId);

    private void ModelSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildAll();

    private void FilterChip_Click(object sender, RoutedEventArgs e) => RebuildAll();

    private void Pick(string id)
    {
        _selectedId = id;
        RefreshSelection();
        ModelPicked?.Invoke(this, id);
    }

    private void RefreshSelection()
    {
        if (AutoCheck is not null)
        {
            AutoCheck.Visibility = VeniceModelCatalog.IsAuto(_selectedId)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        InvalidateLists();
    }

    /// <summary>
    /// Помечает списки к пересборке и собирает их, только если панель на экране.
    /// </summary>
    /// <remarks>
    /// Панель живёт внутри <see cref="System.Windows.Controls.Primitives.Popup"/>, и в настройках
    /// таких полей семь. Раньше каждое открытие настроек перестраивало у всех семи оба списка
    /// целиком — сотни кнопок с подсказками создавались в закрытых попапах и тут же уходили в
    /// мусор. Видимой панель становится ровно тогда, когда попап открыли, поэтому хозяевам
    /// панели ничего знать об этом не нужно.
    /// </remarks>
    private void InvalidateLists()
    {
        _listsDirty = true;
        EnsureLists();
    }

    private void EnsureLists()
    {
        if (!_listsDirty || !IsVisible)
        {
            return;
        }

        _listsDirty = false;
        RebuildRecommended();
        RebuildAll();
    }

    private void RebuildRecommended()
    {
        if (RecommendedGroups is null)
        {
            return;
        }

        RecommendedGroups.Children.Clear();
        foreach (var tier in VeniceModelCatalog.Tiers)
        {
            RecommendedGroups.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("PickerGroupHeader"),
                Text = tier.Title
            });

            foreach (var id in tier.Models)
            {
                if (VeniceModelCatalog.IsAuto(id))
                {
                    continue;
                }

                RecommendedGroups.Children.Add(CreateModelButton(
                    id,
                    VeniceModelCatalog.GetDisplayName(id),
                    TooltipFor(id),
                    IsSelected(id)));
            }
        }
    }

    private void RebuildAll()
    {
        if (AllItems is null)
        {
            return;
        }

        AllItems.Children.Clear();
        if (!_catalogReady)
        {
            AllItems.Children.Add(StatusText(_allStatus ?? Loc.Get("S.Common.Loading")));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_allStatus) && _catalog.Count == 0)
        {
            AllItems.Children.Add(StatusText(_allStatus));
            return;
        }

        var filtered = VeniceModelCatalog.FilterAllTab(
                _catalog,
                ModelSearchBox?.Text,
                VisionChip?.IsChecked == true,
                CodeChip?.IsChecked == true)
            .ToList();

        if (filtered.Count == 0)
        {
            AllItems.Children.Add(StatusText(
                string.IsNullOrWhiteSpace(ModelSearchBox?.Text) &&
                VisionChip?.IsChecked != true &&
                CodeChip?.IsChecked != true
                    ? Loc.Get("S.Models.Empty")
                    : Loc.Get("S.Common.NothingFound")));
            return;
        }

        foreach (var model in filtered)
        {
            AllItems.Children.Add(CreateModelButton(
                model.Id,
                VeniceModelCatalog.GetListDisplayName(model),
                VeniceModelCatalog.BuildTooltip(model),
                IsSelected(model.Id)));
        }
    }

    private bool IsSelected(string id) =>
        !string.IsNullOrWhiteSpace(_selectedId) &&
        _selectedId.Equals(id, StringComparison.OrdinalIgnoreCase);

    private string TooltipFor(string id)
    {
        foreach (var model in _catalog)
        {
            if (model.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return VeniceModelCatalog.BuildTooltip(model);
            }
        }

        return id;
    }

    private Button CreateModelButton(string id, string display, string tooltip, bool selected)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ModelItem"),
            ToolTip = tooltip,
            Tag = id
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = CreateGlyph(id);
        var name = new TextBlock
        {
            Style = (Style)FindResource("ModelName"),
            Text = display
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(glyph);
        grid.Children.Add(name);

        if (selected)
        {
            var check = new System.Windows.Shapes.Path { Style = (Style)FindResource("SelectedCheck") };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        button.Content = grid;
        button.Click += (_, _) => Pick(id);
        return button;
    }

    private UIElement CreateGlyph(string id)
    {
        var key = VeniceModelCatalog.GetLogoResourceKey(id);
        if (key is not null && TryFindResource(key) is ImageSource source)
        {
            var size = key.Equals("Grok", StringComparison.Ordinal) ? 13 : 15;
            var glyph = new Image
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeImages.Assign(glyph, key, source);
            ModelBrand.ApplyLogoBox(glyph, key);
            return glyph;
        }

        return new TextBlock
        {
            Text = VeniceModelCatalog.GetLogoLetter(id),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
    }

    private static TextBlock StatusText(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E)),
            Margin = new Thickness(8, 10, 8, 8),
            TextWrapping = TextWrapping.Wrap
        };
}
