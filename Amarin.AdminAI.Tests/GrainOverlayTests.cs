using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Слой матового зерна: тайл шума поверх всего окна.
/// </summary>
/// <remarks>
/// Проверять глазами тут нечего — три процента непрозрачности не увидишь на скриншоте в
/// отчёте, — зато ровно это и ломается молча: съехавший на дробный DPI тайл, случайно
/// перекошенный в светлое шум, слой, оставшийся в композиции при нулевом ползунке.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class GrainOverlayTests
{
    private readonly WpfFixture _wpf;

    public GrainOverlayTests(WpfFixture wpf) => _wpf = wpf;

    private const double Side = 96;

    /// <summary>
    /// Свежий слой на отдельном окне: главное трогать нельзя, оно общее для всех оконных
    /// тестов. Окно обязательно закрывается — <c>Application.Current.Windows</c> держит его
    /// до тех пор, и за длинный прогон они копились бы.
    /// </summary>
    private static (GrainOverlay Grain, Rectangle Layer, Window Host) Build()
    {
        var layer = new Rectangle { Width = Side, Height = Side };
        var host = new Window();
        return (new GrainOverlay(host, layer), layer, host);
    }

    private static byte[] Paint(FrameworkElement element)
    {
        element.Measure(new Size(Side, Side));
        element.Arrange(new Rect(0, 0, Side, Side));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)Side, (int)Side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    [Fact]
    public void The_noise_is_a_128_pixel_tile_that_shifts_nothing_on_average()
    {
        var (mean, width, height) = _wpf.Ui.Invoke(() =>
        {
            var noise = GrainOverlay.Noise;
            var stride = noise.PixelWidth * 4;
            var pixels = new byte[stride * noise.PixelHeight];
            noise.CopyPixels(pixels, stride, 0);

            // Каждая точка — либо белая, либо чёрная, и весит своей альфой. Сумма со знаком
            // показывает, куда шум тянет картинку: если он не нейтрален, зерно не добавляет
            // микрорельеф, а перекрашивает весь интерфейс светлее или темнее.
            var total = 0L;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                total += pixels[i] == 255 ? alpha : -alpha;
            }

            return ((double)total / (pixels.Length / 4), noise.PixelWidth, noise.PixelHeight);
        });

        Assert.Equal(GrainOverlay.TileSize, width);
        Assert.Equal(GrainOverlay.TileSize, height);
        Assert.True(Math.Abs(mean) < 2.0, $"шум уводит картинку на {mean:0.00} из 255");
    }

    [Fact]
    public void The_tile_is_pinned_to_device_pixels_at_every_ui_scale()
    {
        // Масштаб интерфейса — подделанный DPI, он масштабирует и слой зерна. Без этого
        // деления тайл на 150 % растянулся бы в 192 пикселя и размазался в мыло.
        Assert.Equal(128.0, GrainOverlay.Viewport(1.0).Width, 3);
        Assert.Equal(128.0 / 1.5, GrainOverlay.Viewport(1.5).Width, 3);
        Assert.Equal(64.0, GrainOverlay.Viewport(2.0).Width, 3);

        // Нулевой или невычисленный DPI не должен превращать окно кисти в бесконечность.
        Assert.Equal(128.0, GrainOverlay.Viewport(0).Width, 3);
        Assert.Equal(128.0, GrainOverlay.Viewport(double.NaN).Width, 3);
    }

    [Fact]
    public void Zero_grain_leaves_the_layer_out_of_the_composition()
    {
        var (visibility, fill) = _wpf.Ui.Invoke(() =>
        {
            var (grain, layer, host) = Build();
            grain.Apply(new AppearanceSettings { Grain = 0 });
            var result = (layer.Visibility, layer.Fill);
            grain.Dispose();
            host.Close();
            return result;
        });

        // Именно Collapsed, а не нулевая непрозрачность: полноэкранная тайловая кисть
        // попадает в композицию каждого кадра, даже когда её не видно.
        Assert.Equal(Visibility.Collapsed, visibility);
        Assert.Null(fill);
    }

    [Fact]
    public void Grain_follows_the_theme_until_the_slider_is_touched()
    {
        var (matte, matteLight, dark, overridden) = _wpf.Ui.Invoke(() =>
        {
            var previous = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Apply(AppTheme.Matte);
                var onMatte = GrainOverlay.Effective(new AppearanceSettings());

                // Осознанный ноль на матовой теме обязан перебивать её пожелание — иначе
                // выключить зерно было бы невозможно.
                var silenced = GrainOverlay.Effective(new AppearanceSettings { Grain = 0 });

                ThemeManager.Apply(AppTheme.MatteLight);
                var onMatteLight = GrainOverlay.Effective(new AppearanceSettings());

                ThemeManager.Apply(AppTheme.Dark);
                var onDark = GrainOverlay.Effective(new AppearanceSettings());

                return (onMatte, onMatteLight, onDark, silenced);
            }
            finally
            {
                ThemeManager.Apply(previous);
            }
        });

        Assert.True(matte > 0, "у Matte зерно не запрошено");
        Assert.True(matteLight > 0, "у Matte Light зерно не запрошено");
        Assert.Equal(0, dark);
        Assert.Equal(0, overridden);
    }

    [Fact]
    public void Only_the_two_matte_presets_ask_for_grain()
    {
        // Тридцать одна тема уехала к людям до того, как слой зерна появился, и обязана
        // выглядеть ровно так же, как выглядела.
        var asking = ThemeCatalog.Presets.Where(p => p.Grain > 0).Select(p => p.Theme).ToArray();
        Assert.Equal([AppTheme.Matte, AppTheme.MatteLight], asking);
    }

    [Fact]
    public void Grain_textures_the_surface_without_moving_its_tone()
    {
        if (!GrainOverlay.Allowed())
        {
            // Программный рендеринг: слой намеренно не рисуется, проверять нечего.
            return;
        }

        var (difference, toneShift) = _wpf.Ui.Invoke(() =>
        {
            var (grain, layer, window) = Build();
            var host = new Grid
            {
                Width = Side,
                Height = Side,
                Background = new SolidColorBrush(Color.FromRgb(0x21, 0x23, 0x26))
            };
            host.Children.Add(layer);

            grain.Apply(new AppearanceSettings { Grain = 0 });
            var plain = Paint(host);

            grain.Apply(new AppearanceSettings { Grain = 1 });
            var textured = Paint(host);
            grain.Dispose();
            window.Close();

            var total = 0L;
            var signed = 0L;
            for (var i = 0; i < plain.Length; i++)
            {
                total += Math.Abs(plain[i] - textured[i]);
                signed += textured[i] - plain[i];
            }

            return ((double)total / plain.Length, (double)signed / plain.Length);
        });

        // Зерно обязано быть: слой на максимуме и всё равно ничего не меняющий — это
        // молча сломанная кисть, а не тонкая настройка.
        Assert.True(difference > 0.2, $"зерна не видно вовсе: {difference:0.000}");

        // И обязано оставаться зерном, а не тонировкой. Ноля тут не будет: тайл нейтрален,
        // но на тёмной поверхности белая точка поднимает её сильнее, чем чёрная опускает, —
        // на этом фоне перекос около единицы из 255, то есть ниже различимого шага. Порог
        // ловит именно уход в тонировку, когда ползунок начал бы осветлять интерфейс.
        Assert.True(Math.Abs(toneShift) < 2.0, $"зерно уводит тон на {toneShift:0.00} из 255");
    }
}
