using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Скриншот в рамке с подписью для страницы Info.
/// </summary>
/// <remarks>
/// Картинки гайда лежат ресурсами сборки в <c>Assets/Guide</c> и появляются там по мере того,
/// как их снимают. Обращение к отсутствующему ресурсу роняет загрузку всей разметки страницы
/// <see cref="System.IO.IOException"/>, поэтому файл ищется в коде, а не привязкой в XAML:
/// нет файла — на его месте остаётся заглушка, страница и тесты продолжают работать.
/// </remarks>
public partial class GuideShot : UserControl
{
    /// <summary>
    /// Путь к папке с картинками гайда.
    /// </summary>
    /// <remarks>
    /// Адрес обязан быть квалифицирован именем сборки: голый <c>/Assets/...</c> резолвится
    /// относительно <see cref="Application.ResourceAssembly"/>, а под тестовым раннером и в
    /// конструкторе его нет. Та же ловушка описана у ThemeManager и LanguageManager.
    /// </remarks>
    private static readonly string AssetRoot =
        $"pack://application:,,,/{Assembly.GetExecutingAssembly().GetName().Name};component/Assets/Guide/";

    public GuideShot()
    {
        InitializeComponent();
    }

    /// <summary>Имя файла картинки в <c>Assets/Guide</c>, например <c>venice-01-signup.png</c>.</summary>
    public static readonly DependencyProperty FileNameProperty = DependencyProperty.Register(
        nameof(FileName), typeof(string), typeof(GuideShot),
        new PropertyMetadata(string.Empty, OnFileNameChanged));

    public string FileName
    {
        get => (string)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    /// <summary>Подпись под рамкой.</summary>
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(GuideShot),
        new PropertyMetadata(string.Empty));

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>Текст заглушки, когда картинки ещё нет. Заполняется здесь же.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(GuideShot),
        new PropertyMetadata(string.Empty));

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        private set => SetValue(PlaceholderProperty, value);
    }

    private static void OnFileNameChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((GuideShot)sender).Reload();

    private void Reload()
    {
        var name = FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowPlaceholder(string.Empty);
            return;
        }

        var image = TryLoad(name);
        if (image is null)
        {
            ShowPlaceholder(name);
            return;
        }

        Shot.Source = image;
        Shot.Visibility = Visibility.Visible;
        Missing.Visibility = Visibility.Collapsed;
    }

    private void ShowPlaceholder(string name)
    {
        Shot.Source = null;
        Shot.Visibility = Visibility.Collapsed;
        Missing.Visibility = Visibility.Visible;
        Placeholder = name.Length == 0 ? string.Empty : Loc.Format("S.Info.Shot.Missing", name);
    }

    private static BitmapImage? TryLoad(string name)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(AssetRoot + name, UriKind.Absolute);
            // OnLoad, иначе поток ресурса остаётся открытым до первой отрисовки.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException)
        {
            // Картинку ещё не сняли или она битая — это обычное состояние гайда, не авария.
            return null;
        }
    }
}
