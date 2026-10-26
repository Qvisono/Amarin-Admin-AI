using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Что видно в окне от раздела B 1.37.0: скорость в попапе размышления, кнопка ветки у ответа и
/// плашка временного чата.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SectionBWindowTests
{
    private readonly WpfFixture _wpf;

    public SectionBWindowTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    // ───────────────────────── скорость ─────────────────────────

    [Fact]
    public void The_picker_offers_the_speeds_of_the_model_and_nothing_for_others()
    {
        var (astra, venice) = _wpf.Ui.Invoke(() =>
        {
            var picker = Picker("openai:gpt-6-astra");
            var chips = ((WrapPanel)picker.FindName("SpeedHost")!).Children.OfType<RadioButton>()
                .Select(chip => (string)chip.Tag).ToList();
            var other = Picker("grok-4-6");
            return (chips, ((WrapPanel)other.FindName("SpeedHost")!).Visibility);
        });

        Assert.Equal(["flex", "", "fast", "ultrafast"], astra);
        Assert.Equal(Visibility.Collapsed, venice);
    }

    [Fact]
    public void Choosing_a_speed_is_reported_and_shown_on_the_button()
    {
        var (speed, label) = _wpf.Ui.Invoke(() =>
        {
            var picker = Picker("anthropic:claude-opus-5-5");
            string? chosen = null;
            picker.ChoiceChanged += (_, e) => chosen = e.Speed;
            var fast = ((WrapPanel)picker.FindName("SpeedHost")!).Children.OfType<RadioButton>()
                .Single(chip => (string)chip.Tag == ModelSpeeds.Fast);
            fast.IsChecked = true;
            return (chosen, picker.LabelText);
        });

        Assert.Equal(ModelSpeeds.Fast, speed);
        Assert.Contains(ModelSpeeds.Label(ModelSpeeds.Fast), label, StringComparison.Ordinal);
    }

    private static ReasoningPicker Picker(string model)
    {
        var picker = new ReasoningPicker();
        picker.SetState(model, autoMode: false, disableThinking: true, effort: null);
        picker.Measure(new Size(240, 40));
        picker.Arrange(new Rect(0, 0, 240, 40));
        picker.UpdateLayout();
        return picker;
    }

    // ───────────────────────── ветка ─────────────────────────

    [Fact]
    public void An_answer_offers_a_branch_into_a_new_chat()
    {
        var branched = _wpf.Ui.Invoke(() =>
        {
            ChatDisplayMessage? hit = null;
            var answer = new ChatDisplayMessage
            {
                Role = "assistant", Id = "a1", ResolvedModelId = "grok-4-6", Text = "ответ", Status = AssistantStatus.Complete
            };
            var view = ChatMessageViews.CreateAssistant(Window(), answer, new MessageActions { Branch = message => hit = message });
            var button = Descendants(view.Root).OfType<Button>()
                .Single(candidate => Equals(candidate.ToolTip, Loc.Get("S.Message.Branch")));
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return hit;
        });

        Assert.Equal("a1", branched?.Id);
    }

    /// <summary>
    /// Обводка растягивается по самой фигуре: с полем 4 ветка и скобки выгрузки выходили на 18 точек
    /// при 13-15 у соседей. Поле у них то же, что у корзины, — 14 точек.
    /// </summary>
    [Fact]
    public void The_branch_and_export_icons_get_the_same_field_as_their_outlined_neighbour()
    {
        var margins = _wpf.Ui.Invoke(() => new[] { "Branch", "ExportJson", "Delete" }
            .Select(key => ((Image)ChatMessageViews.IconAction(Window(), key).Content).Margin)
            .ToList());

        Assert.Equal(margins[2], margins[0]);
        Assert.Equal(margins[2], margins[1]);
    }

    [Fact]
    public void The_branch_icon_exists_in_both_themes()
    {
        var found = _wpf.Ui.Invoke(() =>
        {
            var dark = (ResourceDictionary)Application.LoadComponent(
                new Uri("/Amarin Admin AI;component/UI/Theme/Icons.Dark.xaml", UriKind.Relative));
            var light = (ResourceDictionary)Application.LoadComponent(
                new Uri("/Amarin Admin AI;component/UI/Theme/Icons.Light.xaml", UriKind.Relative));
            return (dark.Contains("Branch"), light.Contains("Branch"));
        });

        Assert.True(found.Item1, "значка ветки нет в тёмном словаре");
        Assert.True(found.Item2, "значка ветки нет в светлом словаре");
    }

    // ───────────────────────── временный чат ─────────────────────────

    /// <summary>
    /// Инкогнито в строке заголовка (1.37.0-beta.7): у пустого чата делает его временным и обратно.
    /// Середина обычного чата пуста, у временного там рисунок; включённая кнопка остаётся на месте.
    /// </summary>
    [Fact]
    public void The_incognito_button_makes_an_empty_chat_temporary_and_back() => With((window, _) =>
    {
        window.StartNewChatFromUi();
        var button = window.TemporaryChatButton;
        Assert.Equal(Visibility.Visible, button.Visibility);
        Assert.Equal(Visibility.Collapsed, window.TemporaryChatEmpty.Visibility);

        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(Session(window).IsTemporary);
        Assert.True(button.IsChecked);
        Assert.Equal(Visibility.Visible, button.Visibility);
        Assert.Equal(Visibility.Visible, window.TemporaryChatEmpty.Visibility);
        Assert.Equal(Loc.Get("S.Temporary.TurnOff"), button.ToolTip);

        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.False(Session(window).IsTemporary);
        Assert.Equal(Visibility.Collapsed, window.TemporaryChatEmpty.Visibility);
        Assert.Equal(Visibility.Collapsed, window.TemporaryChatChip.Visibility);
        return true;
    });

    /// <summary>Включённый режим зачёркивает значок: черта и разрыв вокруг неё.</summary>
    [Fact]
    public void The_incognito_icon_is_crossed_out_while_the_chat_is_temporary() => With((window, _) =>
    {
        window.StartNewChatFromUi();
        var button = window.TemporaryChatButton;
        button.ApplyTemplate();
        var slash = (UIElement)button.Template.FindName("Slash", button);
        var figure = (UIElement)button.Template.FindName("Figure", button);
        Assert.Equal(Visibility.Collapsed, slash.Visibility);
        Assert.Null(figure.Clip);

        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(Visibility.Visible, slash.Visibility);
        Assert.NotNull(figure.Clip);
        return true;
    });

    /// <summary>«Приватность» — своя подстраница «Безопасности», и её настройки лежат в ней.</summary>
    [Fact]
    public void Privacy_is_a_subpage_of_security_with_its_settings() => _wpf.Ui.Invoke(() =>
    {
        var window = Window();
        var privacy = (FrameworkElement)window.FindSetting("PrivacySub");
        var link = (FrameworkElement)window.FindSetting("PrivacyLinkRow");

        Assert.Same(privacy, SettingsDrill.GetOpens(link));
        foreach (var name in new[] { "EncryptChatsToggle", "NoRetentionToggle", "TemporaryUntilCloseToggle" })
        {
            var toggle = (DependencyObject)window.FindSetting(name);
            Assert.True(IsInside(toggle, privacy), name);
        }

        return true;
    });

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var current = element; current is not null; current = LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }

        return false;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Leaving_a_temporary_chat_keeps_it_until_close_or_drops_it_at_once(bool untilClose) => With((window, services) =>
    {
        services.Settings.TemporaryChatsUntilClose = untilClose;
        window.StartNewChatFromUi();
        window.TemporaryChatButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var temporary = Session(window);
        temporary.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "m1", Text = "secret-plan" });

        window.StartNewChatFromUi();

        var listed = ((IReadOnlyList<ChatIndexEntry>)typeof(MainWindow)
                .GetMethod("ChatListItems", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [""])!)
            .Any(entry => entry.Id == temporary.Id);
        Assert.Equal(untilClose, listed);
        Assert.Null(services.ChatStore.TryLoad(temporary.Id));
        return true;
    });

    private T With<T>(Func<MainWindow, AppServices, T> body) => _wpf.Ui.Invoke(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-section-b-" + Guid.NewGuid().ToString("N"));
        var services = UiServices.Build(root, "k", new HttpClientHandler());
        var window = new MainWindow();
        window.AttachServices(services);
        try
        {
            return body(window, services);
        }
        finally
        {
            window.Close();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });

    private static ChatSession Session(MainWindow window) =>
        (ChatSession)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }
}
