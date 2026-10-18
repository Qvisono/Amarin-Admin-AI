using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Amarin.Fox;

/// <summary>
/// Как видно, что Роза чувствует: значок эмоции в пузыре над головой, дрожь с волнами по бокам,
/// большой кусь с зубами отдельно от него и фраза из-за края окна, когда он не идёт на «Позвать».
/// </summary>
/// <remarks>
/// Значок — сведения, а не движение: без анимаций он показывается неподвижно. Волны, зубы и следы
/// когтей — движение, без анимаций их нет. Покадровая отрисовка подключается, пока что-то из этого
/// живёт (<see cref="Moving"/>), и снимается сама.
/// </remarks>
internal sealed partial class FoxStage
{
    /// <summary>Сколько живут зубы куся, мс: сомкнуться — первая четверть, потом гаснут.</summary>
    private const double ChompLife = 820;

    private Image? _emote;
    private DateTime _emoteUntil;
    private Image? _trembleLeft;
    private Image? _trembleRight;
    private DateTime _trembleUntil;
    private Chomp? _chomp;
    private DateTime _callShakeUntil;
    private readonly Dictionary<(string Anim, int Frame), (int X0, int X1, int Y0, int Y1)> _boxes = [];

    private sealed record Chomp(Image Top, Image Bottom, Point At, TimeSpan Start);

    /// <summary>Значок эмоции сейчас над головой — тестам.</summary>
    internal bool EmoteShown => _emote is { Visibility: Visibility.Visible };

    /// <summary>Волны дрожи сейчас по бокам — тестам.</summary>
    internal bool TrembleShown => _trembleLeft is { Visibility: Visibility.Visible };

    /// <summary>Зубы куся сейчас в слое — тестам.</summary>
    internal bool ChompShown => _chomp is not null;

    /// <summary>Значок эмоции над головой на <paramref name="ms"/>.</summary>
    private void ShowEmote(string name, int ms)
    {
        if (_mode is FoxMode.Off or FoxMode.Away or FoxMode.Hidden)
        {
            return;
        }

        if (_emote is null)
        {
            _emote = new Image { IsHitTestVisible = false, Focusable = false };
            _host.Layer.Children.Add(_emote);
        }

        var source = _bitmaps.Emote(name);
        _emote.Source = source;
        _emote.Width = Px(source.PixelWidth / (double)_bitmaps.Prescale);
        _emote.Height = Px(source.PixelHeight / (double)_bitmaps.Prescale);
        RenderOptions.SetBitmapScalingMode(_emote, _bitmaps.Prescale == 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _emoteUntil = _clock().AddMilliseconds(Math.Max(600, ms));
        PlaceEmote(_clock());
        ScheduleFrame();
    }

    /// <summary>Пузырь со значком — сбоку над головой, со стороны затылка: мордочку он не закрывает.</summary>
    private void PlaceEmote(DateTime now)
    {
        if (_emote is null)
        {
            return;
        }

        if (now >= _emoteUntil || _mode is FoxMode.Off or FoxMode.Away or FoxMode.Hidden)
        {
            _emote.Visibility = Visibility.Collapsed;
            return;
        }

        var head = HeadPoint();
        Canvas.SetLeft(_emote, Math.Round(head.X + (_flip ? -Px(22) : Px(8))));
        Canvas.SetTop(_emote, Math.Round(head.Y - Px(24)));
        _emote.Visibility = Visibility.Visible;
    }

    /// <summary>Глаз нынешнего кадра на слое; у кадра без якоря — макушка рисунка.</summary>
    private Point HeadPoint()
    {
        var animation = _sprites.Animations[_anim];
        var index = Math.Min(_frame, animation.Frames.Count - 1);
        var (hx, hy) = animation.Frames[index].Head ?? (14, Box(_anim, index).Y0 + 10);
        return new Point(_move.X + Px(_flip ? FoxSprites.FrameSize - 1 - hx : hx), _move.Y + Px(hy));
    }

    /// <summary>Рамка рисунка кадра в клетках — по ней волны встают по бокам тела, а не кадра.</summary>
    private (int X0, int X1, int Y0, int Y1) Box(string anim, int frame)
    {
        if (!_boxes.TryGetValue((anim, frame), out var box))
        {
            var rows = _sprites.Animations[anim].Frames[frame].Rows;
            int x0 = FoxSprites.FrameSize, x1 = 0, y0 = FoxSprites.FrameSize, y1 = 0;
            for (var y = 0; y < rows.Length; y++)
            {
                for (var x = 0; x < rows[y].Length; x++)
                {
                    if (rows[y][x] != '.')
                    {
                        (x0, x1, y0, y1) = (Math.Min(x0, x), Math.Max(x1, x), Math.Min(y0, y), Math.Max(y1, y));
                    }
                }
            }

            box = (x0, x1, y0, y1);
            _boxes[(anim, frame)] = box;
        }

        return box;
    }

    private void Effect(string name, int ms)
    {
        switch (name)
        {
            case "tremble":
                Tremble(ms);
                break;
            case "chomp":
                StartChomp(BiteTarget());
                break;
            case "shove":
                Shove();
                break;
        }
    }

    /// <summary>Дрожит <paramref name="ms"/>, и по бокам тела мигают волны.</summary>
    private void Tremble(int ms)
    {
        var until = _clock().AddMilliseconds(ms);
        _shakeUntil = until;
        if (_host.Motion())
        {
            _trembleUntil = until;
            _trembleLeft ??= AddPixels("tremble-l");
            _trembleRight ??= AddPixels("tremble-r");
        }

        StartRendering();
    }

    private Image AddPixels(string particle)
    {
        var image = Pixels(_bitmaps.Particle(particle));
        image.RenderTransform = Transform.Identity;
        _host.Layer.Children.Add(image);
        return image;
    }

    /// <summary>Волны — по бокам рамки тела, на середине его высоты, и вздрагивают через 90 мс.</summary>
    private void PlaceTremble(DateTime now)
    {
        if (_trembleLeft is null || _trembleRight is null)
        {
            return;
        }

        var on = now < _trembleUntil && _mode is not (FoxMode.Off or FoxMode.Away or FoxMode.Hidden or FoxMode.Drag or FoxMode.Fly);
        _trembleLeft.Visibility = _trembleRight.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
        {
            return;
        }

        var animation = _sprites.Animations[_anim];
        var (x0, x1, y0, y1) = Box(_anim, Math.Min(_frame, animation.Frames.Count - 1));
        if (_flip)
        {
            (x0, x1) = (FoxSprites.FrameSize - 1 - x1, FoxSprites.FrameSize - 1 - x0);
        }

        var jump = (int)(_watch.Elapsed.TotalMilliseconds / 90) % 2;
        var top = _move.Y + Px((y0 + y1) / 2.0) - (_trembleLeft.Height / 2);
        Canvas.SetLeft(_trembleLeft, Math.Round(_move.X + Px(x0) - _trembleLeft.Width - Px(2 + jump)));
        Canvas.SetLeft(_trembleRight, Math.Round(_move.X + Px(x1 + 1) + Px(2 + jump)));
        Canvas.SetTop(_trembleLeft, Math.Round(top));
        Canvas.SetTop(_trembleRight, Math.Round(top));
    }

    /// <summary>Куда кусать: курсор, если он рядом, иначе воздух перед носом.</summary>
    private Point BiteTarget()
    {
        if (PointerInReach() is { } pointer)
        {
            return pointer;
        }

        var head = HeadPoint();
        return new Point(head.X + (_flip ? Px(10) : -Px(10)), head.Y + Px(4));
    }

    /// <summary>Курсор, до которого достают зубы; <c>null</c> — кусать некого, только воздух.</summary>
    private Point? PointerInReach()
    {
        var center = new Point(_move.X + (_sprite.Side / 2), _move.Y + (_sprite.Side / 2));
        return _host.Pointer() is { } pointer && (pointer - center).Length < _sprite.Side * 4 ? pointer : null;
    }

    /// <summary>
    /// Укус сбивает курсор: когда зубы смыкаются, рука отлетает от головы на кадр. Без анимаций зубов
    /// нет — нет и толчка: курсор, прыгнувший сам по себе, выглядел бы сбоем.
    /// </summary>
    private void Shove()
    {
        if (!_host.Motion() || _paused)
        {
            return;
        }

        After(TimeSpan.FromMilliseconds(ChompLife * 0.26), () =>
        {
            if (PointerInReach() is not { } pointer)
            {
                return;
            }

            var away = pointer - HeadPoint();
            if (away.Length < 1)
            {
                away = new Vector(_flip ? 1 : -1, 0);
            }

            away.Normalize();
            _host.ShoveCursor(pointer + (away * _sprite.Side));
        });
    }

    /// <summary>
    /// Зубы: верхний и нижний ряды отдельно от Розы, вдвое крупнее его пикселя и полупрозрачные,
    /// смыкаются над <paramref name="at"/> и гаснут. Без анимаций зубов нет — это движение.
    /// </summary>
    private void StartChomp(Point at)
    {
        if (!_host.Motion() || _paused)
        {
            return;
        }

        RemoveChomp();
        var top = AddPixels("jaws-top");
        var bottom = AddPixels("jaws-bottom");
        foreach (var jaw in (Image[])[top, bottom])
        {
            jaw.Width *= 2;
            jaw.Height *= 2;
            jaw.Opacity = 0;
        }

        _chomp = new Chomp(top, bottom, at, _watch.Elapsed);
        StartRendering();
    }

    private void StepChomp()
    {
        if (_chomp is not { } chomp)
        {
            return;
        }

        var t = (_watch.Elapsed - chomp.Start).TotalMilliseconds / ChompLife;
        if (t >= 1)
        {
            RemoveChomp();
            return;
        }

        var close = Math.Min(1, t / 0.26);
        var open = Px(16) * 2;
        var gap = (1 - (close * close)) * open;
        var bite = Px(3);
        Canvas.SetLeft(chomp.Top, Math.Round(chomp.At.X - (chomp.Top.Width / 2)));
        Canvas.SetLeft(chomp.Bottom, Math.Round(chomp.At.X - (chomp.Bottom.Width / 2)));
        Canvas.SetTop(chomp.Top, Math.Round(chomp.At.Y - chomp.Top.Height - (gap / 2) + bite));
        Canvas.SetTop(chomp.Bottom, Math.Round(chomp.At.Y + (gap / 2) - bite));
        var opacity = t < 0.55 ? 0.78 : 0.78 * (1 - ((t - 0.55) / 0.45));
        chomp.Top.Opacity = chomp.Bottom.Opacity = opacity;
        if (close >= 1 && _shakeUntil < _clock())
        {
            // Сомкнул — и сам вздрогнул от усилия.
            _shakeUntil = _clock().AddMilliseconds(220);
        }
    }

    private void RemoveChomp()
    {
        if (_chomp is { } chomp)
        {
            _host.Layer.Children.Remove(chomp.Top);
            _host.Layer.Children.Remove(chomp.Bottom);
            _chomp = null;
        }
    }

    /// <summary>Частица в точке слоя, без полёта: следы когтей на месте удара.</summary>
    private void MarkAt(string particle, Point at, double life)
    {
        if (!_host.Motion() || _paused)
        {
            return;
        }

        var image = Pixels(_bitmaps.Particle(particle));
        var mote = new Mote(image, at.X - (image.Width / 2), at.Y - (image.Height / 2), 0, 0, life);
        _motes.Add(mote);
        _host.Layer.Children.Add(image);
        Move(mote);
        StartRendering();
    }

    /// <summary>Фраза из-за края окна, у следа «Позвать»: убежавший отказывается прийти.</summary>
    private void Shout(string text, int ms)
    {
        Bubble(text, ms);
        _callShakeUntil = _clock().AddMilliseconds(320);
        StartRendering();
        ScheduleFrame();
    }

    /// <summary>След «Позвать» вздрагивает, пока Роза из-за края ругается.</summary>
    private void StepCallShake()
    {
        if (_callMark is null)
        {
            return;
        }

        var shaking = _callShakeUntil > _clock();
        _callMark.RenderTransform = shaking
            ? new TranslateTransform(((int)(_watch.Elapsed.TotalMilliseconds / 40) % 2 == 0 ? -1 : 1) * 3, 0)
            : Transform.Identity;
    }

    /// <summary>Значок, волны, зубы — долой: окно свернули. <paramref name="detach"/> — Розу выключили, убрать из слоя совсем.</summary>
    private void ClearFeelings(bool detach = false)
    {
        RemoveChomp();
        _trembleUntil = default;
        _emoteUntil = default;
        foreach (var image in (Image?[])[_emote, _trembleLeft, _trembleRight])
        {
            if (image is null)
            {
                continue;
            }

            image.Visibility = Visibility.Collapsed;
            if (detach)
            {
                _host.Layer.Children.Remove(image);
            }
        }

        if (detach)
        {
            _emote = _trembleLeft = _trembleRight = null;
        }
    }
}
