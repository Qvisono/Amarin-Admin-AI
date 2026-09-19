using System.Windows;
using System.Windows.Controls;
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

    public ModelPickerField()
    {
        InitializeComponent();
        PopupManager.Register(PickerPopup, OpenButton);
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

    public void SetSelected(string modelId, string? keyId)
    {
        SelectedModelId = modelId ?? "";
        SelectedKeyId = keyId;
        ShowLabel();
        Panel.SetSelected(SelectedModelId, SelectedKeyId);
    }

    public void SetKeys(IReadOnlyList<ApiKeyEntry> keys)
    {
        Panel.SetKeys(keys);

        // Имя ключа в подписи берётся отсюда же: список мог приехать после выбора модели.
        ShowLabel();
    }

    public void ShowLoading(LlmProvider provider) => Panel.ShowLoading(provider);

    public void SetCatalog(
        LlmProvider provider,
        IReadOnlyList<VeniceModelInfo> models,
        string? error = null) =>
        Panel.SetCatalog(provider, models, error);

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

        return Panel.LabelOf(SelectedKeyId);
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
            child.UpdateLayout();
            var width = child.ActualWidth > 0 ? child.ActualWidth : child.Width;
            PickerPopup.HorizontalOffset = OpenButton.ActualWidth - width;
        }
    }

    private void Panel_ModelPicked(object sender, ModelBinding binding)
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
    private void Panel_KeyPicked(object sender, ModelBinding binding)
    {
        SelectedModelId = binding.ModelId;
        SelectedKeyId = binding.KeyId;
        ShowLabel();
        ModelPicked?.Invoke(this, binding);
    }

    private void Panel_AddKeyRequested(object sender, EventArgs e)
    {
        OpenButton.IsChecked = false;
        AddKeyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Panel_ProviderShown(object sender, LlmProvider provider) =>
        ProviderShown?.Invoke(this, provider);
}
