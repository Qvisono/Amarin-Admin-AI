using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Окно аварии показывается в момент, когда доверять нечему: тема может быть ещё не подставлена,
/// а главное окно — уже развалено. Проверяем, что оно всё-таки рисуется и что кнопка
/// «Продолжить» появляется только там, где продолжать действительно можно.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CrashWindowTests
{
    private readonly WpfFixture _wpf;

    public CrashWindowTests(WpfFixture wpf) => _wpf = wpf;

    private CrashWindow Build(bool canContinue) => _wpf.Ui.Invoke(() =>
    {
        var window = new CrashWindow(
            "Что-то пошло не так",
            "Не удалось прочитать или записать файл.\n\nchats/index.json занят",
            "System.IO.IOException: chats/index.json занят",
            logPath: @"C:\Temp\crash.log",
            canContinue: canContinue);

        // Показывать окно на экране незачем — меряем по отрисовке в картинку. Раскладку
        // считаем по содержимому, а не по самому Window: непоказанное окно детей не меряет,
        // и ActualWidth карточки остался бы нулём.
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(900, 900));
        root.Arrange(new Rect(0, 0, 900, 900));
        root.UpdateLayout();
        return window;
    });

    [Fact]
    public void Fallback_palette_carries_the_panel_brush()
    {
        // Без неё DynamicResource вернёт null и карточка нарисуется прозрачной на прозрачном —
        // то есть окно аварии «не появится».
        var palette = _wpf.Ui.Invoke(() => ThemeManager.LoadFallbackPalette());

        Assert.NotNull(palette["Bg.Panel"]);
        Assert.NotNull(palette["Border.Default"]);
        Assert.NotNull(palette["Status.Danger"]);
        Assert.NotNull(palette["Accent.Fill"]);
    }

    [Fact]
    public void The_card_is_actually_painted()
    {
        var window = Build(canContinue: true);
        try
        {
            var opaque = _wpf.Ui.Invoke(() =>
            {
                var card = (FrameworkElement)window.FindName("Card");
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(card.ActualWidth),
                    (int)Math.Ceiling(card.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(card);

                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var painted = 0;
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] > 0)
                    {
                        painted++;
                    }
                }

                return painted;
            });

            Assert.True(opaque > 1000, $"карточка почти прозрачна: закрашено {opaque} пикселей");
        }
        finally
        {
            _wpf.Ui.Invoke<object?>(() => { window.Close(); return null; });
        }
    }

    [Fact]
    public void A_fatal_crash_offers_no_way_to_continue()
    {
        var window = Build(canContinue: false);
        try
        {
            var visibility = _wpf.Ui.Invoke(
                () => ((FrameworkElement)window.FindName("ContinueButton")).Visibility);

            Assert.Equal(Visibility.Collapsed, visibility);
        }
        finally
        {
            _wpf.Ui.Invoke<object?>(() => { window.Close(); return null; });
        }
    }

    [Fact]
    public void Details_stay_folded_until_asked_for()
    {
        var window = Build(canContinue: true);
        try
        {
            var (before, after) = _wpf.Ui.Invoke(() =>
            {
                var box = (FrameworkElement)window.FindName("DetailsBox");
                var toggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("DetailsToggle");
                var folded = box.Visibility;
                toggle.IsChecked = true;
                return (folded, box.Visibility);
            });

            Assert.Equal(Visibility.Collapsed, before);
            Assert.Equal(Visibility.Visible, after);
        }
        finally
        {
            _wpf.Ui.Invoke<object?>(() => { window.Close(); return null; });
        }
    }
}
