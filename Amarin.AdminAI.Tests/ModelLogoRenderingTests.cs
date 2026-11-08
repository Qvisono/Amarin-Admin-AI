using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Проверяет настоящий рисунок ресурса: существующий ключ сам по себе не спасает от пустого
/// SVG, неподходящих чернил или огромного поля вокруг крошечного логотипа.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ModelLogoRenderingTests
{
    private readonly WpfFixture _wpf;

    public ModelLogoRenderingTests(WpfFixture wpf) => _wpf = wpf;

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Every_declared_mapping_key_has_an_image_in_each_theme(string theme)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(SourceTree.CoreDirectory, "Providers", "ModelLogoCatalog.json")));
        var root = document.RootElement;
        var keys = new HashSet<string>(StringComparer.Ordinal) { "Auto" };
        foreach (var section in new[] { "authors", "models" })
        {
            foreach (var property in root.GetProperty(section).EnumerateObject())
            {
                keys.Add(Assert.IsType<string>(property.Value.GetString()));
            }
        }

        foreach (var section in new[] { "families", "unqualified" })
        {
            foreach (var rule in root.GetProperty(section).EnumerateArray())
            {
                keys.Add(Assert.IsType<string>(rule.GetProperty("key").GetString()));
            }
        }

        Assert.NotEmpty(keys);
        _wpf.Ui.Invoke<object?>(() =>
        {
            var logos = Load($"AiLogos.{theme}.xaml");
            var missing = keys.Where(key => !logos.Contains(key) || logos[key] is not ImageSource)
                .Order(StringComparer.Ordinal).ToArray();
            Assert.True(missing.Length == 0, $"{theme}: нет ImageSource для ключей " + string.Join(", ", missing));
            return null;
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Every_snapshot_model_resolves_to_an_actual_image(string theme)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SourceTree.RepositoryFile(
            Path.Combine("tools", "model-logos", "catalog.snapshot.json"))));
        var ids = document.RootElement.GetProperty("models").EnumerateArray()
            .Select(model => Assert.IsType<string>(model.GetProperty("id").GetString())).ToArray();
        Assert.NotEmpty(ids);
        _wpf.Ui.Invoke<object?>(() =>
        {
            var logos = Load($"AiLogos.{theme}.xaml");
            var failures = new List<string>();
            foreach (var id in ids)
            {
                // Ключ мог остаться только в JSON оснастки, но не попасть в вшитый справочник
                // или словарь WPF. Проверяется тот же путь разрешения, что у строки списка.
                var key = VeniceModelCatalog.GetLogoResourceKey("openrouter:" + id);
                if (key is null || !logos.Contains(key) || logos[key] is not ImageSource)
                {
                    failures.Add($"{id} -> {key ?? "нет ключа"}");
                }
            }

            Assert.True(failures.Count == 0, $"{theme}: модели без изображения:\n" + string.Join("\n", failures));
            return null;
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Mimo_letter_cutouts_show_the_panel_instead_of_a_solid_tile(string theme)
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var logos = Load($"AiLogos.{theme}.xaml");
            var palette = Load($"Palette.{theme}.xaml");
            var background = Assert.IsType<SolidColorBrush>(palette["Bg.Panel"]);
            var canvas = new Canvas { Width = 96, Height = 96, Background = background };
            AddImage(canvas, "MiMo", Assert.IsAssignableFrom<ImageSource>(logos["MiMo"]), 96, 0, 0);
            var bitmap = Render(canvas, 96);
            var pixels = Pixels(bitmap);

            // Точки внутри правой стойки буквы (385, 256) и тела плитки (256, 450)
            // в исходной сетке 512. В OpacityMask и белый, и чёрный были непрозрачны:
            // первая точка тогда показывала заливку вместо отверстия под букву.
            Assert.Equal(background.Color, PixelAt(pixels, bitmap.PixelWidth, 72, 48));
            Assert.NotEqual(background.Color, PixelAt(pixels, bitmap.PixelWidth, 48, 84));
            return null;
        });
    }

    [Theory]
    [InlineData("Dark", 15, 96)]
    [InlineData("Dark", 20, 96)]
    [InlineData("Dark", 15, 192)]
    [InlineData("Dark", 20, 192)]
    [InlineData("Light", 15, 96)]
    [InlineData("Light", 20, 96)]
    [InlineData("Light", 15, 192)]
    [InlineData("Light", 20, 192)]
    public void Every_logo_is_visible_at_message_and_picker_sizes(string theme, int size, int dpi)
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var logos = Load($"AiLogos.{theme}.xaml");
            var palette = Load($"Palette.{theme}.xaml");
            var background = Assert.IsType<SolidColorBrush>(palette["Bg.Panel"]);
            var failures = new List<string>();
            foreach (var key in Keys(logos))
            {
                if (logos[key] is not ImageSource source)
                {
                    failures.Add($"{key}: ресурс не является ImageSource");
                    continue;
                }

                if (!double.IsFinite(source.Width) || !double.IsFinite(source.Height) ||
                    source.Width <= 0 || source.Height <= 0)
                {
                    failures.Add($"{key}: пустые размеры изображения");
                    continue;
                }

                // Поле вокруг слота позволяет заметить выход рисунка за пределы значка.
                const int padding = 4;
                var canvas = new Canvas
                {
                    Width = size + padding * 2,
                    Height = size + padding * 2,
                    Background = background
                };
                AddImage(canvas, key, source, size, padding, padding);
                var bitmap = Render(canvas, dpi);
                var pixels = Pixels(bitmap);
                var scale = dpi / 96;
                var edge = padding * scale;
                var visible = 0;
                var outside = 0;
                var inkLeft = bitmap.PixelWidth;
                var inkTop = bitmap.PixelHeight;
                var inkRight = -1;
                var inkBottom = -1;
                for (var y = 0; y < bitmap.PixelHeight; y++)
                {
                    for (var x = 0; x < bitmap.PixelWidth; x++)
                    {
                        var offset = (y * bitmap.PixelWidth + x) * 4;
                        var difference = Math.Max(
                            Math.Abs(pixels[offset] - background.Color.B),
                            Math.Max(Math.Abs(pixels[offset + 1] - background.Color.G),
                                Math.Abs(pixels[offset + 2] - background.Color.R)));
                        if (difference < 16)
                        {
                            continue;
                        }

                        if (x < edge || x >= edge + size * scale ||
                            y < edge || y >= edge + size * scale)
                        {
                            outside++;
                        }
                        else
                        {
                            visible++;
                            inkLeft = Math.Min(inkLeft, x);
                            inkTop = Math.Min(inkTop, y);
                            inkRight = Math.Max(inkRight, x);
                            inkBottom = Math.Max(inkBottom, y);
                        }
                    }
                }

                if (visible < 4 * scale * scale)
                {
                    failures.Add($"{key}: рисунок почти не виден ({visible} пикселей)");
                }

                // Ошибочный размер SVG оставляет огромные поля вокруг читаемой геометрии.
                // Достаточно одной оси: низкие фирменные надписи сохраняют полную ширину.
                const double minimumRelativeInkExtent = 0.55;
                var inkExtent = visible == 0 ? 0 : Math.Max(inkRight - inkLeft + 1, inkBottom - inkTop + 1);
                if (inkExtent < size * scale * minimumRelativeInkExtent)
                {
                    failures.Add($"{key}: рисунок занимает лишь {inkExtent} пикселей из {size * scale} по большей оси");
                }

                if (outside != 0)
                {
                    failures.Add($"{key}: {outside} пикселей за пределами слота");
                }
            }

            Assert.NotEmpty(Keys(logos));
            Assert.True(failures.Count == 0, $"{theme}, {size} DIP, {dpi} DPI:\n" + string.Join("\n", failures));
            return null;
        });
    }

    /// <summary>
    /// Лист для визуальной проверки, включая растровые аватары малых авторов. Обычные тесты
    /// не создают файлы; для снимков задают AMARIN_MODEL_LOGO_SHOTS=&lt;папка&gt;.
    /// </summary>
    [Fact]
    public void Write_contact_sheets_when_requested()
    {
        var directory = Environment.GetEnvironmentVariable("AMARIN_MODEL_LOGO_SHOTS");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        _wpf.Ui.Invoke<object?>(() =>
        {
            foreach (var theme in new[] { "Dark", "Light" })
            {
                var logos = Load($"AiLogos.{theme}.xaml");
                var palette = Load($"Palette.{theme}.xaml");
                foreach (var dpi in new[] { 96, 192 })
                {
                    var sheet = ContactSheet(theme, dpi, logos, palette);
                    var bitmap = Render(sheet, dpi);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"model-logos-{theme}-{dpi}dpi.png"));
                    encoder.Save(stream);
                }
            }

            return null;
        });
    }

    private static Canvas ContactSheet(string theme, int dpi, ResourceDictionary logos, ResourceDictionary palette)
    {
        const int columns = 4;
        const int cellWidth = 250;
        const int cellHeight = 88;
        const int headerHeight = 46;
        var keys = Keys(logos);
        var foreground = Assert.IsAssignableFrom<Brush>(palette["Text.Primary"]);
        var muted = Assert.IsAssignableFrom<Brush>(palette["Text.Muted"]);
        var outline = Assert.IsAssignableFrom<Brush>(palette["Border.Subtle"]);
        var sheet = new Canvas
        {
            Width = columns * cellWidth,
            Height = headerHeight + ((keys.Length + columns - 1) / columns) * cellHeight,
            Background = Assert.IsAssignableFrom<Brush>(palette["Bg.Panel"])
        };
        AddLabel(sheet, $"Логотипы моделей: {theme}, {dpi} DPI, {keys.Length} значков", 14, foreground, 12, 12);
        for (var index = 0; index < keys.Length; index++)
        {
            var key = keys[index];
            var source = Assert.IsAssignableFrom<ImageSource>(logos[key]);
            var left = index % columns * cellWidth;
            var top = headerHeight + index / columns * cellHeight;
            var border = new Border
            {
                Width = cellWidth,
                Height = cellHeight,
                BorderBrush = outline,
                BorderThickness = new Thickness(0, 0, 1, 1)
            };
            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, top);
            sheet.Children.Add(border);
            AddLabel(sheet, key, 12, foreground, left + 12, top + 5);
            AddImage(sheet, key, source, 15, left + 12, top + 35);
            AddImage(sheet, key, source, 20, left + 56, top + 32);
            AddImage(sheet, key, source, 48, left + 112, top + 24);
            AddLabel(sheet, "15", 10, muted, left + 12, top + 63);
            AddLabel(sheet, "20", 10, muted, left + 56, top + 63);
            AddLabel(sheet, "48", 10, muted, left + 178, top + 43);
        }

        return sheet;
    }

    private static void AddImage(Canvas canvas, string key, ImageSource source, int size, double left, double top)
    {
        var image = new Image { Source = source, Width = size, Height = size };
        ModelBrand.ApplyLogoBox(image, key);
        Canvas.SetLeft(image, left);
        Canvas.SetTop(image, top);
        canvas.Children.Add(image);
    }

    private static void AddLabel(Canvas canvas, string text, double fontSize, Brush foreground, double left, double top)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = foreground,
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.InvariantCulture.IetfLanguageTag)
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        canvas.Children.Add(label);
    }

    private static RenderTargetBitmap Render(FrameworkElement element, int dpi)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
        var scale = dpi / 96.0;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.Width * scale), (int)Math.Ceiling(element.Height * scale),
            dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static Color PixelAt(byte[] pixels, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static string[] Keys(ResourceDictionary logos) =>
        logos.Keys.Cast<object>().Select(key => Assert.IsType<string>(key)).Order(StringComparer.Ordinal).ToArray();

    private static ResourceDictionary Load(string name) =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri("/Amarin Admin AI;component/UI/Theme/" + name, UriKind.Relative));
}
