using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Окна экспорта и импорта данных на живом WPF.
/// </summary>
/// <remarks>
/// Оба лежат в Viewbox с фиксированной шириной, а список категорий строится кодом — ровно те
/// места, где разметка ломается молча. Запускать программу ради этого не нужно: окно берётся из
/// общей фикстуры, а внешний вид галочки снимается в картинку.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class DataBundleUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;

    public DataBundleUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static T Named<T>(MainWindow window, string name) where T : class => (T)window.FindName(name)!;

    private static void Call(MainWindow window, string method) =>
        typeof(MainWindow).GetMethod(method, Hidden)!.Invoke(window, []);

    /// <summary>
    /// Показывает окно тем же способом, что и кнопка.
    /// </summary>
    /// <remarks>
    /// Не через обработчик нажатия: окно фикстуры поднимается без служб, и настоящий обработчик
    /// из него выходит сразу. Проверяется здесь ровно то, что ломается молча, — слой, «модальность»
    /// и возврат мыши чату.
    /// </remarks>
    private static void Show(MainWindow window, string overlayName) =>
        typeof(MainWindow).GetMethod("ShowOverlay", Hidden)!
            .Invoke(window, [Named<UIElement>(window, overlayName)]);

    /// <summary>Закрывает оба окна: фикстура одна на всю коллекцию, мусор достанется соседям.</summary>
    private void CloseOverlays() => _wpf.Ui.Invoke<object?>(() =>
    {
        var window = Window();
        Named<UIElement>(window, "DataExportOverlay").Visibility = Visibility.Collapsed;
        Named<UIElement>(window, "DataImportOverlay").Visibility = Visibility.Collapsed;
        Named<UIElement>(window, "Chat").IsHitTestVisible = true;
        return null;
    });

    // ───────────────────────── строка в настройках ─────────────────────────

    [Fact]
    public void The_data_controls_page_offers_export_and_import()
    {
        var labels = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            window.UpdateLayout();
            return (
                (string)Named<Button>(window, "DataExportButton").Content,
                (string)Named<Button>(window, "DataImportButton").Content);
        });

        // Пустая подпись здесь значит неразрешившийся DynamicResource — на глаз это «кнопка
        // без текста», и заметить такое в релизе труднее, чем кажется.
        Assert.False(string.IsNullOrWhiteSpace(labels.Item1));
        Assert.False(string.IsNullOrWhiteSpace(labels.Item2));
        Assert.NotEqual(labels.Item1, labels.Item2);
    }

    // ───────────────────────── окно экспорта ─────────────────────────

    [Fact]
    public void The_export_dialog_opens_above_the_question_about_a_dangerous_action()
    {
        try
        {
            var state = _wpf.Ui.Invoke(() =>
            {
                var window = Window();
                var overlay = Named<Grid>(window, "DataExportOverlay");
                Show(window, "DataExportOverlay");
                window.UpdateLayout();

                return (
                    overlay.Visibility,
                    Panel.GetZIndex(overlay),
                    Named<UIElement>(window, "Chat").IsHitTestVisible);
            });

            Assert.Equal(Visibility.Visible, state.Visibility);

            // Окно открыл сам человек: фоновый вопрос от модели (ZIndex 30) не должен лечь
            // поверх списка галочек.
            Assert.True(state.Item2 > 30, $"экспорт оказался на слое {state.Item2}");

            // «Модальность» здесь делается руками, и без этого чат под затемнением ловил бы мышь.
            Assert.False(state.IsHitTestVisible);
        }
        finally
        {
            CloseOverlays();
        }
    }

    [Fact]
    public void Escape_closes_the_export_dialog()
    {
        try
        {
            var (before, after, hitTest) = _wpf.Ui.Invoke(() =>
            {
                var window = Window();
                var overlay = Named<Grid>(window, "DataExportOverlay");
                Show(window, "DataExportOverlay");
                window.UpdateLayout();
                var opened = overlay.Visibility;

                overlay.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window) ?? throw new InvalidOperationException(),
                    0,
                    Key.Escape)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                });

                window.UpdateLayout();
                return (opened, overlay.Visibility, Named<UIElement>(window, "Chat").IsHitTestVisible);
            });

            Assert.Equal(Visibility.Visible, before);
            Assert.Equal(Visibility.Collapsed, after);

            // Закрытие обязано вернуть чату мышь — иначе окно остаётся «залипшим» навсегда.
            Assert.True(hitTest);
        }
        finally
        {
            CloseOverlays();
        }
    }

    [Fact]
    public void Unticking_everything_disables_saving()
    {
        try
        {
            var (enabled, summary) = _wpf.Ui.Invoke(() =>
            {
                var window = Window();
                Show(window, "DataExportOverlay");

                // Список категорий собирается после обхода диска, поэтому проверяем сам запрет,
                // подсунув строку руками: она ничем не отличается от построенной.
                var rows = Rows(window, "_exportRows");
                rows.Clear();
                rows.Add((DataCategory.Chats, new CheckBox { IsChecked = false }));

                Call(window, "UpdateExportSummary");
                window.UpdateLayout();

                return (
                    Named<Button>(window, "DataExportSaveButton").IsEnabled,
                    Named<TextBlock>(window, "ExportSummaryText").Text);
            });

            Assert.False(enabled);
            Assert.Equal(Loc.Get("S.Bundle.NothingChosen"), summary);
        }
        finally
        {
            _wpf.Ui.Invoke<object?>(() =>
            {
                Rows(Window(), "_exportRows").Clear();
                return null;
            });

            CloseOverlays();
        }
    }

    [Fact]
    public void A_long_category_list_does_not_stretch_the_dialog_out_of_shape()
    {
        try
        {
            var (export, import) = _wpf.Ui.Invoke(() =>
            {
                var window = Window();
                Show(window, "DataExportOverlay");
                Show(window, "DataImportOverlay");

                // Все пять категорий сразу — больше их не бывает.
                var list = Named<StackPanel>(window, "ExportCategoryList");
                var importList = Named<StackPanel>(window, "ImportCategoryList");
                list.Children.Clear();
                importList.Children.Clear();

                foreach (var category in DataBundle.All)
                {
                    list.Children.Add(BuildRow(category));
                    importList.Children.Add(BuildRow(category));
                }

                window.UpdateLayout();

                var exportCard = Named<Border>(window, "DataExportCard");
                var importCard = Named<Border>(window, "DataImportCard");
                exportCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                importCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                list.Children.Clear();
                importList.Children.Clear();
                return (exportCard.DesiredSize, importCard.DesiredSize);
            });

            // Карточка живёт в Viewbox со Stretch="Uniform": всё, что делает её шире или выше
            // положенного, ужимает весь диалог вместе с кнопками, пока он не станет нечитаем.
            Assert.True(export.Width <= 460, $"окно экспорта расширилось до {export.Width}");
            Assert.True(import.Width <= 460, $"окно импорта расширилось до {import.Width}");
            Assert.True(export.Height <= 560, $"окно экспорта выросло до {export.Height}");
            Assert.True(import.Height <= 720, $"окно импорта выросло до {import.Height}");
        }
        finally
        {
            CloseOverlays();
        }
    }

    /// <summary>Строка категории, собранная тем же кодом, что и в живом окне.</summary>
    private static CheckBox BuildRow(DataCategory category)
    {
        object?[] args =
        [
            category,
            Loc.Get(DataBundle.DescriptionKeyOf(category)),
            "38,4 МБ",
            Loc.Format("S.Data.Usage.Files", 43),
            null
        ];

        return (CheckBox)typeof(MainWindow)
            .GetMethod("BuildCategoryRow", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;
    }

    private static List<(DataCategory Category, CheckBox Box)> Rows(MainWindow window, string field) =>
        (List<(DataCategory, CheckBox)>)typeof(MainWindow)
            .GetField(field, Hidden)!
            .GetValue(window)!;

    // ───────────────────────── квадратная галочка ─────────────────────────

    [Fact]
    public void The_square_check_box_paints_its_tick_only_when_ticked()
    {
        var (off, on) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var box = new CheckBox { Content = "Чаты", Width = 160, Height = 32 };
            box.SetResourceReference(FrameworkElement.StyleProperty, "DialogCheckBox");

            // Контейнер нужен, чтобы у элемента появились размеры: несмонтированный CheckBox
            // рисуется в пустую картинку.
            var host = Named<Grid>(window, "DataExportOverlay");
            host.Children.Add(box);
            try
            {
                box.IsChecked = false;
                var without = Snapshot(box);
                box.IsChecked = true;
                var with = Snapshot(box);
                return (without, with);
            }
            finally
            {
                host.Children.Remove(box);
            }
        });

        // Забытый или неразрешившийся стиль даёт системный чекбокс, и отличить его от нашего
        // на глаз в тесте нельзя — а вот картинки двух состояний обязаны различаться.
        Assert.NotEqual(off, on);
    }

    /// <summary>Снимок элемента в PNG: так же проверялись формулы и углы окна.</summary>
    private static string Snapshot(FrameworkElement element)
    {
        element.Measure(new Size(200, 40));
        element.Arrange(new Rect(0, 0, 200, 40));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(200, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return Convert.ToBase64String(buffer.ToArray());
    }

    // ───────────────────────── окно импорта ─────────────────────────

    [Fact]
    public void Switching_the_import_mode_changes_what_the_settings_row_promises()
    {
        try
        {
            var (merging, replacing) = _wpf.Ui.Invoke(() =>
            {
                var window = Window();
                var hints = (Dictionary<DataCategory, TextBlock>)typeof(MainWindow)
                    .GetField("_importHints", Hidden)!
                    .GetValue(window)!;

                hints.Clear();
                hints[DataCategory.Settings] = new TextBlock();

                Named<RadioButton>(window, "ImportMergeChoice").IsChecked = true;
                Call(window, "UpdateImportHints");
                var inMerge = hints[DataCategory.Settings].Text;

                Named<RadioButton>(window, "ImportReplaceChoice").IsChecked = true;
                Call(window, "UpdateImportHints");
                var inReplace = hints[DataCategory.Settings].Text;

                hints.Clear();
                Named<RadioButton>(window, "ImportMergeChoice").IsChecked = true;
                return (inMerge, inReplace);
            });

            // В слиянии настройки активного профиля не меняются вовсе, и строка обязана сказать
            // это прямо: отмеченная галочка иначе выглядит как обещание, которого импорт не
            // выполнит.
            Assert.Equal(Loc.Get("S.Bundle.Import.SettingsKeptHint"), merging);
            Assert.Equal(Loc.Get("S.Bundle.Cat.SettingsDesc"), replacing);
        }
        finally
        {
            CloseOverlays();
        }
    }

    [Fact]
    public void The_import_dialog_says_what_went_wrong_instead_of_the_exception()
    {
        var foreign = Path.Combine(Path.GetTempPath(), "amarin-ui-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var stream = new FileStream(foreign, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open(), new UTF8Encoding(false)))
            {
                writer.Write("чужой архив");
            }

            var (visible, text, overlay) = _wpf.Ui.Invoke(() =>
            {
                var window = Window();
                var report = new DataBundleImporter(Path.GetTempPath()).Inspect(foreign);

                typeof(MainWindow).GetMethod("ShowImportError", Hidden)!
                    .Invoke(window, [DataBundle.ErrorKey(report.Error)]);

                Named<UIElement>(window, "DataImportOverlay").Visibility = Visibility.Visible;
                window.UpdateLayout();

                var error = Named<TextBlock>(window, "ImportErrorText");
                return (error.Visibility, error.Text, Named<UIElement>(window, "DataImportOverlay").Visibility);
            });

            Assert.Equal(Visibility.Visible, visible);

            // Человеку — понятная строка и что делать дальше, а не «Unhandled exception of type
            // System.IO.InvalidDataException».
            Assert.Equal(Loc.Get("S.Bundle.Error.NotOurArchive"), text);
            Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);

            // Окно при этом остаётся открытым: закрывать его значит терять выбранный файл.
            Assert.Equal(Visibility.Visible, overlay);
        }
        finally
        {
            CloseOverlays();
            File.Delete(foreign);
        }
    }
}
