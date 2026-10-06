using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Доступность (I1): у каждой видимой кнопки есть имя для экранного диктора.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class AccessibilityTests
{
    private readonly WpfFixture _wpf;

    public AccessibilityTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Every_visible_button_of_the_window_and_its_settings_has_a_name()
    {
        var nameless = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            var found = new List<string>();
            try
            {
                // Общее окно тестов собрано, но не показано, и Loaded у его кнопок не приходит —
                // имя из подсказки ставим тем же кодом явным обходом.
                AccessibilityDefaults.ApplyTree(window);
                Collect(window, found);
                overlay.Visibility = Visibility.Visible;
                foreach (var nav in new[] { "NavGeneral", "NavAppearance", "NavData", "NavKey", "NavAbout" })
                {
                    ((RadioButton)window.FindSetting(nav)!).IsChecked = true;
                    window.UpdateLayout();
                    AccessibilityDefaults.ApplyTree(overlay);
                    Collect(overlay, found);
                }
            }
            finally
            {
                overlay.Visibility = Visibility.Collapsed;
                ((RadioButton)window.FindSetting("NavGeneral")!).IsChecked = true;
            }

            return found.Distinct().ToList();
        });

        Assert.True(nameless.Count == 0, "кнопки без имени: " + string.Join(" | ", nameless));
    }

    [Fact]
    public void An_icon_button_takes_its_tooltip_as_its_name_and_follows_it()
    {
        var (first, second) = _wpf.Ui.Invoke(() =>
        {
            AccessibilityDefaults.Register();
            var button = new Button { Content = new System.Windows.Shapes.Rectangle(), ToolTip = "Copy" };
            AccessibilityDefaults.NameFromToolTip(button);
            var peer = UIElementAutomationPeer.CreatePeerForElement(button);
            var before = peer.GetName();
            button.ToolTip = "Копировать";
            return (before, peer.GetName());
        });

        Assert.Equal("Copy", first);
        Assert.Equal("Копировать", second);
    }

    private static void Collect(DependencyObject root, List<string> found)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { IsVisible: false })
            {
                continue;
            }

            // Внутренности полос прокрутки, ползунков и выпадашек: их называет сам элемент, диктор внутрь не ходит.
            if (child is ScrollBar or Slider or ComboBox or TextBoxBase)
            {
                continue;
            }

            if (child is ButtonBase button && button.IsHitTestVisible &&
                UIElementAutomationPeer.CreatePeerForElement(button) is { } peer &&
                string.IsNullOrWhiteSpace(peer.GetName()))
            {
                found.Add(Describe(button));
            }

            Collect(child, found);
        }
    }

    private static string Describe(FrameworkElement element)
    {
        var path = new List<string>();
        DependencyObject? current = element;
        while (current is not null && path.Count < 4)
        {
            if (current is FrameworkElement { Name.Length: > 0 } named)
            {
                path.Add(named.Name);
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return element.GetType().Name + "@" + string.Join("<", path);
    }
}
