using System.Windows;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>Вид пера ворона: маховое, второстепенное, кроющее.</summary>
internal enum FeatherKind
{
    Primary,
    Secondary,
    Covert
}

/// <summary>Расщеп опахала: сторона (1 — внутреннее, −1 — внешнее), место, глубина и ширина у края.</summary>
internal sealed record FeatherSplit(int Side, double U, double Depth, double Gap);

/// <summary>Перо одного места сцены: вид, длина, зеркальность, расщепы и зерно пуха.</summary>
internal sealed record FeatherSpec(FeatherKind Kind, double Length, bool Flip, IReadOnlyList<FeatherSplit> Splits, uint Seed);

/// <summary>Части пера: тело (пух, очин, опахало), блик и стержень — каждая своим цветом.</summary>
internal sealed record FeatherShape(Geometry Body, Geometry Sheen, Geometry Rachis);

/// <summary>
/// Перо ворона плоским силуэтом (1.37.1), по построению концепта «Капля» (<c>feather.js</c>):
/// изогнутый стержень, опахало веретеном, бородки наклонены к кончику, расщепы клином по бородке,
/// голый очин и пух у основания.
/// </summary>
/// <remarks>
/// Не картинка и не рисунок от руки: перо строится из своих долей, поэтому чёткое при любом
/// масштабе интерфейса и у каждого места сцены своё. Координаты — как у концепта: кончик в (0, 0),
/// очин вниз по оси y, длина <see cref="FeatherSpec.Length"/>. Все заливки — Nonzero, как у SVG:
/// лепестки пуха перекрываются, и чётно-нечётное правило вырезало бы их пересечения.
/// </remarks>
internal static class WhatsNewFeather
{
    /// <param name="W">Ширина внутреннего опахала в долях длины.</param>
    /// <param name="Outer">Доля внешнего опахала от внутреннего.</param>
    /// <param name="Peak">Где перо шире всего.</param>
    /// <param name="Tip">Длина сужения к кончику.</param>
    /// <param name="End">Где кончается опахало.</param>
    private sealed record Kind(
        double W, double Outer, double Peak, double Tip, double End, double Sweep, double Barb, double Bend, double S, int Fluff);

    private static Kind Of(FeatherKind kind) => kind switch
    {
        FeatherKind.Secondary => new Kind(.12, .66, .46, .36, .8, .3, 1.15, .055, .5, 4),
        FeatherKind.Covert => new Kind(.16, .82, .4, .4, .7, .26, 1, .04, .7, 5),
        _ => new Kind(.085, .5, .5, .42, .82, .32, 1.25, .07, .25, 3)
    };

    /// <summary>Нижняя губа расщепа чуть оттопырена: насколько, в долях ширины опахала.</summary>
    private const double Jut = .05;

    public static FeatherShape Build(FeatherSpec spec)
    {
        var k = Of(spec.Kind);
        var length = spec.Length;
        var wide = k.W * length;
        var end = k.End;
        var sign = spec.Flip ? -1 : 1;

        // Стержень — кубическая кривая: дуга (s ≈ 1) или лёгкая S (s < .5).
        var c1 = new Vector(sign * k.Bend * length * 1.15, length * .33);
        var c2 = new Vector(sign * k.Bend * length * (k.S * 1.4 - .3), length * .7);

        Point P(double s)
        {
            var b = 3 * (1 - s) * (1 - s) * s;
            var c = 3 * (1 - s) * s * s;
            var d = s * s * s;
            return new Point(b * c1.X + c * c2.X, b * c1.Y + c * c2.Y + d * length);
        }

        Vector T(double s)
        {
            var dx = 3 * (1 - s) * (1 - s) * c1.X + 6 * (1 - s) * s * (c2.X - c1.X) + 3 * s * s * (0 - c2.X);
            var dy = 3 * (1 - s) * (1 - s) * c1.Y + 6 * (1 - s) * s * (c2.Y - c1.Y) + 3 * s * s * (length - c2.Y);
            var t = new Vector(dx, dy);
            t.Normalize();
            return t;
        }

        Vector N(double s)
        {
            var t = T(s);
            return new Vector(t.Y, -t.X);
        }

        // Ширина опахала по доле u от кончика до конца опахала: кончик сужается мягко и скруглённо,
        // к очину опахало закругляется и сходит к стержню.
        double Width(double u, int side)
        {
            var tip = Math.Pow(Math.Sin(Math.PI / 2 * Math.Min(1, u / k.Tip)), .62);
            var fall = Math.Pow(1 - Smooth(k.Peak + .12, 1.015, u), .5);
            var belly = 1 + .06 * Math.Sin(Math.PI * Math.Min(1, u / (k.Peak * 2)));
            var w = tip * fall * belly * wide;
            if (side < 0)
            {
                w *= k.Outer * (1 - .25 * Smooth(.55, 1, u));
            }

            return w;
        }

        // Расщеп у края не уже 2,6 точки: на маленьком пере он иначе исчез бы в сглаживании.
        var splits = spec.Splits
            .Select(split => split with { Gap = Math.Max(split.Gap, 2.6 / (length * end)) })
            .ToList();

        // Край: точка стержня, сдвинутая по нормали на ширину и назад к кончику — бородки идут
        // наискось. Ниже расщепа край чуть выступает: нижняя губа расщепа оттопырена.
        Point Edge(double u, int side)
        {
            var s = u * end;
            var p = P(s);
            var t = T(s);
            var n = N(s);
            var w = Width(u, side);
            foreach (var split in splits)
            {
                if (split.Side != side)
                {
                    continue;
                }

                var d = u - (split.U + split.Gap / 2);
                if (d >= 0 && d < .06)
                {
                    w += wide * Jut * (1 - d / .06) * (1 - d / .06);
                }
            }

            var lean = k.Sweep * Smooth(0, .2, u) * w;
            return p + side * n * w - t * lean;
        }

        // Бородка от края к стержню: отходит от стержня наискось к кончику, поэтому от края внутрь
        // идёт вниз, к очину, и чуть выгнута. f — доля пути от края к стержню.
        Point Barb(double u, int side, double f)
        {
            var e = Edge(u, side);
            var w = Width(u, side);
            var sb = Math.Min(1, u * end + w * k.Barb / length);
            var p = P(sb);
            var t = T(sb);
            var len = (e - p).Length;
            var c = new Point((p.X + e.X) / 2, (p.Y + e.Y) / 2) - t * len * .12;
            var q = 1 - f;
            return new Point(
                q * q * e.X + 2 * q * f * c.X + f * f * p.X,
                q * q * e.Y + 2 * q * f * c.Y + f * f * p.Y);
        }

        // Точек края — по длине пера: мелкому пёрышку двести шестьдесят ни к чему, рисунок тот же.
        var samples = (int)Math.Clamp(Math.Round(length * 1.4), 120, 260);

        List<Point> Side(int side)
        {
            var points = new List<Point>(samples + 64);
            var cuts = splits.Where(split => split.Side == side).OrderBy(split => split.U).ToList();
            var next = 0;
            for (var i = 0; i <= samples; i++)
            {
                var u = (double)i / samples;
                if (next < cuts.Count && u >= cuts[next].U - cuts[next].Gap / 2)
                {
                    // Клин: верхний край идёт по бородке внутрь до дна, нижний от дна выходит к краю
                    // по соседней бородке — у края расщеп шире, к дну сходится в точку.
                    var cut = cuts[next];
                    var ua = cut.U - cut.Gap / 2;
                    var ub = cut.U + cut.Gap / 2;
                    for (var j = 0; j <= 12; j++)
                    {
                        points.Add(Barb(ua, side, cut.Depth * j / 12));
                    }

                    for (var j = 11; j >= 0; j--)
                    {
                        var f = j / 12.0;
                        points.Add(Barb(ub - (ub - ua) * Math.Pow(f, 1.6), side, cut.Depth * f));
                    }

                    while (i <= samples && (double)i / samples <= ub)
                    {
                        i++;
                    }

                    i--;
                    next++;
                    continue;
                }

                points.Add(Edge(u, side));
            }

            return points;
        }

        var right = Side(1);
        var left = Side(-1);
        left.Reverse();
        var vane = Polygon([P(0), .. right, .. left]);

        // Стержень и очин одной полосой: тоньше к кончику, у основания скруглён.
        Geometry Strip(double s0, double s1, double h0, double h1)
        {
            const int steps = 50;
            var outer = new List<Point>(steps + 1);
            var inner = new List<Point>(steps + 1);
            for (var i = 0; i <= steps; i++)
            {
                var s = s0 + (s1 - s0) * i / steps;
                var p = P(s);
                var n = N(s);
                var h = h0 + (h1 - h0) * i / steps;
                outer.Add(p + n * h);
                inner.Add(p - n * h);
            }

            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var context = geometry.Open())
            {
                context.BeginFigure(outer[0], true, true);
                context.PolyLineTo(outer.Skip(1).ToList(), true, true);
                context.ArcTo(inner[steps], new Size(h1, h1), 0, false, SweepDirection.Clockwise, true, true);
                inner.Reverse();
                context.PolyLineTo(inner, true, true);
            }

            geometry.Freeze();
            return geometry;
        }

        // На маленьком пере тонкое растворилось бы в сглаживании: у толщин есть нижний предел.
        var rachis = Strip(.02, end, Math.Max(.35, length * .0012), Math.Max(.6, length * .0042));
        var quill = Strip(end - .02, 1, Math.Max(.9, length * .0062), Math.Max(.7, length * .0042));

        // Пух у основания: опахало внизу распускается на мягкие пряди. Прядь — вытянутая линза от
        // стержня наружу и вниз, корень — под опахалом: видно только то, что выходит из-под края.
        var seed = spec.Seed;
        double Random()
        {
            seed = unchecked(seed * 1664525u + 1013904223u);
            return seed / 4294967296.0;
        }

        var fluff = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = fluff.Open())
        {
            foreach (var side in new[] { 1, -1 })
            {
                var count = side > 0 ? k.Fluff : Math.Max(2, k.Fluff - 1);
                for (var i = 0; i < count; i++)
                {
                    var f = (double)i / Math.Max(1, count - 1);
                    var s = end - .1 + .1 * f + (Random() - .5) * .01;
                    var p = P(s);
                    var n = N(s);
                    var t = T(s);
                    var len = wide * (side > 0 ? 1.05 : .82) * (1 - .3 * f) * (.88 + .24 * Random());
                    var aside = .55 - .3 * f + (Random() - .5) * .1;
                    var direction = side * n * aside + t * (1 - aside);
                    direction.Normalize();
                    var tipPoint = p + direction * len + t * len * .18;
                    var middle = p + direction * len * .5 - t * len * .06;
                    var thick = Math.Max(.9, len * (.15 - .03 * f));
                    var across = new Vector(-direction.Y, direction.X);
                    context.BeginFigure(p, true, true);
                    context.QuadraticBezierTo(middle - across * thick, tipPoint, true, true);
                    context.QuadraticBezierTo(middle + across * thick, p, true, true);
                }
            }
        }

        fluff.Freeze();

        // Блик: полоса вдоль стержня по внутреннему опахалу, на треть его ширины.
        var shine = new List<Point>(81);
        var spine = new List<Point>(81);
        for (var i = 0; i <= 80; i++)
        {
            var u = .08 + .8 * i / 80;
            var s = u * end;
            var p = P(s);
            var n = N(s);
            var t = T(s);
            var w = Width(u, 1) * .42 * Math.Pow(Math.Sin(Math.PI * i / 80), .5);
            shine.Add(p + n * w - t * (k.Sweep * w));
            spine.Add(p);
        }

        spine.Reverse();
        var sheen = Polygon([.. shine, .. spine]);

        var body = new GeometryGroup { FillRule = FillRule.Nonzero, Children = { fluff, quill, vane } };
        body.Freeze();
        return new FeatherShape(body, sheen, rachis);
    }

    private static StreamGeometry Polygon(List<Point> points)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            context.PolyLineTo(points.Skip(1).ToList(), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static double Smooth(double a, double b, double x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
