using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Тени всплывающих окон не обрезаются. У попапа своё прозрачное окно ровно по содержимому, и
/// тень, нарисованная за край карточки, видна только в её полях: у меню поля стояли на глаз
/// (10, 6, 10, 12) при тени, уходящей на 14 и 22 точки, а у части пикеров полей не было вовсе.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class PopupShadowTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private readonly WpfFixture _wpf;

    public PopupShadowTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_shared_shadow_fits_inside_the_shared_margin()
    {
        var edges = _wpf.Ui.Invoke(() =>
        {
            var card = new Border
            {
                Width = 200,
                Height = 100,
                Background = Brushes.White,
                CornerRadius = new CornerRadius(10),
                Margin = PopupShadow.Margin,
                Effect = PopupShadow.Effect
            };
            return EdgeAlpha(new Grid { Children = { card } });
        });

        Assert.True(edges == 0, $"на краю поля тень плотностью {edges}/255");
    }

    [Fact]
    public void Every_popup_draws_its_whole_shadow()
    {
        var results = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var checks = new List<(string Name, int Edge)>();
            foreach (var name in new[] { "ReplyPill", "QuoteSuggestPopup", "CommandSuggestPopup", "ActionsPopup", "ModelPicker", "ConfirmationAllowPopup", "UpdateBadgePopup" })
            {
                checks.Add((name, Detached((Popup)window.FindName(name)!)));
            }

            foreach (var (name, control, popup) in new (string, FrameworkElement, string)[]
                     {
                         ("ModelPickerField", new ModelPickerField(), "PickerPopup"),
                         ("ProviderKeyField", new ProviderKeyField(), "ChoicePopup"),
                         ("ColorPickerField", new ColorPickerField(), "PickerPopup"),
                         ("LanguagePickerField", new LanguagePickerField(), "PickerPopup"),
                         ("ReasoningPicker", new ReasoningPicker(), "PickerPopup"),
                         ("TargetPicker", new TargetPicker(), "Popup")
                     })
            {
                checks.Add((name, Detached((Popup)control.FindName(popup)!)));
            }

            var menu = new ContextMenu { Style = (Style)window.FindResource("AppContextMenu") };
            menu.Items.Add(new MenuItem { Header = "Переименовать" });
            menu.Items.Add(new MenuItem { Header = "Удалить" });
            checks.Add(("AppContextMenu", EdgeAlpha(menu)));

            var combo = new ComboBox { Style = DarkComboBox(), Width = 160, ItemsSource = new[] { "один", "два" } };
            var host = new Grid { Children = { combo } };
            host.Measure(new Size(400, 100));
            host.Arrange(new Rect(0, 0, 400, 100));
            combo.ApplyTemplate();
            checks.Add(("DarkComboBox", Detached(FindPopup(combo)!)));
            return checks;
        });

        Assert.All(results, check => Assert.True(check.Edge == 0, $"{check.Name}: на краю окна попапа тень плотностью {check.Edge}/255"));
    }

    /// <summary>
    /// Поля стали шире, а карточки — там же, где были: смещения попапов вернули их на место.
    /// Числа — где стояла видимая карточка до 1.30.0 (смещение плюс прежнее поле).
    /// </summary>
    [Fact]
    public void The_cards_stay_where_they_were()
    {
        var shifts = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var cards = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
            foreach (var name in new[] { "ReplyPill", "QuoteSuggestPopup", "CommandSuggestPopup", "ActionsPopup", "ModelPicker", "ConfirmationAllowPopup", "UpdateBadgePopup" })
            {
                cards[name] = Card((Popup)window.FindName(name)!);
            }

            foreach (var (name, control, popup) in new (string, FrameworkElement, string)[]
                     {
                         ("ColorPickerField", new ColorPickerField(), "PickerPopup"),
                         ("LanguagePickerField", new LanguagePickerField(), "PickerPopup"),
                         ("ReasoningPicker", new ReasoningPicker(), "PickerPopup"),
                         ("TargetPicker", new TargetPicker(), "Popup")
                     })
            {
                cards[name] = Card((Popup)control.FindName(popup)!);
            }

            var menu = (Style)window.FindResource("AppContextMenu");
            cards["AppContextMenu"] = (
                Setter(menu, ContextMenuService.HorizontalOffsetProperty, ContextMenu.HorizontalOffsetProperty) + PopupShadow.Margin.Left,
                Setter(menu, ContextMenuService.VerticalOffsetProperty, ContextMenu.VerticalOffsetProperty) + PopupShadow.Margin.Top);

            var combo = new ComboBox { Style = DarkComboBox() };
            combo.ApplyTemplate();
            cards["DarkComboBox"] = Card(FindPopup(combo)!);
            return cards;
        });

        // Снизу и у точки мыши — левый верхний угол карточки от якоря; сверху — левый нижний.
        Assert.Equal((12.0, 20.0), shifts["ReplyPill"]);
        Assert.Equal((6.0, -4.0), shifts["QuoteSuggestPopup"]);
        Assert.Equal((6.0, -4.0), shifts["CommandSuggestPopup"]);
        Assert.Equal((0.0, -8.0), shifts["ActionsPopup"]);
        Assert.Equal((0.0, -8.0), shifts["ModelPicker"]);
        Assert.Equal((0.0, -6.0), shifts["ConfirmationAllowPopup"]);
        Assert.Equal((-70.0, 4.0), shifts["ColorPickerField"]);
        Assert.Equal((-40.0, 4.0), shifts["LanguagePickerField"]);
        Assert.Equal((0.0, -8.0), shifts["ReasoningPicker"]);
        Assert.Equal((0.0, -8.0), shifts["TargetPicker"]);
        Assert.Equal((10.0, 6.0), shifts["AppContextMenu"]);
        Assert.Equal((0.0, 4.0), shifts["DarkComboBox"]);

        // Попап значка обновления — правым краем по значку (30 точек), на 6 ниже шапки.
        Assert.Equal((-250.0, 6.0), shifts["UpdateBadgePopup"]);
    }

    /// <summary>
    /// Пикеры, которые выравнивают плашку по правому краю кнопки, считают смещение в коде: поле
    /// под тень справа лежит за карточкой и в выравнивание не входит.
    /// </summary>
    [Fact]
    public void Right_aligned_pickers_still_line_up_with_their_button()
    {
        var gaps = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var result = new List<(string, double)>();
            foreach (var (name, control, popup, button) in new (string, FrameworkElement, string, string)[]
                     {
                         ("ModelPickerField", new ModelPickerField { Width = 200 }, "PickerPopup", "OpenButton"),
                         ("ProviderKeyField", new ProviderKeyField { Width = 200 }, "ChoicePopup", "OpenButton")
                     })
            {
                var host = new Window
                {
                    Width = 400,
                    Height = 200,
                    Left = -32000,
                    Top = 0,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = control
                };
                host.Show();
                try
                {
                    var open = (ToggleButton)control.FindName(button)!;
                    var target = (Popup)control.FindName(popup)!;
                    open.IsChecked = true;
                    host.UpdateLayout();
                    var card = target.Child is ModelPickerPanel panel ? (FrameworkElement)panel.FindName("PanelFrame")! : (FrameworkElement)target.Child;
                    var right = target.HorizontalOffset + PopupShadow.Margin.Left + card.ActualWidth;
                    result.Add((name, right - open.ActualWidth));
                    open.IsChecked = false;
                }
                finally
                {
                    host.Close();
                }
            }

            return result;
        });

        Assert.All(gaps, gap => Assert.True(Math.Abs(gap.Item2) < 0.5, $"{gap.Item1}: правый край плашки разошёлся с кнопкой на {gap.Item2:0.#}"));
    }

    /// <summary>
    /// Все прозрачные попапы с тенью берут её и поля одни на всех: новый попап с тенью «на глаз»
    /// снова обрезал бы её.
    /// </summary>
    [Fact]
    public void Popups_take_the_shared_shadow_and_margin()
    {
        var offenders = new List<string>();
        foreach (var file in SourceTree.SourceFiles().Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            var popups = document.Descendants(Presentation + "Popup")
                .Where(popup => (string?)popup.Attribute("AllowsTransparency") == "True")
                .Concat(document.Descendants(Presentation + "ControlTemplate")
                    .Where(template => (string?)template.Attribute("TargetType") == "ContextMenu"));
            foreach (var popup in popups)
            {
                foreach (var element in popup.Descendants().Where(element => element.Attribute("Effect") is not null || element.Name.LocalName.EndsWith(".Effect", StringComparison.Ordinal)))
                {
                    var effect = (string?)element.Attribute("Effect");
                    var margin = (string?)element.Attribute("Margin");
                    if (effect != "{x:Static local:PopupShadow.Effect}" || margin != "{x:Static local:PopupShadow.Margin}")
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>Стиль выпадашки настроек — из её собственного словаря: окно его не держит.</summary>
    private static Style DarkComboBox() =>
        (Style)new ResourceDictionary
        {
            Source = new Uri("/Amarin Admin AI;component/UI/SettingsPageStyles.xaml", UriKind.Relative)
        }["DarkComboBox"];

    /// <summary>Где видимая карточка встаёт относительно якоря попапа.</summary>
    private static (double X, double Y) Card(Popup popup)
    {
        var margin = PopupShadow.Margin;
        var x = popup.HorizontalOffset + margin.Left;
        var y = popup.Placement == PlacementMode.Top ? popup.VerticalOffset - margin.Bottom : popup.VerticalOffset + margin.Top;
        return (x, y);
    }

    private static double Setter(Style style, DependencyProperty service, DependencyProperty own) =>
        style.Setters.OfType<System.Windows.Setter>()
            .Where(setter => setter.Property == own || setter.Property == service)
            .Select(setter => Convert.ToDouble(setter.Value, System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

    private static Popup? FindPopup(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Popup popup)
            {
                return popup;
            }

            if (FindPopup(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Содержимое попапа без самого попапа: отдельное окно попапа ровно по размеру содержимого,
    /// и то, что не поместилось в него, не видно, — ровно это и меряется.
    /// </summary>
    private static int Detached(Popup popup)
    {
        var child = popup.Child;
        popup.Child = null;
        var host = new Grid { Children = { child } };
        try
        {
            return EdgeAlpha(host);
        }
        finally
        {
            host.Children.Clear();
            popup.Child = child;
        }
    }

    /// <summary>Самая плотная точка крайних строк и столбцов картинки элемента.</summary>
    private static int EdgeAlpha(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.DesiredSize.Width));
        var height = Math.Max(1, (int)Math.Ceiling(element.DesiredSize.Height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var worst = 0;
        for (var x = 0; x < width; x++)
        {
            worst = Math.Max(worst, Math.Max(pixels[(x * 4) + 3], pixels[((((height - 1) * width) + x) * 4) + 3]));
        }

        for (var y = 0; y < height; y++)
        {
            worst = Math.Max(worst, Math.Max(pixels[(y * width * 4) + 3], pixels[(((y * width) + width - 1) * 4) + 3]));
        }

        return worst;
    }
}
