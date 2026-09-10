using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
// В проекте включён неявный using System.IO — без псевдонима Path тут двоится.
using Path = System.Windows.Shapes.Path;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Скобки и надстрочные знаки формул, нарисованные кривыми.
/// </summary>
/// <remarks>
/// Растянутый по вертикали глиф скобки выглядит именно как растянутый: тонкие места
/// становятся ещё тоньше, толстые расползаются. Поэтому скобка любой высоты рисуется
/// заново — одинаковой толщины сверху донизу.
/// </remarks>
internal static class MathDelimiter
{
    /// <summary>Скобка нужной высоты; <c>null</c>, если скобки нет (<c>\right.</c>).</summary>
    public static FrameworkElement? Create(string glyph, double height, double size, string brushKey, bool opening)
    {
        if (string.IsNullOrEmpty(glyph))
        {
            return null;
        }

        // И толщина, и ширина растут вместе с высотой: скобка на две строки, нарисованная
        // теми же волосяными линиями, что и на одну, выглядит проволочной.
        var stroke = Math.Max(1, size * 0.05 + height * 0.006);
        var width = glyph switch
        {
            "(" or ")" => Math.Min(size * 1.1, size * 0.26 + height * 0.13),
            "{" or "}" => Math.Min(size * 1.2, size * 0.3 + height * 0.1),
            "[" or "]" or "⌈" or "⌉" or "⌊" or "⌋" => Math.Min(size * 0.9, size * 0.26 + height * 0.07),
            "⟨" or "⟩" => Math.Min(size * 0.9, size * 0.26 + height * 0.09),
            "‖" => size * 0.36,
            "|" => size * 0.26,
            _ => 0
        };

        var data = glyph switch
        {
            "(" => Curve(width, height, stroke, opening: true),
            ")" => Curve(width, height, stroke, opening: false),
            "[" => Bracket(width, height, stroke, opening: true, top: true, bottom: true),
            "]" => Bracket(width, height, stroke, opening: false, top: true, bottom: true),
            "⌈" => Bracket(width, height, stroke, opening: true, top: true, bottom: false),
            "⌉" => Bracket(width, height, stroke, opening: false, top: true, bottom: false),
            "⌊" => Bracket(width, height, stroke, opening: true, top: false, bottom: true),
            "⌋" => Bracket(width, height, stroke, opening: false, top: false, bottom: true),
            "{" => Brace(width, height, stroke, opening: true),
            "}" => Brace(width, height, stroke, opening: false),
            "⟨" => Angle(width, height, stroke, opening: true),
            "⟩" => Angle(width, height, stroke, opening: false),
            "|" => Bars(width, height, stroke, count: 1),
            "‖" => Bars(width, height, stroke, count: 2),
            _ => null
        };

        if (data is null)
        {
            // Незнакомый разделитель — обычный символ по центру высоты.
            var block = new TextBlock
            {
                Text = glyph,
                FontSize = size,
                TextWrapping = TextWrapping.NoWrap
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var host = new Canvas { Width = block.DesiredSize.Width, Height = height };
            Canvas.SetTop(block, Math.Max(0, (height - block.DesiredSize.Height) / 2));
            host.Children.Add(block);
            return host;
        }

        var path = new Path
        {
            Data = data,
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = width,
            Height = height
        };
        path.SetResourceReference(Shape.StrokeProperty, brushKey);
        return path;
    }

    /// <summary>Надстрочный знак под ширину основания; <c>null</c> для неизвестного.</summary>
    public static FrameworkElement? CreateAccent(MathAccentKind kind, double bodyWidth, double size, string brushKey)
    {
        var stroke = Math.Max(1, size * 0.055);
        var width = Math.Max(size * 0.35, Math.Min(bodyWidth, size * 1.6));

        switch (kind)
        {
            case MathAccentKind.Bar or MathAccentKind.Overline:
            {
                var line = new Rectangle { Width = Math.Max(bodyWidth, width), Height = stroke };
                line.SetResourceReference(Shape.FillProperty, brushKey);
                return line;
            }

            case MathAccentKind.Hat:
            {
                var height = size * 0.24;
                return Stroke(
                    Format("M {0},{1} L {2},{3} L {4},{1}", stroke / 2, height - stroke / 2, width / 2, stroke / 2, width - stroke / 2),
                    width,
                    height,
                    stroke,
                    brushKey);
            }

            case MathAccentKind.Tilde:
            {
                var height = size * 0.24;
                return Stroke(
                    Format(
                        "M {0},{1} C {2},{3} {4},{5} {6},{1}",
                        stroke / 2, height * 0.62,
                        width * 0.3, -height * 0.1,
                        width * 0.7, height * 0.95,
                        width - stroke / 2),
                    width,
                    height,
                    stroke,
                    brushKey);
            }

            case MathAccentKind.Vector:
            {
                var height = size * 0.26;
                var y = height * 0.55;
                var head = size * 0.16;
                return Stroke(
                    Format(
                        "M {0},{1} L {2},{1} M {3},{4} L {2},{1} L {3},{5}",
                        stroke / 2, y,
                        width - stroke / 2,
                        width - head, y - head * 0.6,
                        y + head * 0.6),
                    width,
                    height,
                    stroke,
                    brushKey);
            }

            case MathAccentKind.Dot or MathAccentKind.DoubleDot:
            {
                var radius = Math.Max(1.2, size * 0.07);
                var height = radius * 2;
                var canvas = new Canvas { Width = width, Height = height };
                var count = kind == MathAccentKind.Dot ? 1 : 2;
                for (var i = 0; i < count; i++)
                {
                    var dot = new Ellipse { Width = radius * 2, Height = radius * 2 };
                    dot.SetResourceReference(Shape.FillProperty, brushKey);
                    var offset = count == 1
                        ? width / 2 - radius
                        : width / 2 - radius + (i == 0 ? -radius * 1.6 : radius * 1.6);
                    Canvas.SetLeft(dot, offset);
                    canvas.Children.Add(dot);
                }

                return canvas;
            }

            default:
                return null;
        }
    }

    private static Path Stroke(string data, double width, double height, double thickness, string brushKey)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = width,
            Height = height
        };
        path.SetResourceReference(Shape.StrokeProperty, brushKey);
        return path;
    }

    private static Geometry Curve(double width, double height, double stroke, bool opening)
    {
        var near = stroke / 2 + width * 0.12;
        var far = width - stroke / 2 - width * 0.12;
        var (start, control) = opening ? (far, near - width * 0.05) : (near, far + width * 0.05);

        return Geometry.Parse(Format(
            "M {0},{1} C {2},{3} {2},{4} {0},{5}",
            start, stroke / 2,
            control, height * 0.25,
            height * 0.75,
            height - stroke / 2));
    }

    private static Geometry Bracket(double width, double height, double stroke, bool opening, bool top, bool bottom)
    {
        var spine = opening ? stroke / 2 + width * 0.18 : width - stroke / 2 - width * 0.18;
        var arm = opening ? width - stroke / 2 : stroke / 2;
        var y0 = stroke / 2;
        var y1 = height - stroke / 2;

        var data = Format("M {0},{1} L {0},{2}", spine, y0, y1);
        if (top)
        {
            data += Format(" M {0},{1} L {2},{1}", spine, y0, arm);
        }

        if (bottom)
        {
            data += Format(" M {0},{1} L {2},{1}", spine, y1, arm);
        }

        return Geometry.Parse(data);
    }

    private static Geometry Brace(double width, double height, double stroke, bool opening)
    {
        var tip = opening ? stroke / 2 : width - stroke / 2;
        var back = opening ? width - stroke / 2 : stroke / 2;
        var mid = height / 2;
        var bend = opening ? width * 0.55 : width * 0.45;

        // Две дуги, сходящиеся остриём посередине: так фигурная скобка растёт на любую высоту.
        return Geometry.Parse(Format(
            "M {0},{1} C {2},{3} {4},{5} {6},{7} C {4},{8} {2},{9} {0},{10}",
            back, stroke / 2,
            bend, height * 0.08,
            bend, mid - height * 0.12,
            tip, mid,
            mid + height * 0.12,
            height * 0.92,
            height - stroke / 2));
    }

    private static Geometry Angle(double width, double height, double stroke, bool opening)
    {
        var tip = opening ? stroke / 2 : width - stroke / 2;
        var back = opening ? width - stroke / 2 : stroke / 2;
        return Geometry.Parse(Format(
            "M {0},{1} L {2},{3} L {0},{4}",
            back, stroke / 2,
            tip, height / 2,
            height - stroke / 2));
    }

    private static Geometry Bars(double width, double height, double stroke, int count)
    {
        var data = "";
        for (var i = 0; i < count; i++)
        {
            var x = count == 1
                ? width / 2
                : width / 2 + (i == 0 ? -width * 0.2 : width * 0.2);
            data += Format(" M {0},{1} L {0},{2}", x, stroke / 2, height - stroke / 2);
        }

        return Geometry.Parse(data.Trim());
    }

    private static string Format(string format, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, format, args);
}
