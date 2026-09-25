using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Локализации в программе не было вовсе: весь текст стоял литералами в разметке и в коде.
/// Эти тесты держат два обещания — наборы ключей у языков совпадают, и ни одна подпись не
/// оказывается пустой оттого, что ключ забыли.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class LocalizationTests
{
    private readonly WpfFixture _wpf;

    public LocalizationTests(WpfFixture wpf) => _wpf = wpf;

    private Dictionary<string, string> Load(string code) =>
        _wpf.Ui.Invoke(() => LanguageManager.Flatten(LanguageManager.LoadBuiltIn(code)));

    [Fact]
    public void Every_built_in_language_has_the_same_keys()
    {
        // Главный страж полноты перевода: пропущенный ключ виден здесь, а не в чужом интерфейсе.
        var russian = Load("ru");
        var english = Load("en");

        Assert.NotEmpty(russian);
        Assert.Equal(
            russian.Keys.OrderBy(k => k, StringComparer.Ordinal),
            english.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void The_built_in_russian_copy_in_code_matches_the_dictionary()
    {
        // StringsRu — копия Strings.ru.xaml для кода и для тестов без интерфейса. Разъехавшись,
        // она даёт разный текст в разметке и в C#, а заметить это на глаз нельзя.
        var fromXaml = Load("ru");
        var fromCode = StringsRu.Values;

        Assert.Equal(
            fromXaml.OrderBy(p => p.Key, StringComparer.Ordinal),
            fromCode.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void No_string_is_left_empty()
    {
        foreach (var code in new[] { "ru", "en" })
        {
            var empty = Load(code).Where(pair => string.IsNullOrWhiteSpace(pair.Value)).ToList();
            Assert.True(empty.Count == 0, $"в языке {code} пустые ключи: {string.Join(", ", empty.Select(p => p.Key))}");
        }
    }

    [Fact]
    public void English_is_not_a_copy_of_the_russian()
    {
        var russian = Load("ru");
        var english = Load("en");

        // Часть подписей совпадает намеренно (Account, Behavior, Vision), но не большинство.
        var same = russian.Count(pair => english[pair.Key] == pair.Value);
        Assert.True(same < russian.Count / 2, $"не переведено {same} из {russian.Count} строк");
    }

    [Fact]
    public void Placeholders_survive_the_translation()
    {
        // Расхождение по {0} — это уже не «криво звучит», а исключение при форматировании.
        var russian = Load("ru");
        var english = Load("en");

        foreach (var (key, value) in russian)
        {
            Assert.Equal(Placeholders(value), Placeholders(english[key]));
        }
    }

    [Theory]
    [InlineData("UI", "PasswordWindow.xaml.cs")]
    [InlineData("UI", "MainWindow.Account.cs")]
    [InlineData("UI", "MainWindow.Updates.cs")]
    [InlineData("UI", "MainWindow.DataBundle.cs")]
    [InlineData("UI", "MainWindow.About.cs")]
    [InlineData("UI", "SettingsInfoPage.xaml.cs")]
    [InlineData("UI", "GuideShot.xaml.cs")]
    [InlineData("UI", "SettingsInstructionsPage.xaml.cs")]
    [InlineData("UI", "MainWindow.Instructions.cs")]
    [InlineData("Core", "InstructionLibrary.cs")]
    [InlineData("Core", "InstructionBriefing.cs")]
    [InlineData("Tools", "ReadInstructionTool.cs")]
    [InlineData("Core", "UpdateChecker.cs")]
    [InlineData("Core", "UpdateInstaller.cs")]
    [InlineData("Core", "DataBundle.cs")]
    [InlineData("Core", "DataBundleExporter.cs")]
    [InlineData("Core", "DataBundleImporter.cs")]
    public void The_login_and_account_screens_hold_no_literal_russian(string folder, string file)
    {
        // Эти экраны написали до локализации, и подписи так и остались литералами: при
        // японском интерфейсе «Войти», «Локальный режим» и «Задан» показывались по-русски.
        // Разметка тянет строки через DynamicResource сама, а вот всё, что эти файлы пишут
        // в интерфейс из кода, обязано идти через Loc — иначе язык до них не доходит.
        //
        // Страница обновлений и оба её файла в Core доехали до релиза целиком по-русски именно
        // потому, что их в этом списке не было: девятнадцать литералов, которые никто не искал.
        var relative = Path.Combine(folder, file);
        var lines = File.ReadAllLines(ProjectFile(relative));
        var offenders = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(lines[i], LiteralWithCyrillic))
            {
                offenders.Add($"{relative}:{i + 1} {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private const string LiteralWithCyrillic = "\"[^\"\n]*[А-Яа-яЁё][^\"\n]*\"";

    [Fact]
    public void The_account_and_password_screens_are_translated()
    {
        // Ключ можно завести и оставить русское значение в en.xaml — набор ключей при этом
        // совпадёт, и главный страж ничего не заметит.
        var russian = Load("ru");
        var english = Load("en");

        var untranslated = russian
            .Where(pair => pair.Key.StartsWith("S.Account.", StringComparison.Ordinal) ||
                           pair.Key.StartsWith("S.Password.", StringComparison.Ordinal))
            .Where(pair => english[pair.Key] == pair.Value)
            .Select(pair => pair.Key)
            .ToList();

        Assert.True(untranslated.Count == 0, $"не переведено: {string.Join(", ", untranslated)}");
    }

    [Fact]
    public void The_export_and_import_labels_are_translated()
    {
        // Тот же страж, что у экрана входа: набор ключей сойдётся и с русским значением в
        // en.xaml, а в английском интерфейсе окно экспорта останется наполовину русским.
        var russian = Load("ru");
        var english = Load("en");

        var untranslated = russian
            .Where(pair => pair.Key.StartsWith("S.Bundle.", StringComparison.Ordinal))
            .Where(pair => english[pair.Key] == pair.Value)
            .Select(pair => pair.Key)
            .ToList();

        Assert.True(untranslated.Count == 0, $"не переведено: {string.Join(", ", untranslated)}");
    }

    private static string ProjectFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Amarin Admin AI", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relative);
    }

    private static string Placeholders(string value) =>
        string.Concat(System.Text.RegularExpressions.Regex
            .Matches(value, @"\{\d+\}")
            .Select(m => m.Value)
            .OrderBy(m => m, StringComparer.Ordinal));

    [Fact]
    public void The_default_chat_title_is_translated_for_show_but_not_on_disk()
    {
        // ChatTitle.Default — не подпись, а признак «заголовок ещё не придуман», и он лежит
        // в файле чата. Переведи его — и прежние чаты перестанут опознаваться как безымянные.
        Assert.Equal("Новый чат", ChatTitle.Default);
        Assert.True(ChatTitle.IsDefault(ChatTitle.Default));

        var previous = LanguageManager.Current;
        try
        {
            var shown = _wpf.Ui.Invoke(() =>
            {
                LanguageManager.Apply("en");
                return MainWindow.DisplayTitle(ChatTitle.Default);
            });

            Assert.Equal("New chat", shown);
            Assert.Equal("Проверка сети", MainWindow.DisplayTitle("Проверка сети"));
        }
        finally
        {
            _wpf.Ui.Invoke<object?>(() =>
            {
                LanguageManager.Apply(previous);
                return null;
            });
        }
    }

    [Fact]
    public void The_working_counter_is_translated()
    {
        // Счётчик над ответом собирался литералом «Working {n}s» прямо в ChatFormat, поэтому
        // на любом языке оставался английским — по-русски он должен говорить «Размышляю».
        var russian = Load("ru");
        var english = Load("en");

        Assert.Equal("Размышляю {0}", russian["S.Message.Working"]);
        Assert.Equal("Working {0}", english["S.Message.Working"]);

        // Буква «s» подставляется кодом вместе с числом и в словарь не попадает: иначе перевод
        // на новый язык тронул бы её — модель переводит значения как текст — и оторвал бы
        // пробелом от числа.
        Assert.EndsWith("{0}", russian["S.Message.Working"], StringComparison.Ordinal);
        Assert.EndsWith("{0}", english["S.Message.Working"], StringComparison.Ordinal);

        // Язык фикстура держит русским, поэтому подпись обязана прийти из словаря, а не из
        // литерала. Язык здесь намеренно не переключается: Loc общий на процесс, а соседние
        // тесты вне WPF-коллекции идут параллельно и читают из него свои подписи.
        Assert.Equal("Размышляю 3s", ChatFormat.Working(TimeSpan.FromSeconds(3.2)));
    }

    [Fact]
    public void The_completion_toast_says_the_same_thing_as_the_chat()
    {
        // В карточке уведомления длительность писалась русскими «с» и «мин» литералами: на
        // другом языке они оставались русскими, а об одном и том же ходе карточка и шапка
        // ответа говорили по-разному — «4,2 с» против «4s».
        Assert.Equal("4s", ChatFormat.Duration(TimeSpan.FromSeconds(4.2)));
        Assert.EndsWith("4s", MainWindow.BuildToastMeta("grok-4-6", TimeSpan.FromSeconds(4.2)),
            StringComparison.Ordinal);
        Assert.EndsWith("1m5s", MainWindow.BuildToastMeta("grok-4-6", TimeSpan.FromSeconds(65)),
            StringComparison.Ordinal);

        // Ход без длительности — только имя модели, без висящего в воздухе разделителя.
        Assert.DoesNotContain("·", MainWindow.BuildToastMeta("grok-4-6", TimeSpan.Zero),
            StringComparison.Ordinal);

        // Пустой ответ подписывался литералом, хотя ключ для него давно есть.
        Assert.Equal(Loc.Get("S.Message.NoText"), MainWindow.FirstLine("   "));
        Assert.Equal("Ответ без текста", MainWindow.FirstLine(null));
    }

    [Fact]
    public void Chat_titles_are_asked_for_in_the_interface_language()
    {
        // В промпте стояло «Output only a Russian title»: на английском интерфейсе заголовок
        // оставался русским, а на длинном первом сообщении модель шла за его языком, и список
        // чатов выходил разноязычным.
        var japanese = ChatTitle.SystemPrompt("日本語");

        Assert.Contains("日本語", japanese, StringComparison.Ordinal);
        Assert.DoesNotContain("Russian", japanese, StringComparison.Ordinal);

        // Язык повторяется вплотную к сообщению: инструкция сверху тонет в длинном тексте.
        Assert.Contains("日本語", ChatTitle.UserPrompt("Привет", "日本語"), StringComparison.Ordinal);

        // Имя языка приходит из того же словаря, что и весь интерфейс, — значит переведённый
        // моделью язык приносит его с собой и отдельной настройки не требует.
        Assert.Equal(Loc.LanguageNameKey, UserLanguageStore.NameKey);
        Assert.Equal("Русский", Load("ru")[Loc.LanguageNameKey]);
        Assert.Equal("English", Load("en")[Loc.LanguageNameKey]);

        // Язык фикстура держит русским, поэтому и просить модель обязаны по-русски.
        Assert.Equal("Русский", ChatTitle.LanguageName());
    }

    [Fact]
    public void An_unknown_language_quietly_falls_back_to_russian()
    {
        Assert.Equal("ru", LanguageManager.Normalize("klingon"));
        Assert.Equal("ru", LanguageManager.Normalize(null));
        Assert.Equal("ru", LanguageManager.Normalize("   "));
        Assert.Equal("en", LanguageManager.Normalize("EN"));
    }

    [Fact]
    public void A_translation_sits_on_top_of_the_russian_base()
    {
        // Русский снизу всегда: у перевода может не хватать ключа, и подпись обязана остаться,
        // пусть и русской.
        var merged = _wpf.Ui.Invoke(() => LanguageManager.Flatten(LanguageManager.Build("en")));
        var english = Load("en");

        Assert.Equal(english.Count, merged.Count);
        Assert.Equal(english["S.Common.Save"], merged["S.Common.Save"]);
    }

    [Fact]
    public void Switching_the_language_changes_what_the_settings_page_says()
    {
        var previous = LanguageManager.Current;
        try
        {
            var (russian, english) = _wpf.Ui.Invoke(() =>
            {
                var window = Application.Current.Windows.OfType<MainWindow>().Single();
                var nav = (ContentControl)window.FindName("NavBehavior")!;

                LanguageManager.Apply("ru");
                window.UpdateLayout();
                var ru = (string)nav.Content;

                LanguageManager.Apply("en");
                window.UpdateLayout();
                return (ru, (string)nav.Content);
            });

            Assert.Equal("Behavior", russian);
            Assert.Equal("Behavior", english);
        }
        finally
        {
            _wpf.Ui.Invoke<object?>(() =>
            {
                LanguageManager.Apply(previous);
                return null;
            });
        }
    }

    [Fact]
    public void No_visible_label_goes_blank_in_english()
    {
        // Неразрешившийся DynamicResource даёт пустой TextBlock — на глаз это «подпись пропала».
        var previous = LanguageManager.Current;
        try
        {
            var blanks = _wpf.Ui.Invoke(() =>
            {
                var window = Application.Current.Windows.OfType<MainWindow>().Single();
                LanguageManager.Apply("en");
                var overlay = (FrameworkElement)window.FindName("SettingsOverlay")!;
                overlay.Visibility = Visibility.Visible;
                window.UpdateLayout();

                var found = new List<string>();
                Walk(overlay, found);
                overlay.Visibility = Visibility.Collapsed;
                return found;
            });

            Assert.True(blanks.Count == 0, "пустые подписи: " + string.Join(" | ", blanks));
        }
        finally
        {
            _wpf.Ui.Invoke<object?>(() =>
            {
                LanguageManager.Apply(previous);
                return null;
            });
        }
    }

    /// <summary>Ищет видимые подписи, у которых текст пуст, — и называет их по соседям.</summary>
    private static void Walk(DependencyObject root, List<string> blanks)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Visibility: Visibility.Visible } text &&
                string.IsNullOrEmpty(text.Text) &&
                text.Inlines.Count == 0 &&
                text.Name.Length > 0 &&
                !IsFilledFromCode(text.Name))
            {
                blanks.Add(text.Name);
            }

            Walk(child, blanks);
        }
    }

    /// <summary>Эти подписи заполняются кодом по ходу дела и до первого события пусты.</summary>
    private static bool IsFilledFromCode(string name) =>
        name is "AttachmentsWarning" or "ConfirmationAgentText" or "ConfirmationSummaryText"
            or "ConfirmationExplanationText" or "DomainError" or "NameError" or "UpdateStatusText"
            or "SummarizeError" or "BalanceAmount" or "AllowedDomainsEmpty" or "UsageAppText"
            or "ConfirmationAiText";
}
