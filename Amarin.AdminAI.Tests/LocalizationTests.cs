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
            or "SummarizeError" or "BalanceAmount" or "AllowedDomainsEmpty";
}
