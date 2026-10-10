using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Окно нового ключа: провайдер, адрес, сам ключ и название.
/// </summary>
/// <remarks>
/// Окно только собирает ввод и показывает, что с ним стало. Проверка ключа у провайдера и
/// сохранение — дело главного окна (<c>MainWindow.Keys.cs</c>): там службы, ход и всё, что
/// после нового ключа надо перечитать.
/// <para>
/// Провайдер угадывается по началу ключа, пока человек не выбрал его сам: ключи Anthropic,
/// OpenRouter, xAI, Groq и Gemini узнаются с первых знаков, и переспрашивать о том, что видно
/// из вставленного, незачем. Выбранное вручную догадка не перебивает.
/// </para>
/// </remarks>
public partial class KeyAddDialog : UserControl
{
    private readonly ComboBoxItem _customItem;
    private string _typedAddress = "";
    private bool _chosenByHand;
    private bool _selecting;

    public KeyAddDialog()
    {
        InitializeComponent();

        foreach (var spec in ProviderSpec.All)
        {
            var item = new ComboBoxItem { Content = spec.Name, Tag = spec };
            if (DescriptionKey(spec.Provider) is { } key)
            {
                item.SetResourceReference(ToolTipProperty, key);
            }

            ProviderBox.Items.Add(item);
        }

        _customItem = new ComboBoxItem { Tag = null };
        _customItem.SetResourceReference(ContentControl.ContentProperty, "S.Key.CustomServer");
        _customItem.SetResourceReference(ToolTipProperty, "S.Key.Provider.CustomDesc");
        ProviderBox.Items.Add(_customItem);

        KeyDown += Root_KeyDown;
    }

    /// <summary>Человек нажал «Добавить» или Enter в поле.</summary>
    public event EventHandler? Submitted;

    /// <summary>Окно закрыли, ничего не добавив.</summary>
    public event EventHandler? Cancelled;

    /// <summary>Просьба открыть страницу провайдера, где выдают ключи.</summary>
    public event EventHandler<string>? LinkRequested;

    /// <summary>Выбранный встроенный провайдер. <c>null</c> — свой сервер по адресу.</summary>
    public LlmProvider? Provider => (ProviderBox.SelectedItem as ComboBoxItem)?.Tag is ProviderSpec spec
        ? spec.Provider
        : null;

    public bool IsCustomServer => ReferenceEquals(ProviderBox.SelectedItem, _customItem);

    public string Secret => (KeyValue.Text ?? "").Trim();

    public string KeyLabel => LabelValue.Text ?? "";

    /// <summary>Адрес своего сервера, как его ввели.</summary>
    public string Address => _typedAddress.Trim();

    /// <summary>
    /// Чистит поля и ставит провайдера.
    /// </summary>
    /// <param name="preset">
    /// Провайдер, у которого человек нажал «Добавить ключ». <c>null</c> — Venice, как было до
    /// появления остальных, и догадка по ключу свободна.
    /// </param>
    public void Reset(LlmProvider? preset)
    {
        KeyValue.Text = "";
        LabelValue.Text = "";
        _typedAddress = "";
        _chosenByHand = preset is not null;
        Select(preset ?? LlmProvider.Venice);
        Status.Text = "";
        SetBusy(false, null);
    }

    public void FocusKey() => KeyValue.Focus();

    public void ShowError(string text)
    {
        Status.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
        Status.Text = text;
    }

    /// <summary>«Проверяю…» — в подвале, на месте будущей ошибки: окно не меняет высоту.</summary>
    public void SetBusy(bool busy, string? text)
    {
        SaveButton.IsEnabled = !busy;
        ProviderBox.IsEnabled = !busy;
        if (busy)
        {
            Status.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");
            Status.Text = text ?? "";
        }
        else if (Status.Text == Loc.Get("S.Key.Checking") || Status.Text == Loc.Get("S.Key.CheckingServer"))
        {
            Status.Text = "";
        }
    }

    /// <summary>
    /// Пояснение есть у тех, чем провайдер отличается от остальных: картинки и чтение страниц
    /// живут только у Venice, а OpenRouter — счёт на модели многих производителей.
    /// </summary>
    private static string? DescriptionKey(LlmProvider provider) =>
        provider == LlmProvider.Venice ? "S.Key.Provider.VeniceDesc"
        : provider == LlmProvider.OpenRouter ? "S.Key.Provider.OpenRouterDesc"
        : null;

    private void Select(LlmProvider provider)
    {
        var item = ProviderBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => candidate.Tag is ProviderSpec spec && spec.Provider == provider);

        _selecting = true;
        try
        {
            ProviderBox.SelectedItem = item ?? ProviderBox.Items[0];
        }
        finally
        {
            _selecting = false;
        }

        ShowProvider();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_selecting)
        {
            _chosenByHand = true;
            Status.Text = "";
            ShowProvider();
        }
    }

    /// <summary>
    /// Строка адреса и ссылка «Где взять» — по выбранному провайдеру.
    /// </summary>
    /// <remarks>
    /// У встроенного адрес только для чтения: видно, куда уйдёт ключ, а поменять его значило бы
    /// отправить ключ провайдера чужому серверу. Набранный адрес своего сервера помнится, пока окно
    /// открыто: человек мог заглянуть в соседний пункт списка и вернуться.
    /// </remarks>
    private void ShowProvider()
    {
        if (ProviderBox.SelectedItem is ComboBoxItem { Tag: ProviderSpec spec })
        {
            AddressValue.IsReadOnly = true;
            AddressValue.IsTabStop = false;
            AddressValue.Text = spec.BaseUrl;
            AddressValue.SetResourceReference(ForegroundProperty, "Text.Dim");
            KeysLink.Visibility = Visibility.Visible;
            KeysLink.ToolTip = spec.KeysUrl;
            return;
        }

        AddressValue.IsReadOnly = false;
        AddressValue.IsTabStop = true;
        AddressValue.Text = _typedAddress;
        AddressValue.SetResourceReference(ForegroundProperty, "Text.Bright");
        KeysLink.Visibility = Visibility.Hidden;
    }

    private void KeyValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SaveButton.IsEnabled)
        {
            Status.Text = "";
        }

        if (_chosenByHand || ProviderSpec.ByKeyPrefix(KeyValue.Text) is not { } guess)
        {
            return;
        }

        if (Provider != guess.Provider)
        {
            Select(guess.Provider);
        }
    }

    private void KeysLink_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderBox.SelectedItem is ComboBoxItem { Tag: ProviderSpec spec })
        {
            LinkRequested?.Invoke(this, spec.KeysUrl);
        }
    }

    private void AddressValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!AddressValue.IsReadOnly)
        {
            _typedAddress = AddressValue.Text ?? "";
            if (SaveButton.IsEnabled)
            {
                Status.Text = "";
            }
        }
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Submit();
        }
    }

    /// <summary>
    /// Esc закрывает окно — кроме случая, когда открыт список провайдеров: его Esc закрывает сам
    /// и до окна не пускает.
    /// </summary>
    private void Root_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !ProviderBox.IsDropDownOpen)
        {
            e.Handled = true;
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Submit();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    private void Submit()
    {
        if (SaveButton.IsEnabled)
        {
            Submitted?.Invoke(this, EventArgs.Empty);
        }
    }
}
