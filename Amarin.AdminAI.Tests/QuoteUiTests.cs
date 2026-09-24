using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Ответить» на фрагмент ответа — со стороны окна: что попадает в цитату из выделения, как
/// цитаты стоят над полем ввода и в пузыре, как работают подсказка «@» и меню ленты.
/// </summary>
/// <remarks>
/// Окно общее на всю коллекцию, поэтому каждый тест, трогающий поле ввода, возвращает его
/// в прежнее состояние в <c>finally</c>.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class QuoteUiTests
{
    private readonly WpfFixture _wpf;

    public QuoteUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static T Named<T>(MainWindow window, string name) where T : class =>
        (T)window.FindName(name)!;

    private static object? Invoke(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, args);

    private static List<MessageQuote> Pending(MainWindow window) =>
        (List<MessageQuote>)typeof(MainWindow)
            .GetField("_pendingQuotes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static void Reset(MainWindow window)
    {
        Invoke(window, "ClearPendingAttachments");
        Named<TextBox>(window, "MessageTextBox").Clear();
        window.UpdateLayout();
    }

    private static ChatDisplayMessage Answer(string text) => new()
    {
        Role = "assistant",
        Id = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTime.Now,
        Text = text,
        Status = AssistantStatus.Complete
    };

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

    private static void Layout(FrameworkElement element, double width = 700)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
    }

    // ───────────────────────── Выделение → текст ─────────────────────────

    [Fact]
    public void Code_and_formulas_survive_the_selection()
    {
        // Selection.Text выбрасывает всё, что нарисовано элементом: «выполните `netsh`» доезжало
        // бы до модели как «выполните».
        var text = _wpf.Ui.Invoke(() =>
        {
            var view = ChatMessageViews.CreateAssistant(Window(), Answer(
                "Выполните `netsh advfirewall` и проверьте $E = mc^2$.\n\n" +
                "- один\n- два\n\n" +
                "```powershell\nGet-Service\n```\n\nКонец."));
            Layout(view.Root);
            view.Body.SelectAll();
            return QuoteSelection.ExtractText(view.Body.Selection.Start, view.Body.Selection.End);
        });

        var normalized = ChatQuotes.Normalize(text);
        Assert.Contains("Выполните `netsh advfirewall` и проверьте $E = mc^2$.", normalized, StringComparison.Ordinal);
        Assert.Contains("- один\n- два", normalized, StringComparison.Ordinal);
        Assert.Contains("```powershell\nGet-Service\n```", normalized, StringComparison.Ordinal);
        Assert.EndsWith("Конец.", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', normalized);
    }

    [Fact]
    public void A_short_selection_brings_its_sentence_along()
    {
        var draft = _wpf.Ui.Invoke(() =>
        {
            var message = Answer("Откройте порт 8080 в брандмауэре Windows и перезапустите службу.");
            var view = ChatMessageViews.CreateAssistant(Window(), message);
            Layout(view.Root);

            var document = view.Body.Document;
            var range = QuoteSelection.Locate(document, "8080")!;
            view.Body.Selection.Select(range.Start, range.End);
            return QuoteSelection.Capture(view.Body, message.Id);
        });

        Assert.NotNull(draft);
        Assert.Equal("8080", draft.Text);
        Assert.Equal("Откройте порт 8080 в брандмауэре Windows и перезапустите службу.", draft.Context);
    }

    [Fact]
    public void Nothing_selected_means_nothing_to_quote()
    {
        var draft = _wpf.Ui.Invoke(() =>
        {
            var message = Answer("Текст ответа.");
            var view = ChatMessageViews.CreateAssistant(Window(), message);
            return QuoteSelection.Capture(view.Body, message.Id);
        });

        Assert.Null(draft);
    }

    [Fact]
    public void The_quote_is_found_again_across_formatting()
    {
        // Карточка цитаты ведёт к её месту в ответе — через жирный, курсив и перенос строк.
        var found = _wpf.Ui.Invoke(() =>
        {
            var view = ChatMessageViews.CreateAssistant(Window(), Answer("Сначала **остановите службу**, потом *удалите* файл."));
            Layout(view.Root);
            var range = QuoteSelection.Locate(view.Body.Document, "остановите службу, потом удалите");
            return range?.Text;
        });

        Assert.Equal("остановите службу, потом удалите", found);
    }

    // ───────────────────────── Меню ленты ─────────────────────────

    [Fact]
    public void Chat_boxes_step_aside_from_the_system_menu_and_the_journal_keeps_it()
    {
        // Локальный null — сигнал редактору текста не открывать своё меню и не гасить событие:
        // только так правый клик доходит до ленты. У журнала ленты нет — там меню остаётся.
        var (body, user, chatCode, journalCode) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var view = ChatMessageViews.CreateAssistant(window, Answer("```\nls\n```"));
            var bubble = ChatMessageViews.CreateUser(window, new ChatDisplayMessage { Role = "user", Id = "u", Text = "вопрос" });
            var chatInner = FindInner(view.Body)!;
            var journal = FindInner(CodeBlockView.Create(window, "ls", null))!;
            return (
                view.Body.ReadLocalValue(FrameworkElement.ContextMenuProperty),
                bubble.Display.ReadLocalValue(FrameworkElement.ContextMenuProperty),
                chatInner.ReadLocalValue(FrameworkElement.ContextMenuProperty),
                journal.ReadLocalValue(FrameworkElement.ContextMenuProperty));
        });

        Assert.Null(body);
        Assert.Null(user);
        Assert.Null(chatCode);
        Assert.Same(DependencyProperty.UnsetValue, journalCode);
    }

    private static RichTextBox? FindInner(DependencyObject root)
    {
        if (root is RichTextBox box && !QuoteSelection.GetIsQuotable(box))
        {
            return box;
        }

        if (root is RichTextBox outer)
        {
            foreach (var container in outer.Document.Blocks.OfType<BlockUIContainer>())
            {
                if (FindInner(container.Child) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        if (root is Decorator { Child: { } child })
        {
            return FindInner(child);
        }

        if (root is Panel panel)
        {
            foreach (UIElement item in panel.Children)
            {
                if (FindInner(item) is { } found)
                {
                    return found;
                }
            }
        }

        if (root is ContentControl { Content: DependencyObject content })
        {
            return FindInner(content);
        }

        return null;
    }

    [Fact]
    public void Only_the_body_of_a_reply_is_quotable()
    {
        var (body, user) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var view = ChatMessageViews.CreateAssistant(window, Answer("ответ"));
            var bubble = ChatMessageViews.CreateUser(window, new ChatDisplayMessage { Role = "user", Id = "u", Text = "вопрос" });
            return (QuoteSelection.GetIsQuotable(view.Body), QuoteSelection.GetIsQuotable(bubble.Display));
        });

        Assert.True(body);
        Assert.False(user);
    }

    [Fact]
    public void A_live_or_failed_reply_cannot_be_quoted()
    {
        // Документ живого ответа заменяется на каждой перерисовке, а текст упавшего — это текст
        // ошибки, а не слова модели.
        var (complete, streaming, error, own) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            bool Can(ChatDisplayMessage message) => (bool)Invoke(window, "CanQuote", message)!;
            return (
                Can(Answer("готово")),
                Can(new ChatDisplayMessage { Role = "assistant", Id = "s", Text = "пишу", Status = AssistantStatus.Streaming }),
                Can(new ChatDisplayMessage { Role = "assistant", Id = "e", Text = "ошибка", Status = AssistantStatus.Error }),
                Can(new ChatDisplayMessage { Role = "user", Id = "u", Text = "вопрос" }));
        });

        Assert.True(complete);
        Assert.False(streaming);
        Assert.False(error);
        Assert.False(own);
    }

    // ───────────────────────── Поле ввода ─────────────────────────

    [Fact]
    public void An_attached_quote_shows_above_the_field_and_changes_the_placeholder()
    {
        var (visible, rows, texts, placeholder) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            try
            {
                Invoke(window, "AttachQuote", new QuoteDraft("a1", "Перезапустите службу.", null));
                window.UpdateLayout();
                var panel = Named<StackPanel>(window, "QuotesPanel");
                return (
                    Named<FrameworkElement>(window, "AttachmentsHost").Visibility,
                    panel.Children.Count,
                    Texts(panel),
                    Named<TextBlock>(window, "ComposerPlaceholder").Text);
            }
            finally
            {
                Reset(window);
            }
        });

        Assert.Equal(Visibility.Visible, visible);
        Assert.Equal(1, rows);
        Assert.Contains("@1", texts);
        Assert.Contains("Перезапустите службу.", texts);
        Assert.Equal(StringsRu.Values["S.Composer.PlaceholderQuote"], placeholder);

        var after = _wpf.Ui.Invoke(() => (
            Named<FrameworkElement>(Window(), "AttachmentsHost").Visibility,
            Named<TextBlock>(Window(), "ComposerPlaceholder").Text));
        Assert.Equal(Visibility.Collapsed, after.Item1);
        Assert.Equal(StringsRu.Values["S.Composer.Placeholder"], after.Item2);
    }

    [Fact]
    public void Numbers_stay_put_duplicates_are_ignored_and_five_is_the_limit()
    {
        var (afterDuplicate, numbers, note) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            try
            {
                Invoke(window, "AttachQuote", new QuoteDraft("a1", "один", null));
                Invoke(window, "AttachQuote", new QuoteDraft("a1", "один", null));
                var duplicate = Pending(window).Count;

                Invoke(window, "AttachQuote", new QuoteDraft("a1", "два", null));
                Invoke(window, "RemovePendingQuote", Pending(window)[0]);
                for (var i = 0; i < 5; i++)
                {
                    Invoke(window, "AttachQuote", new QuoteDraft("a2", "фрагмент " + i, null));
                }

                return (
                    duplicate,
                    Pending(window).Select(q => q.Number).ToList(),
                    Named<TextBlock>(window, "AttachmentsWarning").Text);
            }
            finally
            {
                Reset(window);
            }
        });

        Assert.Equal(1, afterDuplicate);

        // «@2» не превращается в «@1», когда первую убрали: в тексте на неё уже могли сослаться.
        Assert.Equal([2, 3, 4, 5, 6], numbers);
        Assert.Contains("5", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Clicking_a_quote_row_writes_its_reference_at_the_caret()
    {
        var text = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            try
            {
                Invoke(window, "AttachQuote", new QuoteDraft("a1", "фрагмент", null));
                var box = Named<TextBox>(window, "MessageTextBox");
                box.Text = "согласен с";
                box.CaretIndex = box.Text.Length;

                var row = (Border)Named<StackPanel>(window, "QuotesPanel").Children[0];
                row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent
                });
                return box.Text;
            }
            finally
            {
                Reset(window);
            }
        });

        Assert.Equal("согласен с @1 ", text);
    }

    [Theory]
    [InlineData("", 0, 0, "@1 ")]
    [InlineData("про", 3, 3, "про @1 ")]
    [InlineData("про ", 4, 4, "про @1 ")]
    [InlineData("(", 1, 1, "(@1 ")]
    [InlineData("про и", 4, 4, "про @1 и")]
    public void The_reference_gets_spaces_only_where_missing(string text, int start, int end, string expected)
    {
        var token = MainWindow.ReferenceToken(text, start, end, 1);

        Assert.Equal(expected, text[..start] + token + text[end..]);
    }

    [Fact]
    public void Typing_at_opens_the_list_and_enter_picks_without_sending()
    {
        var (opened, text, closed) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            try
            {
                Invoke(window, "AttachQuote", new QuoteDraft("a1", "первый", null));
                Invoke(window, "AttachQuote", new QuoteDraft("a1", "второй", null));

                var box = Named<TextBox>(window, "MessageTextBox");
                box.Text = "про @";
                box.CaretIndex = box.Text.Length;
                var popup = Named<Popup>(window, "QuoteSuggestPopup");
                var wasOpen = popup.IsOpen;

                // Прямо в обработчик поля: так проверяется ровно то, что подсказка стоит в нём
                // раньше отправки, и не важно, досталась ли полю фокус в тестовом окне.
                var source = PresentationSource.FromVisual(window) ?? PresentationSource.FromVisual(box);
                foreach (var key in new[] { Key.Down, Key.Enter })
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, key)
                    {
                        RoutedEvent = UIElement.PreviewKeyDownEvent
                    };
                    Invoke(window, "MessageTextBox_PreviewKeyDown", box, args);
                    Assert.True(args.Handled);
                }

                return (wasOpen, box.Text, !popup.IsOpen);
            }
            finally
            {
                Reset(window);
            }
        });

        Assert.True(opened);

        // Enter выбрал вторую цитату и остался в поле: отправка забрала бы и очистила текст.
        Assert.Equal("про @2 ", text);
        Assert.True(closed);
    }

    [Fact]
    public void Without_quotes_at_is_just_a_character()
    {
        var opened = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            try
            {
                var box = Named<TextBox>(window, "MessageTextBox");
                box.Text = "почта @";
                box.CaretIndex = box.Text.Length;
                return Named<Popup>(window, "QuoteSuggestPopup").IsOpen;
            }
            finally
            {
                Reset(window);
            }
        });

        Assert.False(opened);
    }

    // ───────────────────────── Пузырь сообщения ─────────────────────────

    [Fact]
    public void The_bubble_shows_its_quotes_and_lights_up_their_references()
    {
        var (texts, lit, plain) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var answer = Answer("Перезапустите службу.");
            var user = new ChatDisplayMessage
            {
                Role = "user",
                Id = "u",
                CreatedAt = DateTime.Now,
                Text = "про @1 и @9",
                Quotes = [new MessageQuote { Number = 1, SourceMessageId = answer.Id, Text = "Перезапустите службу." }]
            };
            var transcript = new List<ChatDisplayMessage> { answer, user };
            var view = ChatMessageViews.CreateUser(window, user, new MessageActions { Transcript = () => transcript });
            Layout((FrameworkElement)view.Root);

            var runs = new List<Run>();
            foreach (var block in view.Display.Document.Blocks.OfType<Paragraph>())
            {
                runs.AddRange(block.Inlines.OfType<Run>());
            }

            var one = runs.Single(run => run.Text == "@1");
            return (
                Texts(view.Root),
                one.Background is not null && one.FontWeight == FontWeights.Normal,
                runs.Any(run => run.Text == "@9"));
        });

        Assert.Contains("@1", texts);
        Assert.Contains("Перезапустите службу.", texts);
        Assert.Contains(StringsRu.Values["S.Quote.FromLast"], texts);
        Assert.True(lit);

        // «@9» ни на что не указывает — остаётся частью обычного текста.
        Assert.False(plain);
    }

    [Fact]
    public void A_quote_whose_reply_is_gone_says_so()
    {
        var texts = _wpf.Ui.Invoke(() =>
        {
            var user = new ChatDisplayMessage
            {
                Role = "user",
                Id = "u",
                Text = "про это",
                Quotes = [new MessageQuote { Number = 1, SourceMessageId = "удалён", Text = "фрагмент" }]
            };
            var transcript = new List<ChatDisplayMessage> { user };
            var view = ChatMessageViews.CreateUser(Window(), user, new MessageActions { Transcript = () => transcript });
            Layout((FrameworkElement)view.Root);
            return Texts(view.Root);
        });

        Assert.Contains(StringsRu.Values["S.Quote.SourceGone"], texts);
    }

    [Fact]
    public void A_message_with_quotes_reserves_room_for_them()
    {
        var (bare, quoted) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var plain = new ChatDisplayMessage { Role = "user", Id = "", Text = "вопрос" };
            var withQuotes = new ChatDisplayMessage
            {
                Role = "user",
                Id = "",
                Text = "вопрос",
                Quotes = [new MessageQuote { Number = 1, Text = "а" }, new MessageQuote { Number = 2, Text = "б" }]
            };
            return (
                (double)Invoke(window, "ReservedHeight", plain)!,
                (double)Invoke(window, "ReservedHeight", withQuotes)!);
        });

        Assert.Equal(bare + 2 * QuoteViews.CardHeightEstimate, quoted);
    }

    [Fact]
    public void The_reply_pill_opens_on_a_selection_and_attaches_it()
    {
        var (opened, attached, closed) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var panel = Named<StackPanel>(window, "MessagesPanel");
            var message = Answer("Откройте порт 8080 и перезапустите службу.");
            var view = ChatMessageViews.CreateAssistant(window, message);
            var host = new ChatMessageHost { Message = message, Actions = new MessageActions() };
            host.Fill(view.Root);
            panel.Children.Add(host);
            try
            {
                window.UpdateLayout();
                var range = QuoteSelection.Locate(view.Body.Document, "перезапустите службу")!;
                view.Body.Selection.Select(range.Start, range.End);

                Invoke(window, "EvaluateSelection", view.Body);
                var pill = Named<Popup>(window, "ReplyPill");
                var wasOpen = pill.IsOpen;

                Named<Button>(window, "ReplyPillButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return (wasOpen, Pending(window).Select(q => q.Text).ToList(), !pill.IsOpen);
            }
            finally
            {
                panel.Children.Remove(host);
                Reset(window);
            }
        });

        Assert.True(opened);
        Assert.Equal(["перезапустите службу"], attached);
        Assert.True(closed);
    }
}
