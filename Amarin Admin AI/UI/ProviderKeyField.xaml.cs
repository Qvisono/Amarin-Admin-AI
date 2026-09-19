using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Выбор провайдера и ключа — без выбора модели.
/// </summary>
/// <remarks>
/// Настройка инструмента, а не модели: у поиска в интернете человек решает, через кого искать
/// и с какого счёта платить, а какая модель везёт запрос — подробность устройства. Отдельной
/// «ручки поиска» ни у Venice, ни у OpenRouter нет, поиск едет надстройкой на обычном запросе,
/// и показывать этот выбор в настройках значило бы спрашивать человека о том, что программа
/// решает сама.
/// <para>
/// Форма взята у <see cref="ModelPickerField"/>: это соседние строки одной страницы. Разница
/// в содержимом — провайдер, ключ и движок поиска вместо модели, и строка «Авто» в левом
/// столбце.
/// </para>
/// </remarks>
public partial class ProviderKeyField : UserControl
{
    private readonly string _providerGroup = "ProviderKeyProviders_" + Guid.NewGuid().ToString("N");
    private readonly string _keyGroup = "ProviderKeyKeys_" + Guid.NewGuid().ToString("N");
    private readonly string _engineGroup = "ProviderKeyEngines_" + Guid.NewGuid().ToString("N");

    private readonly Dictionary<LlmProvider, string?> _keyChoice = [];

    private IReadOnlyList<ApiKeyEntry> _keys = [];
    private LlmProvider? _provider;
    private string? _keyId;
    private string? _engine;
    private string? _mode;
    private bool _suppress;

    public ProviderKeyField()
    {
        InitializeComponent();
        PopupManager.Register(ChoicePopup, OpenButton);
        Loaded += (_, _) =>
        {
            SmoothScroll.SetIsEnabled(ProviderScroll, true);
            SmoothScroll.SetIsEnabled(KeyScroll, true);
            SmoothScroll.SetIsEnabled(EngineScroll, true);
        };
    }

    /// <summary><c>null</c> — «как у модели хода»: так поиск работал до появления настройки.</summary>
    public LlmProvider? SelectedProvider => _provider;

    public string? SelectedKeyId => _keyId;

    public event EventHandler<WebSearchTarget>? Changed;

    /// <summary>У выбранного провайдера нет ключа, и человек просит его завести.</summary>
    public event EventHandler? AddKeyRequested;

    public void SetSelected(LlmProvider? provider, string? keyId, string? engine, string? mode)
    {
        _provider = provider;
        _keyId = keyId;
        _engine = engine;
        _mode = mode;
        if (provider is { } known)
        {
            _keyChoice[known] = keyId;
        }

        ShowLabel();
        Rebuild();
    }

    public void SetKeys(IReadOnlyList<ApiKeyEntry> keys)
    {
        _keys = keys ?? [];
        ShowLabel();
        Rebuild();
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

    private string? LabelOf(string? keyId)
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

    /// <summary>
    /// Подпись: имя провайдера, а за ним бледно — имя ключа, если выбран не тот, что
    /// по умолчанию.
    /// </summary>
    private void ShowLabel()
    {
        SelectedLabel.Text = _provider is { } provider
            ? ProviderSpec.For(provider).Name
            : Loc.Get("S.Models.Auto");

        var keyLabel = LabelOf(_keyId);
        SelectedKeyLabel.Text = keyLabel ?? "";
        SelectedKeyLabel.Visibility = keyLabel is null ? Visibility.Collapsed : Visibility.Visible;

        OpenButton.ToolTip = _provider is null
            ? Loc.Get("S.Customize.WebSearch.AutoTip")
            : keyLabel is null
                ? ProviderSpec.For(_provider.Value).Name
                : ProviderSpec.For(_provider.Value).Name + "  ·  " + keyLabel;
    }

    private void ChoicePopup_Opened(object sender, EventArgs e)
    {
        Rebuild();

        if (ChoicePopup.Child is FrameworkElement child)
        {
            child.UpdateLayout();
            var width = child.ActualWidth > 0 ? child.ActualWidth : child.Width;
            ChoicePopup.HorizontalOffset = OpenButton.ActualWidth - width;
        }
    }

    private void AddKeyButton_Click(object sender, RoutedEventArgs e)
    {
        OpenButton.IsChecked = false;
        AddKeyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        if (ProviderItems is null || KeyItems is null)
        {
            return;
        }

        _suppress = true;
        try
        {
            RebuildProviders();
            RebuildKeys();
            RebuildEngines();
        }
        finally
        {
            _suppress = false;
        }
    }

    private void RebuildProviders()
    {
        ProviderItems.Children.Clear();

        // «Авто» первой строкой: это заводское состояние, и к нему возвращаются чаще, чем
        // выбирают конкретного провайдера во второй раз.
        ProviderItems.Children.Add(Item(
            Loc.Get("S.Models.Auto"),
            Loc.Get("S.Customize.WebSearch.AutoTip"),
            _providerGroup,
            _provider is null,
            dim: false,
            () => Pick(null)));

        foreach (var spec in ProviderSpec.All)
        {
            var provider = spec.Provider;
            var hasKey = HasKey(provider);
            ProviderItems.Children.Add(Item(
                spec.Name,
                hasKey ? null : Loc.Format("S.Models.NoKeyHint", spec.Name),
                _providerGroup,
                _provider == provider,
                dim: !hasKey,
                () => Pick(provider)));
        }
    }

    private void RebuildKeys()
    {
        KeyItems.Children.Clear();

        if (_provider is not { } provider)
        {
            // Провайдера не выбрали — выбирать ключ не из чего и незачем: поиск идёт ключом
            // того хода, в котором его позвали.
            AddKeyButton.Visibility = Visibility.Collapsed;
            KeyItems.Children.Add(Hint(Loc.Get("S.Customize.WebSearch.AutoTip")));
            return;
        }

        if (!HasKey(provider))
        {
            AddKeyButton.Visibility = Visibility.Visible;
            KeyItems.Children.Add(Hint(Loc.Format("S.Models.NoKeyHint", ProviderSpec.For(provider).Name)));
            return;
        }

        AddKeyButton.Visibility = Visibility.Collapsed;
        var chosen = _keyChoice.GetValueOrDefault(provider);

        KeyItems.Children.Add(Item(
            Loc.Get("S.Models.KeyDefault"),
            Loc.Get("S.Models.KeyDefaultTip"),
            _keyGroup,
            string.IsNullOrWhiteSpace(chosen),
            dim: false,
            () => PickKey(null)));

        foreach (var key in _keys)
        {
            if (key.Provider != provider || key.IsBroken)
            {
                continue;
            }

            var id = key.Id;
            KeyItems.Children.Add(Item(
                key.Label,
                key.Masked,
                _keyGroup,
                string.Equals(id, chosen, StringComparison.Ordinal),
                dim: false,
                () => PickKey(id)));
        }
    }

    /// <summary>
    /// Столбец движков. Показывается всегда: движок действует и когда провайдер не закреплён,
    /// если ход всё равно окажется у OpenRouter. У закреплённого Venice выбирать нечего —
    /// столбец гаснет и говорит почему.
    /// </summary>
    private void RebuildEngines()
    {
        EngineItems.Children.Clear();

        var venice = _provider == LlmProvider.Venice;
        if (venice)
        {
            EngineItems.Children.Add(Hint(Loc.Get("S.Search.Engine.VeniceOnly")));
            return;
        }

        var chosen = WebSearchEngines.Match(_engine, _mode);
        foreach (var option in WebSearchEngines.All)
        {
            var row = option;
            EngineItems.Children.Add(Item(
                row.Label,
                row.Price.Length == 0
                    ? Loc.Get("S.Search.Engine.AutoTip")
                    : Loc.Format("S.Search.Engine.PriceTip", row.Price),
                _engineGroup,
                ReferenceEquals(row, chosen),
                dim: false,
                () => PickEngine(row),
                row.Price));
        }
    }

    private RadioButton Item(
        string label,
        string? tooltip,
        string group,
        bool selected,
        bool dim,
        Action picked,
        string? trailing = null)
    {
        var name = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        // Провайдер без ключа не прячется, а гаснет: увидев его бледным, человек поймёт, что
        // искать через него можно, но платить пока нечем.
        name.SetResourceReference(
            TextBlock.ForegroundProperty,
            dim ? "Text.Faint" : selected ? "Text.Bright" : "Text.Tertiary");

        // Цена рядом с именем движка: разница между ними десятикратная, и ради неё человек
        // сюда и пришёл. Ставить её в подсказку значило бы прятать главное.
        object content = name;
        if (!string.IsNullOrWhiteSpace(trailing))
        {
            var price = new TextBlock
            {
                Text = trailing,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            price.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(price, 1);
            row.Children.Add(name);
            row.Children.Add(price);
            content = row;
        }

        var item = new RadioButton
        {
            Style = (Style)FindResource("ChoiceItem"),
            GroupName = group,
            Content = content,
            IsChecked = selected,
            ToolTip = tooltip
        };

        item.Checked += (_, _) =>
        {
            if (!_suppress)
            {
                picked();
            }
        };

        return item;
    }

    private static TextBlock Hint(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Margin = new Thickness(8, 6, 6, 6),
            TextWrapping = TextWrapping.Wrap
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
        return block;
    }

    /// <summary>
    /// Смена провайдера не закрывает плашку: следом человек обычно выбирает ключ.
    /// </summary>
    private void Pick(LlmProvider? provider)
    {
        _provider = provider;
        _keyId = provider is { } known ? _keyChoice.GetValueOrDefault(known) : null;
        ShowLabel();
        Rebuild();
        Report();
    }

    private void PickKey(string? keyId)
    {
        if (_provider is not { } provider)
        {
            return;
        }

        _keyChoice[provider] = keyId;
        _keyId = keyId;
        ShowLabel();
        Report();
    }

    private void PickEngine(WebSearchEngineOption option)
    {
        _engine = option.Engine;
        _mode = option.Mode;
        ShowLabel();
        Report();
    }

    private void Report() =>
        Changed?.Invoke(this, new WebSearchTarget(_provider, _keyId, _engine, _mode));
}
