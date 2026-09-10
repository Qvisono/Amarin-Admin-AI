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
/// Собирает разобранную формулу в элементы WPF: дроби с чертой, корни со знаком радикала,
/// индексы, скобки в высоту содержимого, матрицы.
/// </summary>
/// <remarks>
/// <para>
/// Всё держится на одном понятии — «коробке»: ширина, подъём над базовой линией и свес под ней
/// (<see cref="MathVisual"/>). Стандартные панели WPF выравнивают по краям, а формула требует
/// выравнивания по базовой линии — знаменатель и показатель степени должны сидеть относительно
/// неё, а не относительно верха строки. Поэтому каждый узел укладывается в <see cref="Canvas"/>
/// с точными координатами, а наружу отдаёт свои три числа.
/// </para>
/// <para>
/// Цвет назначается через <c>SetResourceReference</c>, поэтому формулы переключают тему вместе
/// со всем остальным текстом.
/// </para>
/// </remarks>
internal static class MathRenderer
{
    /// <summary>Коробка: элемент и его метрики относительно базовой линии.</summary>
    internal sealed record MathVisual(
        FrameworkElement Element,
        double Width,
        double Ascent,
        double Descent,
        MathTokenKind Kind = MathTokenKind.Variable)
    {
        public double Height => Ascent + Descent;
    }

    /// <summary>
    /// Шрифт формул. Cambria Math есть в любой Windows и рисует ∑, ∫ и греческие буквы так,
    /// как их положено видеть; интерфейсный шрифт для этого не годится.
    /// </summary>
    private const string MathFontFamily = "Cambria Math, Segoe UI Symbol, Segoe UI";

    private sealed class Context(FrameworkElement host, double size, bool display)
    {
        public FrameworkElement Host { get; } = host;

        /// <summary>Кегль основного уровня. Индексы считают свой от него.</summary>
        public double Size { get; } = size;

        /// <summary>Выключная формула: пределы у ∑ встают сверху и снизу, дроби крупнее.</summary>
        public bool Display { get; } = display;

        public FontFamily Font { get; } = new(MathFontFamily);

        public string BrushKey { get; set; } = "Text.Secondary";
    }

    /// <summary>Готовый элемент формулы. <paramref name="display"/> — выключная, отдельным блоком.</summary>
    public static FrameworkElement Build(
        FrameworkElement host,
        string latex,
        double fontSize,
        bool display,
        string brushKey = "Text.Secondary")
    {
        var visual = BuildVisual(host, latex, fontSize, display, brushKey);
        return visual.Element;
    }

    internal static MathVisual BuildVisual(
        FrameworkElement host,
        string latex,
        double fontSize,
        bool display,
        string brushKey = "Text.Secondary")
    {
        var context = new Context(host, fontSize, display) { BrushKey = brushKey };
        try
        {
            return Layout(LatexParser.Parse(latex), context, context.Size);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            // Формула не должна ронять сообщение: показываем исходник моноширинным.
            return Glyph(context, latex, context.Size, MathTokenKind.Upright);
        }
    }

    // ───────────────────────── укладка узлов ─────────────────────────

    private static MathVisual Layout(MathNode node, Context context, double size) => node switch
    {
        MathRow row => LayoutRow(row.Items, context, size),
        MathSymbol symbol => Glyph(context, symbol.Text, size, symbol.Kind),
        MathSpace space => Space(space.Ems * size),
        MathFraction fraction => LayoutFraction(fraction, context, size),
        MathRadical radical => LayoutRadical(radical, context, size),
        MathScripts scripts => LayoutScripts(scripts, context, size),
        MathFenced fenced => LayoutFenced(fenced, context, size),
        MathAccent accent => LayoutAccent(accent, context, size),
        MathStyled styled => LayoutStyled(styled, context, size),
        MathMatrix matrix => LayoutMatrix(matrix, context, size),
        MathLines lines => LayoutLines(lines, context, size),
        _ => Space(0)
    };

    private static MathVisual LayoutRow(IReadOnlyList<MathNode> items, Context context, double size)
    {
        if (items.Count == 0)
        {
            return Space(0);
        }

        var boxes = new List<MathVisual>(items.Count);
        foreach (var item in items)
        {
            boxes.Add(Layout(item, context, size));
        }

        var children = new List<(FrameworkElement, double, double)>(boxes.Count);
        var ascent = 0.0;
        var descent = 0.0;
        foreach (var box in boxes)
        {
            ascent = Math.Max(ascent, box.Ascent);
            descent = Math.Max(descent, box.Descent);
        }

        var x = 0.0;
        MathVisual? previous = null;
        foreach (var box in boxes)
        {
            x += Gap(previous, box, size);
            children.Add((box.Element, x, ascent - box.Ascent));
            x += box.Width;
            previous = box;
        }

        var kind = boxes.Count == 1 ? boxes[0].Kind : MathTokenKind.Variable;
        return Compose(Math.Max(x, 0), ascent, descent, children, kind);
    }

    /// <summary>
    /// Воздух между соседями. Знаки операций и отношений в формуле стоят свободнее, чем буквы, —
    /// без этого «a+b=c» слипается в одно слово.
    /// </summary>
    private static double Gap(MathVisual? previous, MathVisual next, double size)
    {
        if (previous is null)
        {
            return 0;
        }

        if (previous.Kind == MathTokenKind.Relation || next.Kind == MathTokenKind.Relation)
        {
            return size * 0.26;
        }

        if (previous.Kind == MathTokenKind.Binary || next.Kind == MathTokenKind.Binary)
        {
            return size * 0.20;
        }

        if (previous.Kind == MathTokenKind.Punctuation)
        {
            return size * 0.14;
        }

        if (previous.Kind == MathTokenKind.BigOperator || previous.Kind == MathTokenKind.Upright)
        {
            return size * 0.12;
        }

        return 0;
    }

    private static MathVisual LayoutFraction(MathFraction fraction, Context context, double size)
    {
        // В строке текста дробь набирается мельче: иначе одна «1/2» распирает строку вдвое.
        var inner = context.Display ? size : size * 0.78;
        var numerator = Layout(fraction.Numerator, context, inner);
        var denominator = Layout(fraction.Denominator, context, inner);

        var rule = fraction.Line ? Math.Max(1, size * 0.055) : 0;
        var axis = size * 0.28;
        var gap = size * 0.16;
        var pad = size * 0.14;

        var width = Math.Max(numerator.Width, denominator.Width) + pad * 2;
        var ascent = axis + rule / 2 + gap + numerator.Height;
        var descent = Math.Max(0, gap + rule / 2 + denominator.Height - axis);

        var children = new List<(FrameworkElement, double, double)>
        {
            (numerator.Element, (width - numerator.Width) / 2, 0),
            (denominator.Element, (width - denominator.Width) / 2, ascent - axis + rule / 2 + gap)
        };

        if (fraction.Line)
        {
            var bar = new Rectangle { Width = width, Height = rule };
            bar.SetResourceReference(Shape.FillProperty, context.BrushKey);
            children.Add((bar, 0, ascent - axis - rule / 2));
        }

        return Compose(width, ascent, descent, children);
    }

    private static MathVisual LayoutRadical(MathRadical radical, Context context, double size)
    {
        var body = Layout(radical.Body, context, size);
        var stroke = Math.Max(1, size * 0.06);
        var gapTop = size * 0.16;
        var pad = size * 0.1;

        var height = body.Height + gapTop + stroke;
        var hook = size * 0.55;

        var index = radical.Index is null ? null : Layout(radical.Index, context, size * 0.6);
        var indexWidth = index is null ? 0 : Math.Max(0, index.Width - hook * 0.35);

        var width = indexWidth + hook + body.Width + pad * 2;
        var ascent = body.Ascent + gapTop + stroke;
        var descent = body.Descent;

        var sign = new Path
        {
            StrokeThickness = stroke,
            StrokeLineJoin = PenLineJoin.Miter,
            StrokeStartLineCap = PenLineCap.Round,
            Data = Geometry.Parse(string.Format(
                CultureInfo.InvariantCulture,
                "M {0},{1} L {2},{3} L {4},{5} L {6},{7} L {8},{7}",
                indexWidth, height * 0.58,
                indexWidth + hook * 0.28, height * 0.5,
                indexWidth + hook * 0.55, height - stroke / 2,
                indexWidth + hook * 0.88, stroke / 2,
                width - pad * 0.4)),
            Width = width,
            Height = height
        };
        sign.SetResourceReference(Shape.StrokeProperty, context.BrushKey);

        var children = new List<(FrameworkElement, double, double)>
        {
            (sign, 0, 0),
            (body.Element, indexWidth + hook + pad, gapTop + stroke)
        };

        if (index is not null)
        {
            // Показатель корня садится над изломом радикала, а не рядом с ним.
            children.Add((index.Element, 0, Math.Max(0, height * 0.42 - index.Height)));
        }

        return Compose(width, ascent, descent, children);
    }

    private static MathVisual LayoutScripts(MathScripts scripts, Context context, double size)
    {
        var body = Layout(scripts.Base, context, size);
        var scriptSize = Math.Max(size * 0.7, 7.5);
        var sub = scripts.Sub is null ? null : Layout(scripts.Sub, context, scriptSize);
        var sup = scripts.Sup is null ? null : Layout(scripts.Sup, context, scriptSize);

        // Пределы над и под знаком — только в выключной формуле: в строке текста ∑ с
        // этажами сверху и снизу разрывает межстрочный интервал.
        if (scripts.Limits && context.Display)
        {
            return StackedLimits(body, sub, sup, size);
        }

        var width = body.Width;
        var ascent = body.Ascent;
        var descent = body.Descent;
        var children = new List<(FrameworkElement, double, double)> { (body.Element, 0, 0) };

        var supRaise = Math.Max(size * 0.42, body.Ascent - scriptSize * 0.55);
        var subDrop = Math.Max(size * 0.2, body.Descent + scriptSize * 0.12);

        if (sup is not null)
        {
            ascent = Math.Max(ascent, supRaise + sup.Ascent);
            width = Math.Max(width, body.Width + sup.Width + size * 0.06);
        }

        if (sub is not null)
        {
            descent = Math.Max(descent, subDrop + sub.Descent);
            width = Math.Max(width, body.Width + sub.Width + size * 0.06);
        }

        // Вертикали считаются от подъёма всей коробки, поэтому дети размещаются после него.
        var top = ascent - body.Ascent;
        children[0] = (body.Element, 0, top);

        if (sup is not null)
        {
            children.Add((sup.Element, body.Width + size * 0.04, ascent - supRaise - sup.Ascent));
        }

        if (sub is not null)
        {
            children.Add((sub.Element, body.Width + size * 0.04, ascent + subDrop - sub.Ascent));
        }

        return Compose(width, ascent, descent, children, body.Kind);
    }

    private static MathVisual StackedLimits(MathVisual body, MathVisual? sub, MathVisual? sup, double size)
    {
        var gap = size * 0.14;
        var width = body.Width;
        if (sup is not null)
        {
            width = Math.Max(width, sup.Width);
        }

        if (sub is not null)
        {
            width = Math.Max(width, sub.Width);
        }

        var above = sup is null ? 0 : sup.Height + gap;
        var below = sub is null ? 0 : sub.Height + gap;

        var ascent = body.Ascent + above;
        var descent = body.Descent + below;

        var children = new List<(FrameworkElement, double, double)>
        {
            (body.Element, (width - body.Width) / 2, above)
        };

        if (sup is not null)
        {
            children.Add((sup.Element, (width - sup.Width) / 2, 0));
        }

        if (sub is not null)
        {
            children.Add((sub.Element, (width - sub.Width) / 2, above + body.Height + gap));
        }

        return Compose(width, ascent, descent, children, MathTokenKind.BigOperator);
    }

    private static MathVisual LayoutFenced(MathFenced fenced, Context context, double size)
    {
        var body = Layout(fenced.Body, context, size);
        var axis = size * 0.28;

        // Скобка симметрична относительно математической оси, а не базовой линии.
        var half = Math.Max(body.Ascent - axis, body.Descent + axis) + size * 0.12;
        var height = Math.Max(half * 2, size * 0.9);

        var left = MathDelimiter.Create(fenced.Left, height, size, context.BrushKey, opening: true);
        var right = MathDelimiter.Create(fenced.Right, height, size, context.BrushKey, opening: false);

        var leftWidth = left?.Width ?? 0;
        var rightWidth = right?.Width ?? 0;
        var width = leftWidth + body.Width + rightWidth;

        var ascent = Math.Max(body.Ascent, height / 2 + axis);
        var descent = Math.Max(body.Descent, height / 2 - axis);
        var fenceTop = ascent - height / 2 - axis;

        var children = new List<(FrameworkElement, double, double)>
        {
            (body.Element, leftWidth, ascent - body.Ascent)
        };

        if (left is not null)
        {
            children.Add((left, 0, fenceTop));
        }

        if (right is not null)
        {
            children.Add((right, leftWidth + body.Width, fenceTop));
        }

        return Compose(width, ascent, descent, children);
    }

    private static MathVisual LayoutAccent(MathAccent accent, Context context, double size)
    {
        var body = Layout(accent.Body, context, size);
        var stroke = Math.Max(1, size * 0.055);
        var gap = size * 0.06;

        if (accent.Kind == MathAccentKind.Underline)
        {
            var line = new Rectangle { Width = body.Width, Height = stroke };
            line.SetResourceReference(Shape.FillProperty, context.BrushKey);
            var descentBelow = body.Descent + gap + stroke;
            return Compose(
                body.Width,
                body.Ascent,
                descentBelow,
                [
                    (body.Element, 0, 0),
                    (line, 0, body.Height + gap)
                ]);
        }

        var mark = MathDelimiter.CreateAccent(accent.Kind, body.Width, size, context.BrushKey);
        var markHeight = mark?.Height ?? 0;
        var ascent = body.Ascent + gap + markHeight;

        var children = new List<(FrameworkElement, double, double)>
        {
            (body.Element, 0, gap + markHeight)
        };

        if (mark is not null)
        {
            children.Add((mark, (body.Width - mark.Width) / 2, 0));
        }

        return Compose(body.Width, ascent, body.Descent, children);
    }

    private static MathVisual LayoutStyled(MathStyled styled, Context context, double size)
    {
        if (styled.Body is MathSymbol symbol)
        {
            return Glyph(context, MathAlphabet.Convert(symbol.Text, styled.Style), size, symbol.Kind, styled.Style);
        }

        if (styled.Body is MathRow row)
        {
            var items = row.Items.Select(item => (MathNode)new MathStyled(item, styled.Style)).ToList();
            return LayoutRow(items, context, size);
        }

        return Layout(styled.Body, context, size);
    }

    private static MathVisual LayoutMatrix(MathMatrix matrix, Context context, double size)
    {
        if (matrix.Rows.Count == 0)
        {
            return Space(0);
        }

        var columns = matrix.Rows.Max(row => row.Count);
        var cells = new MathVisual[matrix.Rows.Count][];
        for (var r = 0; r < matrix.Rows.Count; r++)
        {
            cells[r] = new MathVisual[columns];
            for (var c = 0; c < columns; c++)
            {
                cells[r][c] = c < matrix.Rows[r].Count
                    ? Layout(matrix.Rows[r][c], context, size)
                    : Space(0);
            }
        }

        var columnWidths = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            for (var r = 0; r < cells.Length; r++)
            {
                columnWidths[c] = Math.Max(columnWidths[c], cells[r][c].Width);
            }
        }

        var columnGap = size * 0.7;
        var rowGap = size * 0.35;

        var rowAscents = new double[cells.Length];
        var rowDescents = new double[cells.Length];
        for (var r = 0; r < cells.Length; r++)
        {
            foreach (var cell in cells[r])
            {
                rowAscents[r] = Math.Max(rowAscents[r], cell.Ascent);
                rowDescents[r] = Math.Max(rowDescents[r], cell.Descent);
            }
        }

        var bodyWidth = columnWidths.Sum() + columnGap * Math.Max(0, columns - 1);
        var bodyHeight = rowAscents.Sum() + rowDescents.Sum() + rowGap * Math.Max(0, cells.Length - 1);

        var children = new List<(FrameworkElement, double, double)>();
        var y = 0.0;
        for (var r = 0; r < cells.Length; r++)
        {
            var x = 0.0;
            for (var c = 0; c < columns; c++)
            {
                var cell = cells[r][c];
                var offset = matrix.LeftAligned ? 0 : (columnWidths[c] - cell.Width) / 2;
                children.Add((cell.Element, x + offset, y + rowAscents[r] - cell.Ascent));
                x += columnWidths[c] + columnGap;
            }

            y += rowAscents[r] + rowDescents[r] + rowGap;
        }

        // Таблица центрируется по математической оси — так её видит окружающая строка.
        var axis = size * 0.28;
        var body = Compose(bodyWidth, bodyHeight / 2 + axis, bodyHeight / 2 - axis, children);

        return matrix.Left.Length == 0 && matrix.Right.Length == 0
            ? body
            : WrapWithFences(body, matrix.Left, matrix.Right, context, size);
    }

    private static MathVisual WrapWithFences(
        MathVisual body,
        string leftGlyph,
        string rightGlyph,
        Context context,
        double size)
    {
        var axis = size * 0.28;
        var half = Math.Max(body.Ascent - axis, body.Descent + axis) + size * 0.1;
        var height = half * 2;

        var left = MathDelimiter.Create(leftGlyph, height, size, context.BrushKey, opening: true);
        var right = MathDelimiter.Create(rightGlyph, height, size, context.BrushKey, opening: false);
        var leftWidth = left?.Width ?? 0;
        var rightWidth = right?.Width ?? 0;

        var ascent = Math.Max(body.Ascent, height / 2 + axis);
        var descent = Math.Max(body.Descent, height / 2 - axis);
        var top = ascent - height / 2 - axis;

        var children = new List<(FrameworkElement, double, double)>
        {
            (body.Element, leftWidth, ascent - body.Ascent)
        };

        if (left is not null)
        {
            children.Add((left, 0, top));
        }

        if (right is not null)
        {
            children.Add((right, leftWidth + body.Width, top));
        }

        return Compose(leftWidth + body.Width + rightWidth, ascent, descent, children);
    }

    private static MathVisual LayoutLines(MathLines lines, Context context, double size)
    {
        var rows = lines.Rows.Select(row => (IReadOnlyList<MathNode>)new[] { row }).ToList();
        return LayoutMatrix(new MathMatrix(rows, "", "", LeftAligned: false), context, size);
    }

    // ───────────────────────── примитивы ─────────────────────────

    private static MathVisual Glyph(
        Context context,
        string text,
        double size,
        MathTokenKind kind,
        MathStyleKind? style = null)
    {
        var italic = style switch
        {
            MathStyleKind.Bold or MathStyleKind.Roman or MathStyleKind.Blackboard => false,
            MathStyleKind.Italic or MathStyleKind.Calligraphic => true,
            _ => kind == MathTokenKind.Variable && text.Length == 1 && char.IsLetter(text[0])
        };

        var block = new TextBlock
        {
            Text = text,
            FontFamily = context.Font,
            FontSize = size,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            FontWeight = style == MathStyleKind.Bold ? FontWeights.Bold : FontWeights.Normal,
            TextWrapping = TextWrapping.NoWrap,
            SnapsToDevicePixels = true
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, context.BrushKey);

        // Крупный оператор в выключной формуле рисуется в полтора раза больше — как в наборе.
        if (kind == MathTokenKind.BigOperator && context.Display)
        {
            block.FontSize = size * 1.45;
        }

        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = block.DesiredSize.Width;
        var height = block.DesiredSize.Height;
        var ascent = Math.Min(height, block.FontSize * SafeBaseline(context.Font));

        return new MathVisual(block, width, ascent, height - ascent, kind);
    }

    private static double SafeBaseline(FontFamily family)
    {
        var baseline = family.Baseline;
        return baseline is > 0.1 and < 2 ? baseline : 0.9;
    }

    private static MathVisual Space(double width) =>
        new(new Border { Width = Math.Max(0, width), Height = 0 }, Math.Max(0, width), 0, 0);

    private static MathVisual Compose(
        double width,
        double ascent,
        double descent,
        IReadOnlyList<(FrameworkElement Element, double X, double Y)> children,
        MathTokenKind kind = MathTokenKind.Variable)
    {
        var canvas = new Canvas
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, ascent + descent)
        };

        foreach (var (element, x, y) in children)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            canvas.Children.Add(element);
        }

        return new MathVisual(canvas, canvas.Width, ascent, descent, kind);
    }
}
