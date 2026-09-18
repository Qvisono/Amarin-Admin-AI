using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Поиск по чатам и вкладка сводок — на живом окне, потому что весь их смысл в том, что
/// показано на экране и когда.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ChatSearchUiTests
{
    private readonly WpfFixture _wpf;

    public ChatSearchUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static T Named<T>(MainWindow window, string name) where T : class =>
        (T)window.FindName(name)!;

    private static void Invoke(MainWindow window, string name) =>
        typeof(MainWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    private static object Field(MainWindow window, string name) =>
        typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static void Reset(MainWindow window)
    {
        Named<TextBox>(window, "SearchBox").Clear();
        Named<ToggleButton>(window, "SearchModeTitles").IsChecked = true;
        Named<ToggleButton>(window, "SearchModeContent").IsChecked = false;
        typeof(MainWindow)
            .GetField("_searchByContent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, false);
        Invoke(window, "SyncSearchModeRow");
    }

    [Fact]
    public void The_mode_switch_shows_up_only_when_there_is_something_to_search()
    {
        var (empty, typed, cleared) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Reset(window);
            var row = Named<StackPanel>(window, "SearchModeRow");

            var atRest = row.Visibility;
            Named<TextBox>(window, "SearchBox").Text = "принтер";
            var withQuery = row.Visibility;
            Named<TextBox>(window, "SearchBox").Clear();
            var afterClear = row.Visibility;

            Reset(window);
            return (atRest, withQuery, afterClear);
        });

        // Место в боковой панели дорого: выбор режима осмыслен только при непустом запросе.
        Assert.Equal(Visibility.Collapsed, empty);
        Assert.Equal(Visibility.Visible, typed);
        Assert.Equal(Visibility.Collapsed, cleared);
    }

    [Fact]
    public void Exactly_one_mode_is_chosen_at_a_time()
    {
        var (titlesOn, contentOn, backToTitles) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Reset(window);

            var titles = Named<ToggleButton>(window, "SearchModeTitles");
            var content = Named<ToggleButton>(window, "SearchModeContent");

            var start = titles.IsChecked == true && content.IsChecked != true;

            content.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var switched = content.IsChecked == true && titles.IsChecked != true;

            titles.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var back = titles.IsChecked == true && content.IsChecked != true;

            Reset(window);
            return (start, switched, back);
        });

        Assert.True(titlesOn);
        Assert.True(contentOn);
        Assert.True(backToTitles);
    }

    [Fact]
    public void Typing_in_content_mode_waits_for_Enter_instead_of_asking_the_model()
    {
        var state = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Reset(window);

            Named<ToggleButton>(window, "SearchModeContent")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Named<TextBox>(window, "SearchBox").Text = "принтер";

            var current = Field(window, "_contentSearchState").ToString();
            Reset(window);
            Named<TextBox>(window, "SearchBox").Clear();
            return current;
        });

        // Запрос модели на каждую букву стоил бы денег на каждом нажатии клавиши.
        Assert.Equal("Prompt", state);
    }

    [Fact]
    public void Leaving_content_mode_forgets_the_answer_it_had()
    {
        var (before, after) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Reset(window);

            Named<ToggleButton>(window, "SearchModeContent")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Named<TextBox>(window, "SearchBox").Text = "принтер";
            var was = Field(window, "_contentSearchState").ToString();

            Named<ToggleButton>(window, "SearchModeTitles")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var now = Field(window, "_contentSearchState").ToString();

            Reset(window);
            return (was, now);
        });

        Assert.Equal("Prompt", before);

        // Ответ модели относился к другому вопросу — держать его дальше нельзя.
        Assert.Equal("Off", after);
    }

    [Fact]
    public void An_empty_query_keeps_the_date_groups_even_in_content_mode()
    {
        var (withQuery, emptied) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Reset(window);

            Named<ToggleButton>(window, "SearchModeContent")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Named<TextBox>(window, "SearchBox").Text = "принтер";
            var typed = (bool)typeof(MainWindow)
                .GetProperty("IsContentSearchResult", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;

            Named<TextBox>(window, "SearchBox").Clear();
            var cleared = (bool)typeof(MainWindow)
                .GetProperty("IsContentSearchResult", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;

            Reset(window);
            return (typed, cleared);
        });

        // Режим включён, но ответа модели нет — список обычный и группы по датам ему нужны.
        // Иначе все чаты разом уезжали бы под заголовок «Найдено».
        Assert.False(withQuery);
        Assert.False(emptied);
    }

    [Fact]
    public void The_journal_has_a_third_tab_and_it_hides_the_scope_switch()
    {
        var (checkedTab, scope) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Invoke(window, "OpenJournal");

            var tab = Named<ToggleButton>(window, "JournalSummariesTab");
            tab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();

            var result = (
                tab.IsChecked == true,
                Named<StackPanel>(window, "JournalScopePanel").Visibility);

            Named<ToggleButton>(window, "JournalActionsTab")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Invoke(window, "CloseJournal");
            return result;
        });

        Assert.True(checkedTab);

        // Сводки всегда по всем чатам — выбирать охват нечего, как и на точках отката.
        Assert.Equal(Visibility.Collapsed, scope);
    }

    [Fact]
    public void The_journal_is_called_just_that()
    {
        var title = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Invoke(window, "OpenJournal");
            var text = Named<TextBlock>(window, "JournalTitleText").Text;
            Invoke(window, "CloseJournal");
            return text;
        });

        // Прежний «Что модель делала с компьютером» не влезал в карточку и обрывался.
        Assert.Equal("Журнал", title);
    }
}
