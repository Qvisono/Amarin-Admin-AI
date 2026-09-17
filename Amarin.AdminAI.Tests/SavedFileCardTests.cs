using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Плашка файла, который инструмент положил на диск.
/// </summary>
/// <remarks>
/// Прежде о скачанном файле говорила одна строка внутри свёрнутого списка инструментов, и чтобы
/// добраться до самого файла, человек шёл в проводник руками. Проверяется то, что видно только
/// в дереве визуалов: карточка появляется, называет файл и не двоится, когда тот же путь
/// вернули дважды.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class SavedFileCardTests
{
    private readonly WpfFixture _wpf;

    public SavedFileCardTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static ChatDisplayMessage Answer(params SavedFile[] files) => new()
    {
        Role = "assistant",
        Id = "a1",
        ResolvedModelId = "grok-4-6",
        Text = "Скачал.",
        Status = AssistantStatus.Complete,
        ToolRounds =
        [
            new ToolRound
            {
                Calls =
                [
                    new ToolCallRecord
                    {
                        Id = "c1",
                        Name = "download_file",
                        Status = ToolCallStatus.Done,
                        Success = true,
                        SavedFiles = [.. files]
                    }
                ]
            }
        ]
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

    private List<string> Render(ChatDisplayMessage message) => _wpf.Ui.Invoke(() =>
    {
        var view = ChatMessageViews.CreateAssistant(Window(), message);
        view.Root.Measure(new Size(900, 900));
        view.Root.Arrange(new Rect(0, 0, 900, 900));
        view.Root.UpdateLayout();
        return Texts(view.Root);
    });

    [Fact]
    public void A_downloaded_file_gets_a_card_with_its_name_type_and_size()
    {
        var path = Path.Combine(Path.GetTempPath(), "amarin-card-test.zip");
        File.WriteAllBytes(path, new byte[2048]);
        try
        {
            var texts = Render(Answer(new SavedFile(path, "amarin-card-test.zip", 2048)));

            Assert.Contains("amarin-card-test.zip", texts);
            Assert.Contains(texts, line => line.Contains("ZIP", StringComparison.Ordinal) &&
                                           line.Contains("2", StringComparison.Ordinal) &&
                                           line.Contains("КБ", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_without_an_extension_is_not_labelled_with_the_word_file()
    {
        // Рядом с настоящей иконкой «ФАЙЛ» на месте отсутствующего расширения сообщал бы, что
        // файл — это файл. Тип пишется, только когда его есть чем назвать.
        var texts = Render(Answer(new SavedFile(@"C:\Downloads\download", "download", 2048)));

        Assert.DoesNotContain(texts, line => line.Contains(Loc.Get("S.Attach.FileBadge"), StringComparison.Ordinal));
        Assert.Contains("download", texts);
    }

    [Fact]
    public void An_answer_without_files_shows_no_strip()
    {
        var texts = Render(Answer());
        Assert.DoesNotContain(texts, line => line.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_the_nested_agent_downloaded_shows_up_too()
    {
        // Человеку всё равно, чьими руками файл скачан: своим вызовом или агентом внутри него.
        var message = Answer();
        message.ToolRounds[0].Calls[0].NestedAgent = new AgentRunRecord
        {
            ModelId = "grok-4-6",
            Status = AgentRunStatus.Complete,
            ToolRounds =
            [
                new ToolRound
                {
                    Calls =
                    [
                        new ToolCallRecord
                        {
                            Id = "n1",
                            Name = "download_file",
                            Status = ToolCallStatus.Done,
                            Success = true,
                            SavedFiles = [new SavedFile(@"C:\Downloads\agent.msi", "agent.msi", 10)]
                        }
                    ]
                }
            ]
        };

        Assert.Contains("agent.msi", Render(message));
    }

    [Fact]
    public void The_same_path_saved_twice_gets_one_card()
    {
        // Перезапись того же файла вторым вызовом не должна удваивать карточку.
        var file = new SavedFile(@"C:\Downloads\report.pdf", "report.pdf", 10);
        var cards = ChatMessageViews.CollectSavedFiles(Answer(file, file));
        Assert.Single(cards);
    }

    [Fact]
    public void A_file_that_is_gone_says_so_instead_of_pretending_to_have_a_size()
    {
        var texts = Render(Answer(new SavedFile(@"C:\Downloads\нет-такого.zip", "нет-такого.zip", 4096)));

        Assert.Contains("нет-такого.zip", texts);
        Assert.Contains(texts, line =>
            line.Contains(Loc.Get("S.Tools.SavedFileMissing"), StringComparison.Ordinal));
    }

    /// <summary>Сообщение, где модель назвала путь к файлу куском кода.</summary>
    private static ChatDisplayMessage Mentioning(string text, params SavedFile[] files)
    {
        var message = Answer(files);
        message.Text = text;
        return message;
    }

    /// <summary>Пути файлов, чьи карточки встали прямо в текст ответа.</summary>
    private List<string> CardsInText(ChatDisplayMessage message) => _wpf.Ui.Invoke(() =>
    {
        var view = ChatMessageViews.CreateAssistant(Window(), message);
        var found = new List<string>();
        foreach (var block in view.Body.Document.Blocks)
        {
            if (block is BlockUIContainer { Child: Border { ToolTip: string tip } })
            {
                found.Add(tip.Split('\n')[0]);
            }
        }

        return found;
    });

    private bool StripIsShown(ChatDisplayMessage message) =>
        _wpf.Ui.Invoke(() =>
            ChatMessageViews.CreateAssistant(Window(), message).FilesHost.Visibility == Visibility.Visible);

    [Fact]
    public void A_path_the_model_named_becomes_a_card_on_that_very_line()
    {
        // Модель и так пишет путь, чтобы человек знал, куда смотреть; карточке место там же, а
        // не отдельной полосой в конце ответа.
        const string path = @"C:\Users\Who\Downloads\download";
        var message = Mentioning(
            "Готово: установщик скачан в «Загрузки»:\n\n`" + path + "`\n\nУстановка не выполнялась.",
            new SavedFile(path, "download", 107_622));

        Assert.Equal([path], CardsInText(message));

        // И второй раз, полосой под ответом, он уже не нужен.
        Assert.False(StripIsShown(message));
    }

    [Fact]
    public void A_path_written_as_a_code_block_works_the_same_way()
    {
        const string path = @"C:\Users\Who\Downloads\setup.exe";
        var message = Mentioning(
            "Скачал:\n\n```\n" + path + "\n```\n",
            new SavedFile(path, "setup.exe", 10));

        Assert.Equal([path], CardsInText(message));
    }

    [Fact]
    public void A_file_the_model_said_nothing_about_still_gets_its_strip()
    {
        var message = Mentioning(
            "Готово, всё скачал.",
            new SavedFile(@"C:\Downloads\quiet.zip", "quiet.zip", 10));

        Assert.Empty(CardsInText(message));
        Assert.True(StripIsShown(message));
    }

    [Fact]
    public void Only_the_file_that_was_named_leaves_the_strip()
    {
        const string named = @"C:\Downloads\named.zip";
        var message = Mentioning(
            "Первый лежит в `" + named + "`, второй рядом.",
            new SavedFile(named, "named.zip", 10),
            new SavedFile(@"C:\Downloads\other.zip", "other.zip", 10));

        Assert.Equal([named], CardsInText(message));
        Assert.True(StripIsShown(message));
    }

    [Fact]
    public void Ordinary_code_is_still_code()
    {
        // Подмена срабатывает только на полном пути к файлу этого ответа: иначе любая строчка
        // в обратных апострофах рисковала бы превратиться в карточку.
        var message = Mentioning(
            "Запусти `Get-Process` и посмотри на `DiscordSetup.exe`.",
            new SavedFile(@"C:\Downloads\download", "download", 10));

        Assert.Empty(CardsInText(message));
    }

    [Fact]
    public async Task A_download_reports_the_file_it_put_on_disk()
    {
        // Без этого поля в чате нечего показывать: путь оставался только внутри текста для модели.
        var folder = Path.Combine(Path.GetTempPath(), "amarin-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "заметка.txt");
            var tool = new FileSystemTool();
            var result = await tool.ExecuteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    action = "write",
                    path = target,
                    content = "привет"
                }),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.HasFiles);

            var saved = Assert.Single(result.GetFiles());
            Assert.Equal(target, saved.Path);
            Assert.Equal("заметка.txt", saved.FileName);
            Assert.True(saved.SizeBytes > 0);

            // Текст для модели остаётся прежним: она читает его как раньше.
            Assert.Contains(target, result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
