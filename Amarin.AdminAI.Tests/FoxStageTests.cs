using System.Windows;
using System.Windows.Controls;
using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Сцена Розы (1.35.0) без окна программы: подставной хозяин с кромкой поля ввода и полом, время —
/// своё, кадры — вызовом <c>Advance</c>. Проверяется то, что глазами не видно: куда он падает,
/// не вылетает ли за окно, доходит ли удар до мозга и сколько шалостей делают что-то по-настоящему.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class FoxStageTests : IDisposable
{
    private static readonly FoxSprites Sprites = FoxSprites.Load();

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-fox-stage-" + Guid.NewGuid().ToString("N"));

    public FoxStageTests(WpfFixture wpf) => _wpf = wpf;

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

    [Fact]
    public void Dropped_above_the_composer_he_lands_on_its_edge()
    {
        var (mode, spot, feet, edge) = With((stage, host, _) =>
        {
            stage.BeginDrag(new Point(400, 200));
            stage.Release();
            Fly(stage);
            return (stage.Mode, stage.Spot, stage.Corner.Y + stage.Side, host.Edge.Y);
        });

        Assert.Equal(FoxMode.Idle, mode);
        Assert.Equal("home", spot);

        // Лапы на кромке: низ кадра — на ней плюс пустые строки под лапами, не больше пары пикселей рисунка.
        Assert.InRange(feet - edge, 0, 4);
    }

    [Fact]
    public void Dropped_beside_the_composer_he_falls_to_the_floor()
    {
        var (spot, bottom, floor) = With((stage, host, _) =>
        {
            stage.BeginDrag(new Point(40, 100));
            stage.Release();
            Fly(stage);
            return (stage.Spot, stage.Corner.Y + stage.Side, host.Floor());
        });

        Assert.Equal("floor", spot);
        Assert.InRange(bottom - floor, 0, 4);
    }

    [Fact]
    public void A_hard_throw_bounces_off_the_walls_and_never_leaves_the_window()
    {
        var (spot, lowest, highest, width) = With((stage, host, _) =>
        {
            stage.BeginDrag(new Point(300, 200));
            stage.Throw(2.5, -1.2);
            double low = double.MaxValue, high = double.MinValue;
            for (var i = 0; i < 600 && stage.Mode == FoxMode.Fly; i++)
            {
                stage.Advance(16);
                low = Math.Min(low, stage.Corner.X);
                high = Math.Max(high, stage.Corner.X + stage.Side);
            }

            return (stage.Spot, low, high, host.Width());
        });

        Assert.NotEqual("", spot);
        Assert.True(lowest >= -1, $"левее окна: {lowest}");
        Assert.True(highest <= width + 1, $"правее окна: {highest}");
    }

    /// <summary>Скорость удара — та, что мозг считает «приложился»: с трёхсот точек кружится голова, с малой высоты — радость.</summary>
    [Fact]
    public void How_hard_he_landed_reaches_the_brain()
    {
        var (soft, hard) = With((stage, host, store) =>
        {
            stage.BeginDrag(new Point(400, host.Edge.Y - 40));
            stage.Release();
            Fly(stage);
            var afterSoft = store.Snapshot().Today;
            var games = afterSoft.Games;

            stage.BeginDrag(new Point(60, 10));
            stage.Release();
            Fly(stage);
            return (games, store.Snapshot().Today.Annoyance);
        });

        Assert.Equal(1, soft);
        Assert.True(hard > 0, "падение с высоты окна не обидело");
    }

    [Fact]
    public void Without_animations_a_dropped_fox_appears_on_the_edge_below_at_once()
    {
        var (mode, spot) = With((stage, _, _) =>
        {
            stage.BeginDrag(new Point(400, 100));
            stage.Release();
            return (stage.Mode, stage.Spot);
        }, motion: false);

        Assert.Equal(FoxMode.Idle, mode);
        Assert.Equal("home", spot);
    }

    [Fact]
    public void He_rides_the_composer_edge_when_it_grows()
    {
        var (before, after, moved) = With((stage, host, _) =>
        {
            var y = stage.Corner.Y;
            host.Edge = host.Edge with { Y = host.Edge.Y - 60 };
            stage.Relayout();
            return (y, stage.Corner.Y, stage.Spot);
        }, motion: false);

        Assert.Equal("home", moved);
        Assert.Equal(before - 60, after, 0.6);
    }

    [Fact]
    public void Minimizing_mid_flight_seats_him_on_the_edge_below()
    {
        var (mode, spot) = With((stage, _, _) =>
        {
            stage.BeginDrag(new Point(400, 150));
            stage.Release();
            stage.Advance(16);
            stage.SetPaused(true);
            return (stage.Mode, stage.Spot);
        });

        Assert.Equal(FoxMode.Idle, mode);
        Assert.Equal("home", spot);
    }

    /// <summary>
    /// Настоящих шалостей ровно две: «Новый чат» и толчок колонки. Остальные — картинка поверх окна
    /// и ничего в программе не нажимают — ни с анимациями, ни без них.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_two_mischiefs_touch_the_program(bool motion)
    {
        var calls = new Dictionary<string, (int NewChat, int Nudge)>();
        foreach (var kind in FoxMischief.Acting.Concat(FoxMischief.Visual))
        {
            calls[kind] = With((stage, host, _) =>
            {
                stage.Mischief(kind);
                for (var i = 0; i < 400; i++)
                {
                    host.Now = host.Now.AddMilliseconds(100);
                    stage.Advance(16);
                }

                return (host.NewChats, host.Nudges);
            }, motion);
        }

        Assert.Equal((1, 0), calls[FoxMischief.NewChat]);
        Assert.Equal((0, 1), calls[FoxMischief.PushColumn]);
        Assert.All(FoxMischief.Visual, kind => Assert.Equal((0, 0), calls[kind]));
    }

    /// <summary>Отпущенный на ходу летит в ту сторону, куда его несла рука, а не падает отвесно.</summary>
    [Fact]
    public void Let_go_on_the_move_he_keeps_the_hand_speed()
    {
        var (fromX, flewTo, mode) = With((stage, _, _) =>
        {
            stage.BeginDrag(new Point(300, 150));
            Thread.Sleep(15);
            stage.Hold(new Point(330, 140));
            Thread.Sleep(15);
            stage.Hold(new Point(360, 130));
            var start = stage.Corner.X;
            stage.Release();
            for (var i = 0; i < 10; i++)
            {
                stage.Advance(16);
            }

            return (start, stage.Corner.X, stage.Mode);
        });

        Assert.Equal(FoxMode.Fly, mode);
        Assert.True(flewTo - fromX > 60, $"улетел только на {flewTo - fromX:0} точек");
    }

    [Fact]
    public void The_release_speed_is_the_last_moment_of_the_hand_and_a_still_hand_gives_none()
    {
        var now = TimeSpan.FromSeconds(10);
        (TimeSpan, double, double)[] moving = [(now - TimeSpan.FromMilliseconds(200), 0, 0), (now - TimeSpan.FromMilliseconds(60), 100, 0), (now - TimeSpan.FromMilliseconds(10), 150, -20)];
        var (vx, vy) = FoxStage.ReleaseVelocity(moving, now, 160, -24);
        Assert.InRange(vx, 0.9, 1.1);
        Assert.InRange(vy, -0.5, -0.3);

        (TimeSpan, double, double)[] still = [(now - TimeSpan.FromMilliseconds(400), 0, 0), (now - TimeSpan.FromMilliseconds(300), 50, 0)];
        Assert.Equal((0d, 0d), FoxStage.ReleaseVelocity(still, now, 50, 0));
    }

    /// <summary>С высоты он отскакивает — каждый раз ниже, — и только потом садится.</summary>
    [Fact]
    public void A_fall_from_high_bounces_lower_each_time_before_he_sits()
    {
        var (bounced, mode, spot) = With((stage, _, _) =>
        {
            stage.BeginDrag(new Point(60, 20));
            stage.Release();
            var lowest = double.MinValue;
            var rises = 0;
            var previous = stage.Corner.Y;
            for (var i = 0; i < 600 && stage.Mode == FoxMode.Fly; i++)
            {
                stage.Advance(16);
                if (stage.Corner.Y < previous - 0.5 && previous >= lowest - 1)
                {
                    rises++;
                }

                lowest = Math.Max(lowest, stage.Corner.Y);
                previous = stage.Corner.Y;
            }

            return (rises, stage.Mode, stage.Spot);
        });

        Assert.True(bounced > 0, "не отскочил ни разу");
        Assert.Equal(FoxMode.Idle, mode);
        Assert.Equal("floor", spot);
    }

    /// <summary>
    /// Водить курсором туда-сюда по голове — гладить, даже если концы движения выходят за контур
    /// рисунка: прежде уход курсора сбрасывал счёт, и погладить не удавалось вовсе.
    /// </summary>
    [Fact]
    public void Stroking_his_head_back_and_forth_pets_him()
    {
        var pets = With((stage, host, store) =>
        {
            var corner = stage.Corner;
            Point At(double cell) => new(stage.Side * cell / FoxSprites.FrameSize, stage.Side * 13.5 / FoxSprites.FrameSize);
            foreach (var cell in (double[])[13, 15, 17, 19, 21, 19, 17, 15, 13, 15, 17, 19, 21, 19, 17])
            {
                host.Now = host.Now.AddMilliseconds(60);
                var onSprite = At(cell);
                _ = stage.Stroke(onSprite, new Point(corner.X + onSprite.X, corner.Y + onSprite.Y));
            }

            return store.Snapshot().Today.Pets;
        }, motion: false);

        Assert.Equal(1, pets);
    }

    [Fact]
    public void Hidden_he_leaves_the_window_and_comes_back_on_time()
    {
        var (hidden, visible, back) = With((stage, host, _) =>
        {
            stage.HideFor(1);
            for (var i = 0; i < 200 && stage.Mode != FoxMode.Hidden; i++)
            {
                host.Now = host.Now.AddMilliseconds(100);
                stage.Advance(16);
            }

            var mode = stage.Mode;
            var shown = host.Layer.Children.OfType<FrameworkElement>().Any(child => child.GetType().Name == "FoxSprite" && child.IsVisible);
            stage.Unhide();
            return (mode, shown, stage.Mode);
        }, motion: false);

        Assert.Equal(FoxMode.Hidden, hidden);
        Assert.False(visible);
        Assert.Equal(FoxMode.Idle, back);
    }

    /// <summary>Падает, пока не сядет: не больше десяти секунд кадров.</summary>
    private static void Fly(FoxStage stage)
    {
        for (var i = 0; i < 600 && stage.Mode is FoxMode.Fly or FoxMode.Drag; i++)
        {
            stage.Advance(16);
        }
    }

    private T With<T>(Func<FoxStage, TestHost, FoxStore, T> body, bool motion = true) => _wpf.Ui.Invoke(() =>
    {
        var host = new TestHost { Moving = motion };
        var window = new Window
        {
            Width = 820,
            Height = 640,
            Left = -32000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = host.Layer
        };
        window.Show();
        window.UpdateLayout();
        using var store = new FoxStore(Path.Combine(_root, Guid.NewGuid().ToString("N")), () => false, TimeSpan.FromMilliseconds(10));
        var stage = new FoxStage(host, store, Sprites, () => host.Now, new Random(7));
        try
        {
            stage.Start();
            for (var i = 0; i < 200 && stage.Mode != FoxMode.Idle; i++)
            {
                stage.Advance(16);
            }

            return body(stage, host, store);
        }
        finally
        {
            stage.Stop();
            window.Close();
        }
    });

    /// <summary>Окно в миниатюре: кромка поля ввода посередине снизу, пол под ней, кнопки — пустые.</summary>
    private sealed class TestHost : IFoxHost
    {
        public Canvas Layer { get; } = new() { Width = 800, Height = 600, ClipToBounds = true };

        public FoxEdge Edge { get; set; } = new(200, 700, 480);

        public DateTime Now { get; set; } = new(2026, 10, 13, 12, 0, 0);

        public bool Moving { get; set; } = true;

        public int NewChats { get; private set; }

        public int Nudges { get; private set; }

        public FoxSituation Situation() => new() { WindowActive = true, MischiefEnabled = true, ReadChat = false };

        public FoxEdge Home() => Edge;

        public FoxEdge? Card() => null;

        public double Floor() => 598;

        public double Width() => 800;

        public int Size() => 48;

        public bool Motion() => Moving;

        public Point? Pointer() => null;

        public void NewChat() => NewChats++;

        public void NudgeChatList() => Nudges++;

        public Rect SendButton() => new(640, 520, 32, 32);

        public Rect ModelChip() => new(240, 520, 120, 32);

        public void ShowMenu(FoxStage stage)
        {
        }

        public void TalkClosed()
        {
        }
    }
}
