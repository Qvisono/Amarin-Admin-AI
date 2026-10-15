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

    /// <summary>
    /// За шкирку — маятник: рука вправо, а низ тела отстаёт влево (поворот по часовой), и наоборот.
    /// Прежде знак был обратным, и тело летело вперёд руки.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void In_the_hand_the_body_lags_behind_the_hand(int direction)
    {
        var tilt = With((stage, _, _) =>
        {
            stage.BeginDrag(new Point(400, 150));
            for (var i = 1; i <= 4; i++)
            {
                Thread.Sleep(10);
                stage.Hold(new Point(400 + (direction * i * 20), 150));
                stage.Advance(16);
            }

            return stage.Tilt;
        });

        Assert.True(direction * tilt > 5, $"наклон {tilt:0.0}° при руке {(direction > 0 ? "вправо" : "влево")}");
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

    /// <summary>Упал на пол над кнопками поля ввода — отходит в свободный промежуток, а не накрывает их.</summary>
    [Fact]
    public void Dropped_on_the_floor_he_steps_off_the_buttons()
    {
        var (spot, corner) = With((stage, host, _) =>
        {
            host.Spans = [new FoxEdge(0, 150, 598), new FoxEdge(750, 800, 598)];
            stage.BeginDrag(new Point(165, 420));
            stage.Release();
            Fly(stage);
            Settle(stage);
            return (stage.Spot, stage.Corner.X);
        });

        Assert.Equal("floor", spot);
        Assert.True(corner < 150, $"сидит у {corner:0}, а свободно до 150");
    }

    /// <summary>
    /// Где положили, там и сидит: прежде через 25 секунд он сам уходил в любимый угол справа, а
    /// с пола — через 8.
    /// </summary>
    [Fact]
    public void Put_down_on_the_composer_he_stays_there()
    {
        var (before, after) = With((stage, host, _) =>
        {
            stage.BeginDrag(new Point(280, host.Edge.Y - 30));
            stage.Release();
            Fly(stage);
            Settle(stage);
            var at = stage.Corner.X;
            for (var i = 0; i < 60; i++)
            {
                host.Now = host.Now.AddSeconds(1);
                stage.Advance(16);
            }

            Settle(stage);
            return (at, stage.Corner.X);
        });

        Assert.True(Math.Abs(after - before) < 80, $"ушёл с {before:0} на {after:0}");
    }

    [Fact]
    public void The_top_of_the_chat_column_footer_is_an_edge_too()
    {
        var spot = With((stage, host, _) =>
        {
            host.ColumnEdge = new FoxEdge(20, 180, 300);
            stage.BeginDrag(new Point(90, 200));
            stage.Release();
            Fly(stage);
            return stage.Spot;
        });

        Assert.Equal("column", spot);
    }

    /// <summary>Страх видно сразу: значок над головой и волны по бокам тела.</summary>
    [Fact]
    public void Fear_shows_an_emote_and_waves_beside_him()
    {
        var (emote, waves) = With((stage, _, _) =>
        {
            stage.Signal(FoxSignalKind.DangerAsked);
            stage.Advance(16);
            return (stage.EmoteShown, stage.TrembleShown);
        });

        Assert.True(emote, "нет значка страха");
        Assert.True(waves, "нет волн дрожи");
    }

    [Fact]
    public void A_cold_fox_snaps_big_teeth_at_a_lingering_cursor()
    {
        var teeth = With((stage, host, store) =>
        {
            store.Update(state =>
            {
                state.Affection = -60;
                state.Cold = true;
            });
            host.PointerAt = new Point(stage.Corner.X - 10, stage.Corner.Y + 20);
            stage.Signal(FoxSignalKind.HoverLong);
            return stage.ChompShown;
        });

        Assert.True(teeth);
    }

    /// <summary>Указка гаснет через своё время, как бы ни метался курсор, и Роза садится.</summary>
    [Fact]
    public void The_laser_ends_on_time_and_he_stops_chasing()
    {
        var (during, laserAfter, modeAfter, anim) = With((stage, host, _) =>
        {
            stage.PlayLaser();
            var holding = false;
            for (var i = 0; i < 400; i++)
            {
                host.PointerAt = new Point(i % 20 < 10 ? 250 : 650, 470);
                host.Now = host.Now.AddMilliseconds(100);
                stage.Advance(16);
                holding |= stage.Holding;
            }

            return (holding, stage.LaserShown, stage.Mode, stage.Anim);
        });

        Assert.True(during);
        Assert.False(laserAfter);
        Assert.Equal(FoxMode.Idle, modeAfter);
        Assert.DoesNotContain(anim, (string[])["run", "walk"]);
    }

    /// <summary>
    /// За указкой Роза бегает прямо под курсором, и махание ею — игра, а не дразнилка. Прежде оно
    /// затевало драку с курсором или кусь, а через 20 с — снова, и погоня не кончалась.
    /// </summary>
    [Fact]
    public void Waving_the_laser_over_him_is_play_not_teasing()
    {
        var (game, teeth, laser) = With((stage, host, store) =>
        {
            store.Update(state => state.Resentment = 60);
            stage.PlayLaser();
            for (var i = 0; i < 8; i++)
            {
                host.Now = host.Now.AddMilliseconds(100);
                var x = i % 2 == 0 ? 300 : 340;
                stage.MovedOver(new Point(20, 30), new Point(x, 460));
            }

            return (stage.Game, stage.ChompShown, stage.LaserShown);
        });

        Assert.Null(game);
        Assert.False(teeth);
        Assert.True(laser);
    }

    /// <summary>Игра из меню сменяет указку: обе сразу тянули бы Розу к курсору и от него.</summary>
    [Fact]
    public void A_game_from_the_menu_replaces_the_laser()
    {
        var (laser, game) = With((stage, _, _) =>
        {
            stage.PlayLaser();
            stage.PlayGame(FoxWill.Catch);
            return (stage.LaserShown, stage.Game);
        });

        Assert.False(laser);
        Assert.Equal(FoxWill.Catch, game);
    }

    /// <summary>Пока идёт игра человека, воля своего не затевает.</summary>
    [Fact]
    public void The_will_starts_nothing_over_a_laser()
    {
        var (chosen, game, anim) = With((stage, host, store) =>
        {
            host.SinceTyping = TimeSpan.FromMinutes(5);
            store.Update(state =>
            {
                state.Boredom = 90;
                state.NextActivityAt = null;
            });
            stage.PlayLaser();
            var before = stage.Anim;
            stage.Signal(new FoxSignal(FoxSignalKind.Tick, Value: 10));
            return (store.Snapshot().NextActivityAt is not null, stage.Game, stage.Anim == before);
        });

        Assert.True(chosen, "воля так и не выбирала");
        Assert.Null(game);
        Assert.True(anim);
    }

    /// <summary>Принёс мячик, а его не бросили за двенадцать секунд — уносит и обижается.</summary>
    [Fact]
    public void A_ball_he_brought_and_nobody_threw_makes_him_sulk()
    {
        var (waited, after, resentment) = With((stage, host, store) =>
        {
            stage.PlayGame(FoxWill.Fetch);
            for (var i = 0; i < 800 && !stage.BallWaiting; i++)
            {
                stage.Advance(16);
            }

            var waiting = stage.BallWaiting;
            host.Now = host.Now.AddSeconds(13);
            stage.Advance(16);
            return (waiting, stage.BallWaiting, store.Snapshot().Resentment);
        });

        Assert.True(waited, "мячик так и не лёг");
        Assert.False(after);
        Assert.True(resentment >= 3);
    }

    [Fact]
    public void Hide_and_seek_ends_when_he_is_found()
    {
        var (peeking, mode, games) = With((stage, _, store) =>
        {
            stage.PlayGame(FoxWill.HideSeek);
            var peek = stage.Mode == FoxMode.Peek;
            stage.ClickAt(new Point(20, 20));
            return (peek, stage.Mode, store.Snapshot().Today.Games);
        });

        Assert.True(peeking);
        Assert.Equal(FoxMode.Idle, mode);
        Assert.Equal(1, games);
    }

    [Fact]
    public void In_tag_he_runs_from_a_close_cursor()
    {
        var mode = With((stage, host, _) =>
        {
            stage.PlayGame(FoxWill.Catch);
            host.PointerAt = new Point(stage.Corner.X + (stage.Side / 2), stage.Corner.Y + (stage.Side / 2));
            stage.Advance(16);
            return stage.Mode;
        });

        Assert.Equal(FoxMode.Walk, mode);
    }

    /// <summary>Доходит, куда шёл, и садится: не больше десяти секунд кадров.</summary>
    private static void Settle(FoxStage stage)
    {
        for (var i = 0; i < 600 && stage.Mode != FoxMode.Idle; i++)
        {
            stage.Advance(16);
        }
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

        /// <summary>Сколько человек не печатал: по умолчанию ноль, и воля молчит.</summary>
        public TimeSpan SinceTyping { get; set; }

        public FoxSituation Situation() => new() { WindowActive = true, MischiefEnabled = true, ReadChat = false, SinceTyping = SinceTyping, Motion = Moving };

        public FoxEdge Home() => Edge;

        public FoxEdge? Card() => null;

        public double Floor() => 598;

        /// <summary>Пол: левый промежуток и правый, а середина над кнопками (300–600) занята.</summary>
        public List<FoxEdge> Spans { get; set; } = [new(0, 300, 598), new(600, 800, 598)];

        public IReadOnlyList<FoxEdge> FloorSpans() => Spans;

        public FoxEdge? Column() => ColumnEdge;

        public FoxEdge? ColumnEdge { get; set; }

        public Point? PointerAt { get; set; }

        public double Width() => 800;

        public int Size() => 48;

        public bool Motion() => Moving;

        public Point? Pointer() => PointerAt;

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
