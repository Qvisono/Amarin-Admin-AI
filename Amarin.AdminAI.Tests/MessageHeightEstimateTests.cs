using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Прикидка высоты ещё не построенного сообщения близка к настоящей: чем она точнее, тем меньше
/// сдвигается лента, пока фоновая дорисовка строит сообщения выше видимого.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class MessageHeightEstimateTests
{
    private readonly WpfFixture _wpf;

    public MessageHeightEstimateTests(WpfFixture wpf) => _wpf = wpf;

    public static TheoryData<string, string> Samples => new()
    {
        { "user", "Короткий вопрос" },
        { "user", "Средний вопрос про службу печати, которая не стартует после обновления Windows 11 и пишет ошибку 1068 в журнал событий." },
        { "user", string.Concat(Enumerable.Repeat("Длинный вопрос с подробностями и контекстом. ", 12)) },
        { "assistant", "Готово." },
        { "assistant", "Один абзац ответа средней длины, в котором объясняется, что произошло и что делать дальше, без кода и таблиц." },
        { "assistant", string.Join("\n\n", Enumerable.Repeat("Абзац ответа из двух-трёх строк текста, чтобы проверить перенос в ленте шириной около тысячи точек и интервалы между абзацами.", 5)) },
        { "assistant", "## Заголовок\n\nТекст под заголовком.\n\n- пункт один\n- пункт два\n- пункт три\n\n### Ещё заголовок\n\nИ ещё текст." },
        { "assistant", "Код:\n\n```powershell\n" + string.Concat(Enumerable.Repeat("Get-Service -Name Spooler | Select-Object Status\n", 6)) + "```\n\nИ пояснение." },
        { "assistant", "Большой код:\n\n```csharp\n" + string.Concat(Enumerable.Repeat("var x = Compute(a, b) + 1; // comment\n", 30)) + "```" },
        { "assistant", "| A | B | C |\n|---|---|---|\n" + string.Concat(Enumerable.Repeat("| 1 | 2 | 3 |\n", 8)) + "\nПосле таблицы." },
        { "assistant", "Два блока:\n\n```\nplain one\nplain two\n```\n\nтекст\n\n```json\n{\"a\": 1,\n \"b\": 2}\n```" }
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void The_estimate_is_close_to_the_built_height(string role, string text)
    {
        var (actual, estimate) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow
            {
                Left = -32000,
                Top = 0,
                Width = 1200,
                Height = 900,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            try
            {
                var panel = (StackPanel)window.FindName("MessagesPanel")!;
                var message = new ChatDisplayMessage
                {
                    Id = "h",
                    Role = role,
                    Text = text,
                    CreatedAt = DateTime.Now,
                    Status = AssistantStatus.Complete,
                    ResolvedModelId = role == "assistant" ? "grok-4-6" : null
                };
                var view = role == "user"
                    ? ChatMessageViews.CreateUser(window, message).Root
                    : ChatMessageViews.CreateAssistant(window, message).Root;
                var host = new ChatMessageHost { Message = message, Actions = new MessageActions() };
                host.Fill(view);
                panel.Children.Clear();
                panel.Children.Add(host);
                panel.UpdateLayout();
                return (host.ActualHeight, MessageHeightEstimate.For(message, panel.ActualWidth));
            }
            finally
            {
                window.Close();
            }
        });

        var ratio = estimate / actual;
        Assert.True(ratio is >= 0.8 and <= 1.25, $"прикидка {estimate:0} при настоящей высоте {actual:0} (×{ratio:0.00})");
    }

    [Fact]
    public void An_unfinished_code_block_still_counts_its_lines()
    {
        var closed = MessageHeightEstimate.Body("```\na\nb\nc\n```", 900, 21);
        var open = MessageHeightEstimate.Body("```\na\nb\nc", 900, 21);
        Assert.Equal(closed, open, 1);
    }
}
