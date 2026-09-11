using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Выпадашка языка в настройках: список готовых языков, галка у выбранного и кнопка перевода.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class LanguagePickerTests
{
    private readonly WpfFixture _wpf;

    public LanguagePickerTests(WpfFixture wpf) => _wpf = wpf;

    private T Build<T>(string code, Func<LanguagePickerField, T> read) => _wpf.Ui.Invoke(() =>
    {
        var picker = new LanguagePickerField();
        picker.SetSelected(code);
        picker.Measure(new Size(240, 40));
        picker.Arrange(new Rect(0, 0, 240, 40));
        picker.UpdateLayout();
        return read(picker);
    });

    private static StackPanel List(LanguagePickerField picker) =>
        (StackPanel)picker.FindName("LanguageList")!;

    private static List<string> Texts(DependencyObject root)
    {
        var found = new List<string>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                found.Add(text.Text);
            }

            found.AddRange(Texts(child));
        }

        return found;
    }

    [Fact]
    public void Both_built_in_languages_are_offered()
    {
        var names = Build("ru", picker => List(picker).Children
            .OfType<Button>()
            .Select(b => ((TextBlock)((Grid)b.Content).Children[0]).Text)
            .ToList());

        Assert.Contains("Русский", names);
        Assert.Contains("English", names);
    }

    [Fact]
    public void The_field_shows_the_chosen_language_in_its_own_name()
    {
        Assert.Equal("English", Build("en", picker =>
            ((TextBlock)picker.FindName("SelectedLabel")!).Text));
        Assert.Equal("Русский", Build("ru", picker =>
            ((TextBlock)picker.FindName("SelectedLabel")!).Text));
    }

    [Fact]
    public void Only_the_chosen_language_carries_the_tick()
    {
        var ticks = Build("en", picker => List(picker).Children
            .OfType<Button>()
            .Select(b => ((Grid)b.Content).Children.Count)
            .ToList());

        // Строка с галкой держит два элемента, остальные — один.
        Assert.Equal(1, ticks.Count(count => count == 2));
    }

    [Fact]
    public void Picking_a_language_raises_the_event_for_every_language_but_the_current_one()
    {
        var (picked, offered) = _wpf.Ui.Invoke(() =>
        {
            var picker = new LanguagePickerField();
            picker.SetSelected("ru");
            var seen = new List<string>();
            picker.LanguagePicked += (_, code) => seen.Add(code);

            foreach (var button in List(picker).Children.OfType<Button>())
            {
                // Повторный выбор того же языка события не даёт — переключать нечего.
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }

            // На этой машине могут лежать языки, переведённые моделью, — считаем от списка,
            // а не от двух встроенных.
            return (seen, LanguageManager.Available().Select(item => item.Code).ToList());
        });

        Assert.Equal(offered.Where(code => code != "ru"), picked);
        Assert.Contains("en", picked);
        Assert.DoesNotContain("ru", picked);
    }

    [Fact]
    public void The_new_language_button_asks_the_window_not_the_picker()
    {
        var asked = _wpf.Ui.Invoke(() =>
        {
            var picker = new LanguagePickerField();
            var count = 0;
            picker.NewLanguageRequested += (_, _) => count++;
            ((Button)picker.FindName("NewLanguageButton")!)
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return count;
        });

        Assert.Equal(1, asked);
    }

    [Fact]
    public void The_progress_line_appears_only_while_there_is_something_to_say()
    {
        var (hidden, shown) = _wpf.Ui.Invoke(() =>
        {
            var picker = new LanguagePickerField();
            var before = ((TextBlock)picker.FindName("ProgressText")!).Visibility;
            picker.ShowProgress("Перевод: 1 из 8 частей");
            return (before, ((TextBlock)picker.FindName("ProgressText")!).Visibility);
        });

        Assert.Equal(Visibility.Collapsed, hidden);
        Assert.Equal(Visibility.Visible, shown);
    }
}
