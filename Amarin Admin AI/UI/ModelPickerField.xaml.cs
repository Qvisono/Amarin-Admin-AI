using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

public partial class ModelPickerField : UserControl
{
    public ModelPickerField()
    {
        InitializeComponent();
        PopupManager.Register(PickerPopup, OpenButton);
    }

    public string SelectedModelId { get; private set; } = "";

    public event EventHandler<string>? ModelPicked;

    public void SetSelected(string modelId)
    {
        SelectedModelId = modelId ?? "";
        SelectedLabel.Text = VeniceModelCatalog.GetDisplayName(SelectedModelId);

        // Короткое имя скрывает ID, а в настройках рядом стоят модели, чьи имена различаются
        // одним словом. Точный ID под курсором — единственное место, где их видно наверняка.
        OpenButton.ToolTip = string.IsNullOrWhiteSpace(SelectedModelId) ? null : SelectedModelId;
        Panel.SetSelected(SelectedModelId);
    }

    public void ShowLoading() => Panel.ShowLoading();

    public void SetCatalog(IReadOnlyList<VeniceModelInfo> models, string? error = null) =>
        Panel.SetCatalog(models, error);

    private void Panel_ModelPicked(object sender, string id)
    {
        OpenButton.IsChecked = false;
        SetSelected(id);
        ModelPicked?.Invoke(this, id);
    }
}
