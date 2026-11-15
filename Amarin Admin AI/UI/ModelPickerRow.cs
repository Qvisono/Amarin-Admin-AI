using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Данные строки. Логотип запрашивается только при создании видимого контейнера.</summary>
internal sealed class ModelPickerRow(ModelPickerPanel host, VeniceModelInfo model, string id) : INotifyPropertyChanged
{
    private string? _logoKey;
    private string? _display;
    private bool _logoResolved;
    private bool _selected;
    private bool _favorite;

    public VeniceModelInfo Model => model;
    public string Id => id;
    public string FavoriteId { get; } = ModelRef.WithoutOpenRouterRouting(id);
    public string Display => _display ??= ModelPickerField.ModelName(Id, Model);
    public string Tooltip => VeniceModelCatalog.BuildTooltip(Model, Id);
    public string Letter => VeniceModelCatalog.GetLogoLetter(Id);

    private string? LogoKey
    {
        get
        {
            if (!_logoResolved)
            {
                _logoKey = VeniceModelCatalog.GetLogoResourceKey(Id);
                _logoResolved = true;
            }

            return _logoKey;
        }
    }

    public ImageSource? Logo => LogoKey is { } key ? host.RowLogo(key) : null;
    public Thickness LogoMargin => LogoKey is { } key ? ModelBrand.LogoMargin(key) : default;
    public double LogoSize => Math.Max(1, (LogoKey == "Grok" ? 13 : 15) - LogoMargin.Left - LogoMargin.Right);
    public Visibility LogoVisibility => Logo is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LetterVisibility => Logo is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CheckVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;
    public bool IsSelected => _selected;

    public bool IsFavorite
    {
        get => _favorite;
        set
        {
            if (_favorite == value)
            {
                return;
            }

            _favorite = value;
            Notify(nameof(IsFavorite));
            Notify(nameof(FavoriteAction));
        }
    }

    public string FavoriteAction => Loc.Get(_favorite ? "S.Models.FavoriteRemove" : "S.Models.FavoriteAdd");

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetSelection(bool selected)
    {
        if (_selected == selected)
        {
            return;
        }

        _selected = selected;
        Notify(nameof(IsSelected));
        Notify(nameof(CheckVisibility));
    }

    public void RefreshTheme()
    {
        Notify(nameof(Logo));
        Notify(nameof(LogoVisibility));
        Notify(nameof(LetterVisibility));
    }

    public void RefreshLanguage()
    {
        _display = null;
        Notify(nameof(Display));
        Notify(nameof(FavoriteAction));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
