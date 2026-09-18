using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Заготовки основного промпта: свой файл в папке профиля, как у остатка на счету.
/// </summary>
/// <remarks>
/// Файл человек не открывает руками, но переживает он ровно то же, что и остальные: его могут
/// оборвать на середине записи, снести или подсунуть чужой. Ни один из этих случаев не повод
/// показывать окно аварии посреди настроек — библиотека просто оказывается пустой.
/// </remarks>
public sealed class PromptLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-prompts-" + Guid.NewGuid().ToString("N"));

    public PromptLibraryTests() => Directory.CreateDirectory(_root);

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

    private string File_ => Path.Combine(_root, "prompts.json");

    [Fact]
    public void A_saved_preset_reads_back()
    {
        var library = new PromptLibrary(_root);
        library.Save([new PromptPreset { Name = "Роза", Text = "Ты — Роза." }]);

        var preset = Assert.Single(new PromptLibrary(_root).Load());
        Assert.Equal("Роза", preset.Name);
        Assert.Equal("Ты — Роза.", preset.Text);
        Assert.NotEqual("", preset.Id);
    }

    [Fact]
    public void A_missing_file_is_an_empty_library() =>
        Assert.Empty(new PromptLibrary(_root).Load());

    [Fact]
    public void A_damaged_file_is_an_empty_library()
    {
        System.IO.File.WriteAllText(File_, "{ это не json");
        Assert.Empty(new PromptLibrary(_root).Load());
    }

    /// <summary>
    /// Плитка без подписи или без текста не нажимается осмысленно, а удалить её было бы нечем.
    /// </summary>
    [Fact]
    public void Entries_without_a_name_or_a_body_are_dropped()
    {
        System.IO.File.WriteAllText(
            File_,
            """
            [
              { "id": "a", "name": "Пусто", "text": "   " },
              { "id": "b", "name": "  ",    "text": "текст" },
              { "id": "c", "name": "Живой", "text": "текст" }
            ]
            """);

        Assert.Equal("Живой", Assert.Single(new PromptLibrary(_root).Load()).Name);
    }

    [Fact]
    public void An_entry_without_an_id_gets_one()
    {
        System.IO.File.WriteAllText(File_, """[{ "name": "Без имени файла", "text": "текст" }]""");
        Assert.NotEqual("", Assert.Single(new PromptLibrary(_root).Load()).Id);
    }

    /// <summary>Библиотека — вещь личная: у каждого профиля своя папка и свой файл.</summary>
    [Fact]
    public void Two_profiles_do_not_share_a_library()
    {
        var second = Path.Combine(_root, "profiles", "other");
        Directory.CreateDirectory(second);

        new PromptLibrary(_root).Save([new PromptPreset { Name = "Первый", Text = "т" }]);

        Assert.Empty(new PromptLibrary(second).Load());
        Assert.Single(new PromptLibrary(_root).Load());
    }

    /// <summary>
    /// Плитка фиксированной высоты: перевод строки внутри текста вытолкнул бы название
    /// соседней плитки за её край.
    /// </summary>
    [Fact]
    public void The_preview_is_one_line()
    {
        var preview = PromptLibrary.Preview("первая\r\nвторая\n\nтретья");

        Assert.Equal("первая вторая третья", preview);
        Assert.DoesNotContain('\n', preview);
    }

    [Fact]
    public void A_long_preview_is_cut_with_an_ellipsis()
    {
        var preview = PromptLibrary.Preview(new string('я', 400), 120);

        Assert.EndsWith("…", preview, StringComparison.Ordinal);
        Assert.Equal(121, preview.Length);
    }

    [Fact]
    public void An_empty_body_previews_as_nothing() => Assert.Equal("", PromptLibrary.Preview("  "));

    [Fact]
    public void A_name_is_trimmed_to_what_fits_on_a_tile()
    {
        Assert.Equal("Роза", PromptLibrary.TrimName("  Роза  "));
        Assert.Equal(PromptLibrary.NameLimit, PromptLibrary.TrimName(new string('я', 90)).Length);
    }
}

/// <summary>Плитки, модалка и подтверждение подмены — на живом окне.</summary>
[Collection(WpfCollection.Name)]
public sealed class PromptLibraryUiTests
{
    private readonly WpfFixture _wpf;

    public PromptLibraryUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private T Find<T>(string name) where T : class =>
        _wpf.Ui.Invoke(() => (T)Window().FindName(name));

    [Fact]
    public void The_library_starts_with_its_empty_note_and_no_tiles()
    {
        var (tiles, emptyShown) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var host = (WrapPanel)window.FindName("PromptLibraryHost");
            var empty = (FrameworkElement)window.FindName("PromptLibraryEmpty");
            return (host.Children.Count, empty.Visibility);
        });

        // Библиотека выходит пустой: заводских текстов в релизе нет.
        Assert.Equal(0, tiles);
        Assert.Equal(Visibility.Visible, emptyShown);
    }

    [Fact]
    public void Both_dialogs_start_hidden()
    {
        var (editor, confirm) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            return (((FrameworkElement)window.FindName("PromptPresetOverlay")).Visibility,
                    ((FrameworkElement)window.FindName("PromptApplyOverlay")).Visibility);
        });

        Assert.Equal(Visibility.Collapsed, editor);
        Assert.Equal(Visibility.Collapsed, confirm);
    }

    /// <summary>
    /// Подтверждение лежит выше модалки правки, а та — выше настроек. Иначе вопрос «удалить?»
    /// оказался бы под окном, из которого его задали.
    /// </summary>
    [Fact]
    public void The_dialogs_stack_in_the_right_order()
    {
        var (editor, confirm) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            return (Panel.GetZIndex((UIElement)window.FindName("PromptPresetOverlay")),
                    Panel.GetZIndex((UIElement)window.FindName("PromptApplyOverlay")));
        });

        Assert.True(confirm > editor, $"подтверждение {confirm} должно быть выше правки {editor}");
    }

    /// <summary>
    /// Поле текста в модалке — своя копия стиля: оригинал лежит в ресурсах сетки настроек,
    /// а оверлей ей сосед. Без копии окно вообще не собиралось бы.
    /// </summary>
    [Fact]
    public void The_dialog_body_is_a_multiline_box_with_its_own_ceiling()
    {
        var (accepts, max) = _wpf.Ui.Invoke(() =>
        {
            var box = (TextBox)Window().FindName("PromptPresetText");
            return (box.AcceptsReturn, box.MaxHeight);
        });

        Assert.True(accepts);
        Assert.True(max is > 0 and < 400);
    }

    [Fact]
    public void The_create_button_carries_its_caption() =>
        Assert.Equal(
            Loc.Get("S.Customize.PromptCreate"),
            _wpf.Ui.Invoke(() => Find<Button>("PromptCreateButton").Content));
}
