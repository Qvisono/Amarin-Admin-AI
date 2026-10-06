using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Amarin.Core;

namespace Amarin.UI;

public partial class ModelPickerField : UserControl
{
    /// <summary>
    /// Показывать ли строку «Авто». У служебных слотов её нет: у каждого своя конкретная
    /// модель, а «Авто» — это просьба выбрать её, а не выбор.
    /// </summary>
    public static readonly DependencyProperty AllowAutoProperty =
        DependencyProperty.Register(
            nameof(AllowAuto),
            typeof(bool),
            typeof(ModelPickerField),
            new PropertyMetadata(false));

    /// <summary>Ключ подсказки у «Авто»: у разных слотов она значит разное.</summary>
    public static readonly DependencyProperty AutoTipKeyProperty =
        DependencyProperty.Register(
            nameof(AutoTipKey),
            typeof(string),
            typeof(ModelPickerField),
            new PropertyMetadata("S.Models.AutoTip"));

    private ModelPickerPanel? _panel;
    private IReadOnlyList<ApiKeyEntry> _keys = [];

    /// <summary>Каталог провайдера, сказанный полю; <c>Models = null</c> — идёт загрузка.</summary>
    private readonly Dictionary<LlmProvider, (IReadOnlyList<VeniceModelInfo>? Models, string? Error)> _catalogs = [];

    public ModelPickerField()
    {
        InitializeComponent();
        PopupManager.Register(PickerPopup, OpenButton);

        // Раньше, чем попап откроется: Checked приходит до того, как привязка донесёт IsOpen.
        OpenButton.Checked += (_, _) => _ = Panel;
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

    public string SelectedModelId { get; private set; } = "";

    public string? SelectedKeyId { get; private set; }

    public event EventHandler<ModelBinding>? ModelPicked;

    /// <summary>У показанного провайдера нет ключа, и человек просит его завести.</summary>
    public event EventHandler? AddKeyRequested;

    /// <summary>Человек открыл столбец этого провайдера — хозяину пора подвезти каталог.</summary>
    public event EventHandler<LlmProvider>? ProviderShown;

    /// <summary>Плашка выбора модели — создаётся при первом обращении, обычно при открытии.</summary>
    /// <remarks>
    /// Плашка втрое крупнее самого поля, а полей на странице «Модели» девять: собранные заранее,
    /// они были половиной её постройки, хотя открывают их по одной и не каждый раз. До создания
    /// поле помнит всё, что ему сказали (ключи, каталоги, выбор), и отдаёт плашке разом.
    /// </remarks>
    internal ModelPickerPanel Panel => _panel ?? CreatePanel();

    private ModelPickerPanel CreatePanel()
    {
        var panel = new ModelPickerPanel();
        panel.SetBinding(ModelPickerPanel.AllowAutoProperty, new Binding(nameof(AllowAuto)) { Source = this });
        panel.SetBinding(ModelPickerPanel.AutoTipKeyProperty, new Binding(nameof(AutoTipKey)) { Source = this });
        panel.ModelPicked += Panel_ModelPicked;
        panel.KeyPicked += Panel_KeyPicked;
        panel.AddKeyRequested += Panel_AddKeyRequested;
        panel.ProviderShown += Panel_ProviderShown;
        _panel = panel;

        panel.SetKeys(_keys);
        foreach (var (provider, catalog) in _catalogs)
        {
            if (catalog.Models is null)
            {
                panel.ShowLoading(provider);
            }
            else
            {
                panel.SetCatalog(provider, catalog.Models, catalog.Error);
            }
        }

        panel.SetSelected(SelectedModelId, SelectedKeyId);
        PickerPopup.Child = panel;
        return panel;
    }

    public void SetSelected(string modelId, string? keyId)
    {
        SelectedModelId = modelId ?? "";
        SelectedKeyId = keyId;
        ShowLabel();
        _panel?.SetSelected(SelectedModelId, SelectedKeyId);
    }

    public void SetKeys(IReadOnlyList<ApiKeyEntry> keys)
    {
        _keys = keys ?? [];
        _panel?.SetKeys(_keys);

        // Имя ключа в подписи берётся отсюда же: список мог приехать после выбора модели.
        ShowLabel();
    }

    public void ShowLoading(LlmProvider provider)
    {
        _catalogs[provider] = (null, null);
        _panel?.ShowLoading(provider);
    }

    public void SetCatalog(
        LlmProvider provider,
        IReadOnlyList<VeniceModelInfo> models,
        string? error = null)
    {
        _catalogs[provider] = (models ?? [], error);
        _panel?.SetCatalog(provider, models ?? [], error);
    }

    /// <summary>
    /// Подпись поля: имя модели, а за ним бледно — имя ключа, если выбран не тот, что по
    /// умолчанию.
    /// </summary>
    /// <remarks>
    /// Имя ключа показывается только когда он выбран явно: строка «GPT · по умолчанию» у всех
    /// восьми слотов не сообщала бы ничего, зато съедала бы место у имени модели. Полный
    /// идентификатор уходит в подсказку — короткое имя его прячет, а соседние строки настроек
    /// различаются одним словом.
    /// </remarks>
    private void ShowLabel()
    {
        SelectedLabel.Text = VeniceModelCatalog.GetDisplayName(SelectedModelId);

        var keyLabel = KeyLabel();
        SelectedKeyLabel.Text = keyLabel ?? "";
        SelectedKeyLabel.Visibility = keyLabel is null ? Visibility.Collapsed : Visibility.Visible;

        OpenButton.ToolTip = keyLabel is null
            ? SelectedModelId
            : SelectedModelId + "  ·  " + keyLabel;
    }

    private string? KeyLabel()
    {
        if (string.IsNullOrWhiteSpace(SelectedKeyId))
        {
            return null;
        }

        // Тем же правилом, что ModelPickerPanel.LabelOf, но без плашки: её может ещё не быть.
        foreach (var key in _keys)
        {
            if (key.Id.Equals(SelectedKeyId, StringComparison.Ordinal))
            {
                return key.Label;
            }
        }

        return null;
    }

    /// <summary>
    /// Прижимает попап правым краем к полю.
    /// </summary>
    /// <remarks>
    /// Плашка стала шире поля втрое, и при выравнивании по левому краю она уезжала за правую
    /// границу окна настроек. Смещение считается по живой ширине, а не записано числом
    /// в разметке: у полей она разная, а у самой плашки зависит от масштаба интерфейса.
    /// </remarks>
    private void PickerPopup_Opened(object sender, EventArgs e)
    {
        // Открываемся на провайдере выбранной модели. Только здесь: SetSelected зовут и при
        // открытой плашке, и оттуда это возвращало бы столбец под рукой человека.
        Panel.ShowSelectedProvider();

        if (PickerPopup.Child is FrameworkElement child)
        {
            // Правый край карточки — по правому краю кнопки. Ширина попапа включает поля под
            // тень, а правое из них лежит за карточкой, поэтому оно и возвращается.
            child.UpdateLayout();
            var width = child.ActualWidth > 0 ? child.ActualWidth : child.Width;
            PickerPopup.HorizontalOffset = OpenButton.ActualWidth - width + PopupShadow.Margin.Right;
        }
    }

    private void Panel_ModelPicked(object? sender, ModelBinding binding)
    {
        OpenButton.IsChecked = false;
        SelectedModelId = binding.ModelId;
        SelectedKeyId = binding.KeyId;
        ShowLabel();
        ModelPicked?.Invoke(this, binding);
    }

    /// <summary>
    /// Ключ сменили — плашку не закрываем: человек обычно тут же выбирает под него модель.
    /// </summary>
    private void Panel_KeyPicked(object? sender, ModelBinding binding)
    {
        SelectedModelId = binding.ModelId;
        SelectedKeyId = binding.KeyId;
        ShowLabel();
        ModelPicked?.Invoke(this, binding);
    }

    private void Panel_AddKeyRequested(object? sender, EventArgs e)
    {
        OpenButton.IsChecked = false;
        AddKeyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Panel_ProviderShown(object? sender, LlmProvider provider) =>
        ProviderShown?.Invoke(this, provider);
}
