using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Подготовка картинки фона: попадание в кэш и готовый снимок на диске.
/// </summary>
/// <remarks>
/// Ради того и другого фон появляется вместе с окном, а не через полсекунды после него.
/// Кэш статический, поэтому класс живёт в коллекции WPF — так же, как остальные тесты,
/// трогающие общее состояние, — и каждый тест работает со своим файлом во временной папке.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class AppearanceImageCacheTests
{
    [Fact]
    public async Task A_slider_value_still_finds_its_picture_in_the_cache()
    {
        // Peek строил ключ из сырых чисел, а Load клал запись под округлёнными, и для любого
        // значения с ползунка («1.1506024096386072») совпадения не случалось никогда: каждое
        // применение оформления заново мигало градиентом вместо фотографии.
        var root = NewTempRoot();
        try
        {
            AppearanceImageCache.Clear();
            var path = WritePicture(root, "background.png", 40, 24);

            const double saturation = 1.1506024096386072;
            const double blur = 6.1176470588235805;

            Assert.Null(AppearanceImageCache.Peek(path, saturation, blur));
            Assert.NotNull(await AppearanceImageCache.LoadAsync(path, saturation, blur));
            Assert.NotNull(AppearanceImageCache.Peek(path, saturation, blur));
        }
        finally
        {
            AppearanceImageCache.Clear();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task The_processed_picture_is_kept_on_disk_for_the_next_launch()
    {
        // Кэш в памяти не переживает выход из программы, а обои бывают на десятки
        // мегапикселей. Без снимка рядом каждый запуск распаковывал бы их заново.
        var root = NewTempRoot();
        try
        {
            AppearanceImageCache.Clear();
            var path = WritePicture(root, "background.png", 64, 48);

            var first = await AppearanceImageCache.LoadAsync(path, 1.2, 8, root);
            Assert.NotNull(first);

            var derived = Directory.GetFiles(root, "background*.cache.png");
            Assert.Single(derived);

            // Новый процесс — пустая память: картинка обязана прийти с диска.
            AppearanceImageCache.Clear();
            Assert.NotNull(await AppearanceImageCache.LoadAsync(path, 1.2, 8, root));
        }
        finally
        {
            AppearanceImageCache.Clear();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Moving_a_slider_does_not_leave_the_profile_full_of_snapshots()
    {
        var root = NewTempRoot();
        try
        {
            AppearanceImageCache.Clear();
            var path = WritePicture(root, "background.png", 64, 48);

            foreach (var blur in new[] { 4.0, 8.0, 12.0 })
            {
                await AppearanceImageCache.LoadAsync(path, 1.2, blur, root);
            }

            Assert.Single(Directory.GetFiles(root, "background*.cache.png"));
        }
        finally
        {
            AppearanceImageCache.Clear();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task A_repainted_wallpaper_is_not_served_from_the_old_snapshot()
    {
        // Отпечаток берёт и время правки, и длину файла: иначе подменённая картинка
        // показывалась бы прежней до самой смены настроек.
        var root = NewTempRoot();
        try
        {
            AppearanceImageCache.Clear();
            var path = WritePicture(root, "background.png", 64, 48, Colors.Red);
            await AppearanceImageCache.LoadAsync(path, 1.0, 6, root);

            AppearanceImageCache.Clear();
            WritePicture(root, "background.png", 80, 60, Colors.Blue);
            var again = await AppearanceImageCache.LoadAsync(path, 1.0, 6, root);

            Assert.NotNull(again);
            Assert.Single(Directory.GetFiles(root, "background*.cache.png"));
        }
        finally
        {
            AppearanceImageCache.Clear();
            Cleanup(root);
        }
    }

    [Fact]
    public void The_snapshot_never_travels_in_the_data_bundle()
    {
        // Имя начинается с «background.», и без отдельного правила классификатор отправил бы
        // производный файл в «Оформление» — то есть в архив, который человек пересылает.
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf("background.0123456789abcdef.cache.png"));
        Assert.EndsWith(".cache.png", AppearanceImageCache.DerivedSuffix, StringComparison.Ordinal);

        // Сама картинка ехать по-прежнему обязана.
        Assert.Equal(DataCategory.Appearance, DataBundle.CategoryOf("background.jpg"));
    }

    [Fact]
    public void A_bare_file_name_is_resolved_against_the_profile_folder()
    {
        // Разрешение пути общее у прогрева и у самого фона: разойдись они — прогрев считал бы
        // картинку под одним ключом, а окно спрашивало бы её под другим.
        var root = Path.Combine(Path.GetTempPath(), "amarin-profile");

        Assert.Equal(Path.Combine(root, "background.jpg"), AppearanceImageCache.ResolvePath("background.jpg", root));
        Assert.Equal(@"C:\pics\wall.png", AppearanceImageCache.ResolvePath(@"C:\pics\wall.png", root));
        Assert.Equal("", AppearanceImageCache.ResolvePath("  ", root));
        Assert.Equal("", AppearanceImageCache.ResolvePath(null, root));
    }

    private static string WritePicture(string root, string name, int width, int height, Color? fill = null)
    {
        var path = Path.Combine(root, name);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var color = fill ?? Colors.Green;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-backdrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Временная папка — не повод ронять тест.
        }
    }
}
