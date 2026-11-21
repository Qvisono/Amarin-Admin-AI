using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.UI;
using Path = System.IO.Path;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class Beta7RenderingTests(WpfFixture wpf)
{
    [Fact]
    public async Task The_panorama_renders_and_the_camera_rotates_without_reloading()
    {
        var sample = Environment.GetEnvironmentVariable("AMARIN_BETA7_PANORAMA");
        BitmapSource? source = null;
        if (sample is not null)
        {
            Assert.True(PanoramaBackdrop.IsSupportedFile(sample));
            source = await AppearanceImageCache.LoadAsync(sample, 1, 0, panorama: true);
            Assert.NotNull(source);
            Assert.Equal(7680, source.PixelWidth);
            Assert.Equal(3840, source.PixelHeight);
        }

        wpf.Ui.Invoke(() =>
        {
            var view = new PanoramaBackdrop();
            view.Show(source ?? SampleTexture());
            view.SetView(0, 0, 75);
            var front = Render(view.View, 640, 320);
            var first = CenterPixel(front);
            view.SetView(180, 0, 75);
            var back = Render(view.View, 640, 320);
            var second = CenterPixel(back);

            // Растеризация 3D на разных адаптерах может округлить непрозрачность на один уровень.
            Assert.InRange(first[3], 254, 255);
            Assert.InRange(second[3], 254, 255);
            Assert.False(first.AsSpan().SequenceEqual(second));

            Save(front, "panorama-front");
            Save(back, "panorama-back");
            return true;
        });
    }

    [Theory]
    [InlineData(180, 85)]
    [InlineData(180, -85)]
    [InlineData(0, 0)]
    public void The_panorama_fills_the_viewport_at_wide_angles_and_across_the_seam(double yaw, double pitch)
    {
        wpf.Ui.Invoke(() =>
        {
            var view = new PanoramaBackdrop();
            view.Show(SampleTexture());
            view.SetView(yaw, pitch, 110);
            var bitmap = Render(view.View, 320, 180);
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            for (var y = 0; y < bitmap.PixelHeight; y++)
            {
                for (var x = 0; x < bitmap.PixelWidth; x++)
                {
                    Assert.True(pixels[y * stride + x * 4 + 3] >= 254, $"Просвет в кадре ({x}, {y})");
                }
            }

            return true;
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_cold_background_loads_when_startup_has_no_synchronization_context(bool panorama)
    {
        var directory = Directory.CreateTempSubdirectory("amarin-backdrop-startup-");
        try
        {
            wpf.Ui.Invoke(() =>
            {
                const string fileName = "backdrop.png";
                var path = Path.Combine(directory.FullName, fileName);
                WriteSampleTexture(path);

                var settings = new AppearanceSettings
                {
                    Enabled = true,
                    BackdropMode = BackdropMode.Image,
                    UsePanorama = panorama,
                    PanoramaPath = fileName,
                    BackgroundImagePath = fileName,
                    AnimationsEnabled = false
                };
                Assert.Null(AppearanceImageCache.Peek(path, settings.ImageSaturation, settings.ImageBlur, panorama));

                var dictionaries = Application.Current.Resources.MergedDictionaries;
                var previousOverrides = dictionaries[ThemeManager.OverrideSlot];
                var host = new Grid();
                var window = new Window { Content = host };
                try
                {
                    using var appearance = new AppearanceManager(window, host) { DataRoot = directory.FullName };
                    var context = SynchronizationContext.Current;
                    try
                    {
                        // Приложение применяет оформление до запуска цикла диспетчера WPF.
                        SynchronizationContext.SetSynchronizationContext(null);
                        appearance.Apply(settings);
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(context);
                    }

                    var frame = new DispatcherFrame();
                    var wait = Stopwatch.StartNew();
                    var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
                    timer.Tick += (_, _) =>
                    {
                        if (BackgroundLoaded() || wait.Elapsed >= TimeSpan.FromSeconds(5))
                        {
                            frame.Continue = false;
                        }
                    };
                    timer.Start();
                    try
                    {
                        Dispatcher.PushFrame(frame);
                    }
                    finally
                    {
                        timer.Stop();
                    }

                    Assert.True(BackgroundLoaded());
                    if (panorama)
                    {
                        var viewport = Assert.Single(host.Children.OfType<Viewport3D>());
                        Assert.Equal(Visibility.Collapsed, viewport.Visibility);
                        Assert.Null(PanoramaSurface(viewport).Material);
                    }
                }
                finally
                {
                    window.Close();
                    dictionaries[ThemeManager.OverrideSlot] = previousOverrides;
                }

                return true;

                bool BackgroundLoaded() => panorama
                    ? host.Children.OfType<Viewport3D>().Any()
                    : host.Children.OfType<Rectangle>().Any(layer => layer is { Visibility: Visibility.Visible, Fill: ImageBrush });
            });
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // При сбое запуска фоновый декодер ещё может держать временный файл.
            }
        }
    }

    [Fact]
    public async Task Minimizing_and_hiding_the_window_release_the_panorama_scene_until_restore()
    {
        var directory = Directory.CreateTempSubdirectory("amarin-panorama-window-");
        try
        {
            const string fileName = "panorama.png";
            var path = Path.Combine(directory.FullName, fileName);
            wpf.Ui.Invoke(() =>
            {
                WriteSampleTexture(path);
                return true;
            });

            var settings = new AppearanceSettings
            {
                Enabled = true,
                BackdropMode = BackdropMode.Image,
                UsePanorama = true,
                PanoramaPath = fileName,
                PanoramaYaw = 78,
                AnimationsEnabled = false
            };
            var source = await AppearanceImageCache.LoadAsync(path, settings.ImageSaturation, settings.ImageBlur, panorama: true);
            Assert.NotNull(source);

            wpf.Ui.Invoke(() =>
            {
                var dictionaries = Application.Current.Resources.MergedDictionaries;
                var previousOverrides = dictionaries[ThemeManager.OverrideSlot];
                var host = new Grid();
                var window = new Window
                {
                    Content = host,
                    Left = -32000,
                    Top = 0,
                    Width = 320,
                    Height = 180,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                try
                {
                    using var appearance = new AppearanceManager(window, host) { DataRoot = directory.FullName };
                    window.Show();
                    appearance.Apply(settings);
                    var viewport = Assert.Single(host.Children.OfType<Viewport3D>());
                    var surface = PanoramaSurface(viewport);
                    var camera = Assert.IsType<PerspectiveCamera>(viewport.Camera);
                    var direction = camera.LookDirection;
                    AssertVisible();

                    window.WindowState = WindowState.Minimized;
                    AssertSuspended();
                    window.WindowState = WindowState.Normal;
                    AssertVisible();

                    window.Hide();
                    AssertSuspended();
                    window.Show();
                    AssertVisible();

                    void AssertSuspended()
                    {
                        Assert.Equal(Visibility.Collapsed, viewport.Visibility);
                        Assert.Null(surface.Material);
                        Assert.Null(surface.BackMaterial);
                    }

                    void AssertVisible()
                    {
                        Assert.Equal(Visibility.Visible, viewport.Visibility);
                        var material = Assert.IsType<DiffuseMaterial>(surface.Material);
                        Assert.Same(source, Assert.IsType<ImageBrush>(material.Brush).ImageSource);
                        Assert.Equal(direction, camera.LookDirection);
                    }
                }
                finally
                {
                    window.Close();
                    dictionaries[ThemeManager.OverrideSlot] = previousOverrides;
                }

                return true;
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_panorama_stops_pointer_frames_while_suspended_and_resumes_from_the_same_angle()
    {
        wpf.Ui.Invoke(() =>
        {
            var view = new PanoramaBackdrop();
            view.Show(SampleTexture());
            try
            {
                var camera = Assert.IsType<PerspectiveCamera>(view.View.Camera);
                var centered = camera.LookDirection;
                var frame = TimeSpan.FromMilliseconds(40);
                view.SetPointerTarget(new Vector(16, 8));
                Assert.True(view.IsPointerMoving);
                view.AdvancePointerMotion(frame);
                var moving = camera.LookDirection;
                Assert.NotEqual(centered, moving);

                view.SetWindowVisible(false);
                Assert.False(view.IsPointerMoving);
                Assert.Equal(moving, camera.LookDirection);
                view.SetPointerTarget(null);
                Assert.False(view.IsPointerMoving);

                view.SetWindowVisible(true);
                Assert.Equal(moving, camera.LookDirection);
                view.SetPointerTarget(null);
                Assert.True(view.IsPointerMoving);
                view.AdvancePointerMotion(frame);
                Assert.InRange(camera.LookDirection.X, centered.X, moving.X);
                Assert.NotEqual(moving, camera.LookDirection);
            }
            finally
            {
                view.Hide();
            }

            Assert.False(view.IsPointerMoving);
            return true;
        });
    }

    [Fact]
    public void The_panorama_eases_toward_center_and_back_when_the_pointer_returns()
    {
        wpf.Ui.Invoke(() =>
        {
            const double yaw = -20;
            const double pitch = -10;
            const double fieldOfView = 75;
            var offset = new Vector(16, 8);
            var frame = TimeSpan.FromMilliseconds(40);
            var view = new PanoramaBackdrop();
            try
            {
                view.Show(SampleTexture());
                var camera = Assert.IsType<PerspectiveCamera>(view.View.Camera);
                view.SetView(yaw + offset.X, pitch + offset.Y, fieldOfView);
                var atPointer = camera.LookDirection;
                view.SetView(yaw, pitch, fieldOfView);
                var atCenter = camera.LookDirection;
                var travel = Vector3D.AngleBetween(atCenter, atPointer);

                view.SetPointerTarget(offset);
                Assert.Equal(atCenter, camera.LookDirection);
                view.AdvancePointerMotion(frame);
                AssertBetween(atCenter, camera.LookDirection, atPointer);
                Assert.InRange(Vector3D.AngleBetween(atCenter, camera.LookDirection), 0, travel * 0.01);
                for (var i = 0; i < 4; i++)
                {
                    view.SetPointerTarget(offset);
                    view.AdvancePointerMotion(frame);
                }

                Assert.InRange(Vector3D.AngleBetween(atCenter, camera.LookDirection), travel * 0.1, travel * 0.4);
                Settle();
                Assert.Equal(atPointer, camera.LookDirection);

                view.SetPointerTarget(new Vector(0, 0));
                view.AdvancePointerMotion(frame);
                Assert.InRange(Vector3D.AngleBetween(atPointer, camera.LookDirection), travel * 0.3, travel * 0.5);
                view.SetPointerTarget(offset);
                Settle();
                Assert.Equal(atPointer, camera.LookDirection);

                view.SetPointerTarget(null);
                Assert.Equal(atPointer, camera.LookDirection);
                view.AdvancePointerMotion(frame);
                Assert.InRange(Vector3D.AngleBetween(atPointer, camera.LookDirection), 0, travel * 0.01);
                for (var i = 0; i < 4; i++)
                {
                    view.AdvancePointerMotion(frame);
                }

                var returning = camera.LookDirection;
                AssertBetween(atCenter, returning, atPointer);
                Assert.InRange(Vector3D.AngleBetween(atPointer, returning), travel * 0.1, travel * 0.4);

                view.SetPointerTarget(offset);
                Assert.Equal(returning, camera.LookDirection);
                view.AdvancePointerMotion(TimeSpan.FromMilliseconds(1));
                AssertBetween(atCenter, camera.LookDirection, returning);
                Assert.InRange(Vector3D.AngleBetween(returning, camera.LookDirection), 0, 0.1);
                Settle();
                Assert.Equal(atPointer, camera.LookDirection);

                view.SetPointerTarget(null);
                Settle();
                Assert.Equal(atCenter, camera.LookDirection);
                Assert.False(view.IsPointerMoving);
            }
            finally
            {
                view.Hide();
            }

            return true;

            void Settle()
            {
                for (var i = 0; i < 24; i++)
                {
                    view.AdvancePointerMotion(frame);
                }
            }
        });

        static void AssertBetween(Vector3D start, Vector3D current, Vector3D end)
        {
            Assert.True(start.X < current.X && current.X < end.X, $"X: {start.X} < {current.X} < {end.X}");
            Assert.True(start.Y < current.Y && current.Y < end.Y, $"Y: {start.Y} < {current.Y} < {end.Y}");
        }
    }

    [Fact]
    public void The_logo_composer_list_and_compaction_are_renderable_in_both_themes()
    {
        wpf.Ui.Invoke(() =>
        {
            var previous = ThemeManager.Current.Theme;
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var mode = (ComposerCompactMode)typeof(MainWindow).GetField("_compact", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            try
            {
                mode.Apply(new AppearanceSettings { CompactComposer = false, AnimationsEnabled = false });
                window.UpdateLayout();
                foreach (var (theme, suffix) in new[] { (AppTheme.Light, "light"), (AppTheme.Dark, "dark") })
                {
                    ThemeManager.Apply(theme);
                    var background = (Brush)window.FindResource("Bg.Window");
                    var logo = new Image { Width = 40, Height = 40, Source = (ImageSource)window.FindResource("AmarinLogo") };
                    var logoBitmap = Render(logo, 40, 40);
                    Assert.Contains(BitmapPixels(logoBitmap).Where((_, i) => i % 4 == 3), value => value > 0);
                    Save(logoBitmap, $"logo-{suffix}");

                    var body = ChatMessageViews.CreateAssistant(window, new ChatDisplayMessage
                    {
                        Role = "assistant", Status = AssistantStatus.Complete, Text = "99. Before\n100. After\n\n     999. Before\n     1000. After"
                    });
                    var text = new Border { Background = background, Padding = new Thickness(8), Child = body.Root };
                    Save(Render(text, 420, 180, 192), $"markdown-{suffix}-192");

                    var panel = new SessionRailPanel { Width = 250 };
                    panel.ShowSession(new SessionRailSnapshot(new SessionRailContext(88_000, 128_000, []), [], [],
                        new(false, false), true, Compaction: new SessionRailCompaction("grok-4-6", null,
                            _ => new SessionRailCompactPreview(39_200, 32, 42, 51_000))));
                    panel.CompactButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    var card = new Border { Child = panel, Background = background };
                    Save(Render(card, 250, 460), $"compaction-{suffix}");
                    Assert.Null(window.FindName("BalanceBadge"));
                    if (window.FindName("ComposerBorder") is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } composer)
                    {
                        var bounds = new Rect(0, 0, composer.ActualWidth, composer.ActualHeight);
                        var drawing = new DrawingVisual();
                        using (var context = drawing.RenderOpen())
                        {
                            context.DrawRectangle(background, null, bounds);
                            var brush = new VisualBrush(composer)
                            {
                                Viewbox = new Rect((Point)VisualTreeHelper.GetOffset(composer), bounds.Size),
                                ViewboxUnits = BrushMappingMode.Absolute
                            };
                            context.DrawRectangle(brush, null, bounds);
                        }
                        var composerShot = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width),
                            (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
                        composerShot.Render(drawing);
                        Save(composerShot, $"composer-{suffix}");
                    }
                }
            }
            finally
            {
                mode.Apply(new AppearanceSettings());
                ThemeManager.Apply(previous);
            }

            return true;
        });

        static byte[] BitmapPixels(BitmapSource bitmap)
        {
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }
    }

    private static GeometryModel3D PanoramaSurface(Viewport3D viewport) =>
        Assert.Single(viewport.Children.OfType<ModelVisual3D>().Select(visual => visual.Content).OfType<GeometryModel3D>());

    private static void WriteSampleTexture(string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(SampleTexture()));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static BitmapSource SampleTexture()
    {
        const int width = 128;
        const int height = 64;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)(255 * (0.5 + 0.5 * Math.Cos(2 * Math.PI * x / width)));
                pixels[offset + 2] = (byte)(255 - pixels[offset]);
                pixels[offset + 3] = 255;
            }
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static byte[] CenterPixel(BitmapSource bitmap)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static RenderTargetBitmap Render(FrameworkElement element, int width, int height, int dpi = 96)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width * dpi / 96, height * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        if (Environment.GetEnvironmentVariable("AMARIN_BETA7_SHOTS") is not { Length: > 0 } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, $"{name}.png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }
}
