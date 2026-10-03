using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Всплывающие окна 1.28.0 после чистки: одни стили, ошибка в подвале, тег с цветом и правкой,
/// короткая сводка в подтверждении.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class DialogRedesignTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-dialogs-" + Guid.NewGuid().ToString("N"));

    public DialogRedesignTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private T WithWindow<T>(Func<MainWindow, AppServices, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(_root, "k", new HttpClientHandler());
        var window = new MainWindow();
        window.AttachServices(services);
        try
        {
            return body(window, services);
        }
        finally
        {
            window.Close();
        }
    });

    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(target, args);

    private static double CardHeight(MainWindow window, string overlay)
    {
        var card = (FrameworkElement)((Viewbox)((Grid)window.FindName(overlay)!).Children.OfType<Viewbox>().Single()).Child;
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return card.DesiredSize.Height;
    }

    /// <summary>
    /// Отказ ключа пишется в подвал, слева от кнопок: окно стоит по центру, и выросшее от
    /// строки ошибки оно подпрыгивало бы. Прежде ошибка вставала под полем.
    /// </summary>
    [Fact]
    public void A_key_error_does_not_change_the_dialog_height()
    {
        var (before, after, text) = WithWindow((window, _) =>
        {
            window.OpenKeyDialog();
            var height = CardHeight(window, "KeyOverlay");
            Call(window, "ShowKeyDialogError", "Провайдер этот ключ не принял, и это длинное объяснение");
            var status = (TextBlock)window.FindName("KeyDialogStatus")!;
            return (height, CardHeight(window, "KeyOverlay"), status.Text);
        });

        Assert.Equal(before, after, 1);
        Assert.False(string.IsNullOrEmpty(text));
    }

    /// <summary>Удаление в общем окне вопроса — красной кнопкой, обычный вопрос — главной.</summary>
    [Fact]
    public void A_destructive_question_gets_the_danger_button()
    {
        var (danger, plain) = WithWindow((window, services) =>
        {
            // Словарь стилей подключён у каждого окна свой — сверять с ресурсами этого же окна.
            string Key(Style style) => new[] { "DialogDangerButton", "DialogPrimaryButton" }
                .Single(key => ReferenceEquals(window.FindResource(key), style));
            var button = (Button)window.FindName("NoticePrimaryButton")!;
            _ = window.ShowNoticeAsync("Удалить?", "", "Удалить", "Отмена", NoticeTone.Danger);
            var first = Key(button.Style);
            _ = window.ShowNoticeAsync("Сжать?", "", "Сжать", "Отмена");
            return (first, Key(button.Style));
        });

        Assert.Equal("DialogDangerButton", danger);
        Assert.Equal("DialogPrimaryButton", plain);
    }

    /// <summary>
    /// Тег открывается на правку: имя и цвет меняются, «Удалить» есть. Прежде тег можно было
    /// только удалить, а цвет назначался сам.
    /// </summary>
    [Fact]
    public void A_tag_can_be_renamed_and_recoloured()
    {
        var (deleteVisible, colors, tag) = WithWindow((window, services) =>
        {
            var created = services.Organizer.CreateTag("work", ChatOrganizer.TagColors[0]);
            Call(window, "OpenTagDialog", created, null);
            var delete = ((FrameworkElement)window.FindName("NameDeleteButton")!).Visibility;
            var swatches = ((Panel)window.FindName("NameColors")!).Children.OfType<RadioButton>().ToList();
            swatches[3].IsChecked = true;
            ((TextBox)window.FindName("NameInput")!).Text = "home";
            Call(window, "NameSaveButton_Click", window, new RoutedEventArgs());
            return (delete, swatches.Count, services.Organizer.Snapshot().Tags.Single());
        });

        Assert.Equal(Visibility.Visible, deleteVisible);
        Assert.Equal(ChatOrganizer.TagColors.Length, colors);
        Assert.Equal("home", tag.Name);
        Assert.Equal(ChatOrganizer.TagColors[3], tag.Color);
    }

    /// <summary>
    /// У тега в меню своя «⋯» — изменить или удалить, без захода в отдельный пункт. Щелчок по ней
    /// не срабатывает как щелчок по строке: фильтр не ставится и тег на чат не вешается.
    /// </summary>
    [Fact]
    public void A_tag_in_a_menu_has_its_own_actions_button()
    {
        var (rowFired, hasButton) = WithWindow((window, services) =>
        {
            var tag = services.Organizer.CreateTag("work", ChatOrganizer.TagColors[0]);
            var fired = false;
            var item = (MenuItem)Call(window, "CheckItem", "work", false, tag.Color, (Action)(() => fired = true), tag)!;
            var button = ((Grid)item.Header).Children.OfType<Button>().SingleOrDefault();
            button?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
            return (fired, button is not null);
        });

        Assert.True(hasButton);
        Assert.False(rowFired);
    }

    /// <summary>Цвета тегов различимы в тёмной теме: двух почти белых больше нет.</summary>
    [Fact]
    public void Tag_colours_are_distinct_in_the_dark_theme()
    {
        var colors = _wpf.Ui.Invoke(() =>
        {
            var palette = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Amarin Admin AI;component/UI/Theme/Palette.Obsidian.xaml")
            };
            return ChatOrganizer.TagColors.Select(key => ((SolidColorBrush)palette[key]).Color).ToList();
        });

        Assert.Equal(colors.Count, colors.Distinct().Count());
    }

    /// <summary>
    /// Сводка PowerShell в окне подтверждения — без эха команды: та стоит ниже целиком. «Меняет
    /// систему: …» остаётся, а у сводки без кода (путь, служба) ничего не режется.
    /// </summary>
    [Fact]
    public void The_confirmation_summary_does_not_echo_the_script()
    {
        using var json = JsonDocument.Parse("""{"command":"Get-Service -Name Spooler | Restart-Service -Force"}""");
        var info = DangerousActionGuard.DescribeDetailed("run_powershell", json.RootElement.Clone());
        var plain = info with { CodeText = "", ChangeSummary = "Остановка службы: Spooler" };

        var summary = MainWindow.ConfirmationSummary(info);

        Assert.DoesNotContain("Get-Service", summary, StringComparison.Ordinal);
        Assert.Contains("Restart-Service", summary, StringComparison.Ordinal);
        Assert.Equal("Остановка службы: Spooler", MainWindow.ConfirmationSummary(plain));
    }

    /// <summary>Системных MessageBox в главном окне не осталось: только своё окно вопроса.</summary>
    [Fact]
    public void The_main_window_never_shows_a_system_message_box()
    {
        var ui = Path.Combine(SourceTree.ProjectDirectory, "UI");
        var offenders = Directory.GetFiles(ui, "*.cs")
            .Where(file => !file.EndsWith("MainWindow.Notice.cs", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("MessageBox.Show(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "MessageBox.Show в: " + string.Join(", ", offenders));
    }
}
