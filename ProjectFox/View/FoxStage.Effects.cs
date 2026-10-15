using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;

namespace Amarin.Fox;

/// <summary>Что вокруг Розы: пузырь реплики, частицы, след лапки «Позвать», мячик, лазер, шалости.</summary>
/// <remarks>
/// Всё это — дети того же слоя, и прозрачность меняется только у них: у самого слоя её держит
/// блокировка (<c>LockNow</c> гасит слои окна), и анимация слоя перебила бы её. Мышь ловит только
/// след лапки; пузырь, частицы, мячик и точка лазера щелчки пропускают.
/// </remarks>
internal sealed partial class FoxStage
{
    /// <summary>Сколько живёт частица, мс.</summary>
    private const double MoteLife = 1100;

    private readonly List<Mote> _motes = [];
    private Border? _bubble;
    private TextBlock? _bubbleText;
    private DateTime _bubbleUntil;
    private int _bubbleFade;
    private Border? _callMark;

    private Image? _ball;
    private double _ballX;
    private double _ballY;
    private double _ballVx;
    private double _ballVy;
    private string? _ballSpot;
    private Image? _laser;
    private DateTime _laserUntil;
    private DateTime _nextPounce;

    private sealed class Mote(Image image, double x, double y, double vx, double vy, double life)
    {
        public Image Image { get; } = image;
        public double X { get; set; } = x;
        public double Y { get; set; } = y;
        public double Vx { get; } = vx;
        public double Vy { get; } = vy;
        public double Life { get; } = life;
        public double Age { get; set; }
    }

    /// <summary>Реплика над головой. Длинная держится дольше: по 45 мс на знак, но не меньше <paramref name="ms"/>.</summary>
    private void Bubble(string text, int ms)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_bubble is null)
        {
            _bubbleText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 220 };
            _bubbleText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            _bubble = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 5, 9, 6),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                Focusable = false,
                Child = _bubbleText
            };
            _bubble.SetResourceReference(Border.BackgroundProperty, "Bg.Elevated");
            _bubble.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
            _host.Layer.Children.Add(_bubble);
        }

        _bubbleText!.Text = text;
        _bubble.Visibility = Visibility.Visible;
        _bubbleUntil = _clock().AddMilliseconds(Math.Max(ms, 1200 + (text.Length * 45)));
        if (_host.Motion())
        {
            _bubble.Opacity = 0;
            _bubbleFade = 1;
            StartRendering();
        }
        else
        {
            _bubble.Opacity = 1;
            _bubbleFade = 0;
        }

        PlaceBubble();
    }

    /// <summary>Пузырь над головой, внутри окна и ниже заголовка.</summary>
    private void PlaceBubble()
    {
        if (_bubble is null || _bubble.Visibility != Visibility.Visible)
        {
            return;
        }

        if (_mode is FoxMode.Off or FoxMode.Hidden || (_mode == FoxMode.Away && _callMark is not { Visibility: Visibility.Visible }))
        {
            _bubble.Visibility = Visibility.Collapsed;
            _bubbleFade = 0;
            return;
        }

        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _bubble.DesiredSize;
        if (_mode == FoxMode.Away && _callMark is { Visibility: Visibility.Visible } mark)
        {
            // Убежавший ругается из-за края: фраза — над следом «Позвать».
            mark.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var markLeft = Canvas.GetLeft(mark);
            Canvas.SetLeft(_bubble, Math.Round(Math.Clamp(markLeft + mark.DesiredSize.Width - size.Width, 8, Math.Max(8, _host.Width() - size.Width - 8))));
            Canvas.SetTop(_bubble, Math.Round(Math.Max(34, Canvas.GetTop(mark) - size.Height - 6)));
            return;
        }

        var head = _move.Y + (_mode == FoxMode.Peek ? Px(20) : Px(2));
        var left = Math.Clamp(_move.X + (_sprite.Side / 2) - (size.Width / 2), 8, Math.Max(8, _host.Width() - size.Width - 8));

        // Над значком эмоции, а не поверх него.
        var raise = _emote is { Visibility: Visibility.Visible } ? Px(12) : 0;
        var top = Math.Max(34, head - size.Height - 4 - raise);
        Canvas.SetLeft(_bubble, Math.Round(left));
        Canvas.SetTop(_bubble, Math.Round(top));
    }

    private void ExpireBubble(DateTime now)
    {
        if (_bubble is null || _bubble.Visibility != Visibility.Visible || now < _bubbleUntil || _bubbleFade == -1)
        {
            return;
        }

        if (_host.Motion() && !_paused)
        {
            _bubbleFade = -1;
            StartRendering();
        }
        else
        {
            _bubble.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Частицы над головой: сердечки, ноты, звёздочки. Без анимаций их нет — они только движение.</summary>
    private void Burst(string name, int count)
    {
        if (!_host.Motion() || _paused || _mode is FoxMode.Off or FoxMode.Away or FoxMode.Hidden)
        {
            return;
        }

        var source = _bitmaps.Particle(name);
        var top = _move.Y + (_mode == FoxMode.Peek ? Px(20) : Px(4));
        var center = _move.X + (_sprite.Side / 2);
        for (var i = 0; i < Math.Min(count, 6); i++)
        {
            var image = Pixels(source);
            var x = center - (image.Width / 2) + ((_random.NextDouble() - 0.5) * Px(18));
            var mote = new Mote(
                image,
                x,
                top - (i * Px(3)),
                (_random.NextDouble() - 0.5) * 0.03,
                -0.035 - (_random.NextDouble() * 0.02),
                MoteLife + (i * 120));
            _motes.Add(mote);
            _host.Layer.Children.Add(image);
            Move(mote);
        }

        StartRendering();
    }

    /// <summary>Картинка рисунка в слое: пиксель рисунка — те же целые пиксели экрана, что у Розы.</summary>
    private Image Pixels(BitmapSource source)
    {
        // Картинка уже увеличена заранее (Prescale): в точках рисунка она во столько же раз меньше.
        var image = new Image
        {
            Source = source,
            Width = Px(source.PixelWidth / (double)_bitmaps.Prescale),
            Height = Px(source.PixelHeight / (double)_bitmaps.Prescale),
            IsHitTestVisible = false,
            Focusable = false,
            RenderTransform = new TranslateTransform()
        };
        RenderOptions.SetBitmapScalingMode(image, _bitmaps.Prescale == 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        return image;
    }

    private static void Move(Mote mote)
    {
        var shift = (TranslateTransform)mote.Image.RenderTransform;
        shift.X = Math.Round(mote.X);
        shift.Y = Math.Round(mote.Y);
        var t = mote.Age / mote.Life;
        mote.Image.Opacity = Math.Clamp(1 - (t * t), 0, 1);
    }

    private void StepEffects(double ms)
    {
        for (var i = 0; i < _motes.Count; i++)
        {
            var mote = _motes[i];
            mote.Age += ms;
            if (mote.Age >= mote.Life)
            {
                _host.Layer.Children.Remove(mote.Image);
                _motes.RemoveAt(i--);
                continue;
            }

            mote.X += mote.Vx * ms;
            mote.Y += mote.Vy * ms;
            Move(mote);
        }

        StepChomp();
        StepCallShake();
        if (_bubble is not null && _bubbleFade != 0)
        {
            _bubble.Opacity = Math.Clamp(_bubble.Opacity + (_bubbleFade * ms / 140), 0, 1);
            if (_bubbleFade > 0 && _bubble.Opacity >= 1)
            {
                _bubbleFade = 0;
            }
            else if (_bubbleFade < 0 && _bubble.Opacity <= 0)
            {
                _bubbleFade = 0;
                _bubble.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Убирает всё летучее: частицы, мячик, лазер, пузырь. След «Позвать» — нет: он нужен, пока Роза за краем.</summary>
    private void ClearEffects()
    {
        foreach (var mote in _motes)
        {
            _host.Layer.Children.Remove(mote.Image);
        }

        _motes.Clear();
        RemoveBall();
        StopLaser();
        StopGame();
        ClearFeelings();
        if (_bubble is not null)
        {
            _bubble.Visibility = Visibility.Collapsed;
            _bubbleFade = 0;
        }
    }

    /// <summary>След лапки у края поля ввода: Роза убежал, щелчок зовёт обратно.</summary>
    private void ShowCallMark()
    {
        if (_callMark is null)
        {
            var paw = Pixels(_bitmaps.Particle("paw"));
            paw.RenderTransform = Transform.Identity;
            paw.Margin = new Thickness(0, 0, 6, 0);
            var label = new TextBlock { Text = Loc.Get("S.Fox.Call"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(paw);
            row.Children.Add(label);
            _callMark = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 10, 4),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Focusable = false,
                Child = row
            };
            _callMark.SetResourceReference(Border.BackgroundProperty, "Bg.Elevated");
            _callMark.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
            _callMark.MouseLeftButtonDown += (_, e) => e.Handled = true;
            _callMark.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Signal(FoxSignalKind.Call);
            };
            _host.Layer.Children.Add(_callMark);
        }

        _callMark.Visibility = Visibility.Visible;
        PlaceCallMark();
    }

    private void HideCallMark()
    {
        if (_callMark is not null)
        {
            _callMark.Visibility = Visibility.Collapsed;
        }
    }

    private void PlaceCallMark()
    {
        if (_callMark is null || _callMark.Visibility != Visibility.Visible)
        {
            return;
        }

        _callMark.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var home = _host.Home();
        Canvas.SetLeft(_callMark, Math.Round(home.Right - _callMark.DesiredSize.Width - 12));
        Canvas.SetTop(_callMark, Math.Round(home.Y - _callMark.DesiredSize.Height - 6));
    }

    /// <summary>Мячик из меню: вылетает от курсора, падает на кромку, Роза приносит его.</summary>
    public void PlayBall(Point from)
    {
        if (_mode is not (FoxMode.Idle or FoxMode.Sleep or FoxMode.Peek) || !_host.Motion())
        {
            Signal(FoxSignalKind.PlayBall);
            return;
        }

        if (_mode == FoxMode.Sleep)
        {
            Wake();
        }

        if (_mode == FoxMode.Peek)
        {
            EndPeek();
        }

        Replan();
        RemoveBall();
        _ball = Pixels(_bitmaps.Item("ball"));
        _ballX = from.X;
        _ballY = from.Y;
        _ballVx = (from.X > _host.Width() / 2 ? -1 : 1) * (0.25 + (_random.NextDouble() * 0.3));
        _ballVy = -0.5;
        _ballSpot = null;
        _host.Layer.Children.Add(_ball);
        StartRendering();
    }

    /// <summary>Лазерная указка: 15 секунд гоняется за точкой под курсором.</summary>
    public void PlayLaser()
    {
        Signal(FoxSignalKind.PlayLaser);
        if (!_host.Motion() || _mode is not (FoxMode.Idle or FoxMode.Sleep or FoxMode.Peek))
        {
            return;
        }

        if (_mode == FoxMode.Peek)
        {
            EndPeek();
        }

        Replan();
        _laser ??= Pixels(FoxBitmaps.Render([[',', 'b', ','], ['b', 'b', 'b'], [',', 'b', ',']], _sprites.Palette, cold: false, flip: false, _bitmaps.Prescale));
        if (!_host.Layer.Children.Contains(_laser))
        {
            _host.Layer.Children.Add(_laser);
        }

        _laserUntil = _clock().AddSeconds(15);
        StartRendering();
    }

    private void StopLaser()
    {
        if (_laser is null)
        {
            return;
        }

        _host.Layer.Children.Remove(_laser);
        _laser = null;
        _laserUntil = default;
        if (_mode is FoxMode.Idle or FoxMode.Walk && _spot == "home")
        {
            After(TimeSpan.FromSeconds(2), () => GoHomeEdge(HomeFoot(_host.Home()), run: false));
        }
    }

    private void RemoveBall()
    {
        if (_ball is not null)
        {
            _host.Layer.Children.Remove(_ball);
            _ball = null;
        }
    }

    private void StepGames(double ms)
    {
        if (_ball is not null)
        {
            StepBall(ms);
        }

        if (_laser is not null)
        {
            StepLaser();
        }

        StepGame();
    }

    private void StepBall(double ms)
    {
        var size = _ball!.Width;
        if (_ballSpot is null)
        {
            var before = _ballY + size;
            _ballVy += Gravity * ms;
            _ballX += _ballVx * ms;
            _ballY += _ballVy * ms;
            if (_ballX < 0 || _ballX > _host.Width() - size)
            {
                _ballX = Math.Clamp(_ballX, 0, _host.Width() - size);
                _ballVx = -_ballVx * 0.6;
            }

            var center = _ballX + (size / 2);
            foreach (var spot in (string[])["home", "settings", "floor"])
            {
                if (spot == "settings" && _host.Card() is null)
                {
                    continue;
                }

                var edge = EdgeOf(spot);
                if (_ballVy > 0 && edge.Contains(center) && before <= edge.Y && _ballY + size >= edge.Y)
                {
                    _ballY = edge.Y - size;
                    if (_ballVy > 0.25)
                    {
                        _ballVy = -_ballVy * 0.45;
                        _ballVx *= 0.8;
                    }
                    else
                    {
                        _ballSpot = spot;

                        // Принесённый мячик лежит и ждёт, пока человек его бросит.
                        if (!_ballWaiting)
                        {
                            Fetch(spot, center);
                        }
                    }

                    break;
                }
            }
        }

        var shift = (TranslateTransform)_ball.RenderTransform;
        shift.X = Math.Round(_ballX);
        shift.Y = Math.Round(_ballY);
    }

    /// <summary>Бежит к мячику, берёт в зубы и несёт на место.</summary>
    private void Fetch(string spot, double x)
    {
        void Grab()
        {
            RemoveBall();
            _item = "ball";
            _itemUntil = _clock().AddSeconds(3);
            Signal(FoxSignalKind.PlayBall);
            After(TimeSpan.FromSeconds(1.2), () => GoHomeEdge(HomeFoot(_host.Home()), run: false));
        }

        if (_spot == spot && !Airborne)
        {
            WalkTo(x, run: true, then: Grab);
        }
        else
        {
            JumpTo(spot, x, Grab);
        }
    }

    private void StepLaser()
    {
        var now = _clock();
        if (now >= _laserUntil || _host.Pointer() is not { } pointer)
        {
            if (now >= _laserUntil)
            {
                StopLaser();
            }

            return;
        }

        var dot = _laser!;
        var shift = (TranslateTransform)dot.RenderTransform;
        shift.X = Math.Round(pointer.X - (dot.Width / 2));
        shift.Y = Math.Round(pointer.Y - (dot.Height / 2));

        if (_mode is not (FoxMode.Idle or FoxMode.Walk))
        {
            return;
        }

        var target = ClampFoot(EdgeOf(_spot), pointer.X);
        var near = Math.Abs(target - _foot) < Px(10);
        if (!near)
        {
            if (_mode == FoxMode.Idle)
            {
                WalkTo(target, run: true);
            }
            else
            {
                _walkTo = target;
                var gait = Math.Abs(target - _foot) > Px(30) ? "run" : "walk";
                if (gait != _gait)
                {
                    _gait = gait;
                    Play(gait, gait);
                }
            }
        }
        else if (_mode == FoxMode.Idle && now >= _nextPounce && Math.Abs(pointer.Y - _edgeY) < _sprite.Side * 1.5)
        {
            _nextPounce = now.AddSeconds(1.2);
            Play("jump", "sit");
            _face = "excited";
            _faceUntil = now.AddMilliseconds(900);
        }
    }

    /// <summary>
    /// Шалость. Делают что-то по-настоящему две — «Новый чат» и толчок колонки, — остальные только
    /// картинка. Без анимаций картинок нет, а настоящие делаются на месте.
    /// </summary>
    internal void Mischief(string kind)
    {
        if (_mode != FoxMode.Idle)
        {
            return;
        }

        Replan();
        var home = _host.Home();
        var seat = HomeFoot(home);
        void Back(double seconds) => After(TimeSpan.FromSeconds(seconds), () => GoHomeEdge(seat, run: false));
        void Poke(Action act)
        {
            Play("poke", "sit");
            _face = "sly";
            _faceUntil = _clock().AddMilliseconds(1600);
            After(TimeSpan.FromMilliseconds(260), act);
        }

        var motion = _host.Motion();
        switch (kind)
        {
            case FoxMischief.NewChat:
                Poke(_host.NewChat);
                break;

            case FoxMischief.PushColumn when motion && _spot == "home":
                WalkTo(home.Left, run: true, then: () =>
                {
                    _flip = false;
                    Poke(_host.NudgeChatList);
                    Back(4);
                });
                break;

            case FoxMischief.PushColumn:
                Poke(_host.NudgeChatList);
                break;

            case FoxMischief.SitOnSend when motion && !_host.SendButton().IsEmpty:
                JumpTo("send", _host.SendButton().Left + (_host.SendButton().Width / 2), () =>
                {
                    _face = "sly";
                    _faceUntil = _clock().AddSeconds(3);
                    After(TimeSpan.FromSeconds(25), () => JumpTo("home", seat));
                });
                break;

            case FoxMischief.ChewChip when motion && !_host.ModelChip().IsEmpty:
                JumpTo("chip", _host.ModelChip().Left + (_host.ModelChip().Width / 2), () =>
                {
                    for (var i = 0; i < 3; i++)
                    {
                        After(TimeSpan.FromMilliseconds(400 + (i * 900)), () =>
                        {
                            Play("bite", "sit");
                            Burst("bite", 1);
                        });
                    }

                    After(TimeSpan.FromSeconds(4), () => JumpTo("home", seat));
                });
                break;

            case FoxMischief.TailInInput when motion && _spot == "home":
                WalkTo((home.Left + home.Right) / 2, run: false, then: () =>
                {
                    LieDown(wag: true);
                    After(TimeSpan.FromSeconds(20), () =>
                    {
                        _lying = false;
                        Play("stretch", "sit");
                        Back(2);
                    });
                });
                break;

            case FoxMischief.Footprints when motion && _spot == "home":
                var from = _foot;
                var to = Math.Max(home.Left + Px(17), _foot - Px(150));
                for (var i = 1; i <= 8; i++)
                {
                    var at = from + ((to - from) * i / 8);
                    After(TimeSpan.FromMilliseconds(i * 250), () => Footprint(at));
                }

                WalkTo(to, run: false, then: () => Back(1.5));
                break;

            case FoxMischief.PeekColumn when motion && _spot == "home":
                WalkTo(home.Left, run: true, then: () =>
                {
                    Peek(TimeSpan.FromSeconds(10));
                    Back(14);
                });
                break;
        }
    }

    /// <summary>Тающий след лапы на поле ввода под Розой.</summary>
    private void Footprint(double x)
    {
        var source = _bitmaps.Particle("paw");
        var image = Pixels(source);
        var mote = new Mote(image, x - (image.Width / 2), _edgeY + Px(3), 0, 0, 4000);
        _motes.Add(mote);
        _host.Layer.Children.Add(image);
        Move(mote);
        StartRendering();
    }
}
