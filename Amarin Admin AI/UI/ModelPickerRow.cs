using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Одна строка списка моделей в плашке выбора.
/// </summary>
/// <remarks>
/// Отдельный объект, а не собранная в коде кнопка: контейнеры строк переиспользует
/// <c>VirtualizingStackPanel</c>, и переиспользовать он умеет только то, что нарисовано
/// шаблоном по данным. Прежде список строился кнопками целиком — у OpenRouter это триста
/// кнопок с подсказками на каждый символ, набранный в поиске, и окно вставало.
/// <para>
/// Логотип разрешается один раз при сборке строки: <c>TryFindResource</c> на каждую
/// перерисовку виртуализованного контейнера обходился бы словарями тем по десять раз в кадр.
/// Смену темы плашка переживает пересборкой — она подписана на
/// <c>ThemeManager.EffectiveThemeChanged</c>.
/// </para>
/// </remarks>
internal sealed class ModelPickerRow : INotifyPropertyChanged
{
    private bool _selected;

    public required string Id { get; init; }

    public required string Display { get; init; }

    public required string Tooltip { get; init; }

    public ImageSource? Logo { get; init; }

    public double LogoSize { get; init; } = 15;

    public Thickness LogoMargin { get; init; }

    public string Letter { get; init; } = "";

    public Visibility LogoVisibility => Logo is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterVisibility => Logo is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Галочка у выбранной модели. Меняется без пересборки списка.</summary>
    public Visibility CheckVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckVisibility)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Собирает строку по модели каталога, разрешив логотип в живых ресурсах.</summary>
    public static ModelPickerRow Build(FrameworkElement host, VeniceModelInfo model)
    {
        var key = VeniceModelCatalog.GetLogoResourceKey(model.Id);
        var logo = key is null ? null : host.TryFindResource(key) as ImageSource;
        var margin = key is null ? default : ModelBrand.LogoMargin(key);

        // Grok рисуется плотнее прочих: его знак занимает весь квадрат без полей.
        var size = key is not null && key.Equals("Grok", StringComparison.Ordinal) ? 13d : 15d;

        return new ModelPickerRow
        {
            Id = model.Id,
            Display = VeniceModelCatalog.GetListDisplayName(model),
            Tooltip = VeniceModelCatalog.BuildTooltip(model),
            Logo = logo,
            LogoSize = Math.Max(1, size - margin.Left - margin.Right),
            LogoMargin = margin,
            Letter = VeniceModelCatalog.GetLogoLetter(model.Id)
        };
    }
}
