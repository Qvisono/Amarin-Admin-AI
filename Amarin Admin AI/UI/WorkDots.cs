using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Amarin.UI;

/// <summary>
/// Три точки, которые играют фигурами: значок кнопки «В конец чата», пока внизу идёт ответ
/// (1.37.0-beta.9).
/// </summary>
/// <remarks>
/// <para>
/// Первая версия повторяла одно движение по кругу, и человек назвал его однообразным. Теперь точки
/// делают фигуры (<see cref="Figure"/>) по одной: следующая выбирается случайно и не повторяет
/// предыдущую, у каждой свой случайный вариант, темп и пауза перед ней. Любая фигура начинается и
/// кончается ровным рядом, поэтому любые две стыкуются без рывка.
/// </para>
/// <para>
/// Положение точек считают чистые <see cref="Place"/>, <see cref="After"/> и <see cref="NextMove"/>:
/// движение проверяется тестом без окна. На каждую фигуру — одна анимация хода 0→1; анимация идёт,
/// только пока точки нужны (<see cref="Active"/>) и видны, а без анимаций точки стоят рядом.
/// </para>
/// </remarks>
public sealed class WorkDots : FrameworkElement
{
    /// <summary>Расстояние между центрами соседних точек в ряду.</summary>
    internal const double Spacing = 6;

    /// <summary>Насколько далеко от ряда точки уходят вверх и вниз.</summary>
    internal const double Lift = 6;

    private const double Diameter = 4;

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(WorkDots),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(WorkDots),
        new PropertyMetadata(false, (d, _) => ((WorkDots)d).Animate()));

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(WorkDots),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Какая точка в каком месте ряда: индекс — место слева направо, значение — точка.</summary>
    private int[] _order = [0, 1, 2];

    private Move? _move;
    private bool _running;

    /// <summary>Номер прогона: <c>Completed</c> снятой анимации не должен заводить следующую фигуру.</summary>
    private int _generation;

    public WorkDots()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Animate();
    }

    /// <summary>Фигуры точек.</summary>
    internal enum Figure
    {
        /// <summary>Крайняя точка перелетает над или под рядом на другой край, остальные сдвигаются.</summary>
        Hop,

        /// <summary>Крайние меняются местами вокруг средней: одна поверху, другая понизу.</summary>
        SwapEnds,

        /// <summary>Ряд делает полный оборот вокруг средней точки.</summary>
        Spin,

        /// <summary>Ряд складывается в треугольник, тот поворачивается и раскладывается обратно.</summary>
        Triangle,

        /// <summary>Волна бежит вдоль ряда.</summary>
        Wave,

        /// <summary>Две соседние точки меняются местами по кругу.</summary>
        Pair,

        /// <summary>Средняя подпрыгивает, крайние подаются к ней.</summary>
        Bounce
    }

    /// <summary>Фигура с вариантом и темпом.</summary>
    /// <param name="Direction">1 — слева направо (или по часовой), −1 — наоборот.</param>
    /// <param name="Side">−1 — вверх, 1 — вниз (Y в WPF растёт вниз).</param>
    /// <param name="Pair">Левая пара (0) или правая (1) у <see cref="Figure.Pair"/>.</param>
    internal readonly record struct Move(Figure Figure, int Direction, int Side, int Pair, TimeSpan Duration, TimeSpan Rest);

    /// <summary>Цвет точек.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Играть фигурами или стоять рядом.</summary>
    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    /// <summary>Идёт ли сейчас анимация. Отдельно ради тестов.</summary>
    internal bool IsRunning => _running;

    /// <summary>Следующая фигура: случайная, но не та же, что была; случайный вариант, темп и пауза.</summary>
    internal static Move NextMove(Figure? previous, Random random)
    {
        var all = Enum.GetValues<Figure>();
        Figure figure;
        do
        {
            figure = all[random.Next(all.Length)];
        }
        while (figure == previous);

        var duration = BaseDuration(figure) * (0.85 + (random.NextDouble() * 0.3));
        var rest = TimeSpan.FromMilliseconds(120 + (random.NextDouble() * 480));
        return new Move(
            figure,
            random.Next(2) == 0 ? 1 : -1,
            random.Next(2) == 0 ? -1 : 1,
            random.Next(2),
            duration,
            rest);
    }

    private static TimeSpan BaseDuration(Figure figure) => TimeSpan.FromMilliseconds(figure switch
    {
        Figure.Hop => 520,
        Figure.SwapEnds => 620,
        Figure.Spin => 900,
        Figure.Triangle => 1100,
        Figure.Wave => 720,
        Figure.Pair => 480,
        _ => 560
    });

    /// <summary>Порядок точек в ряду после фигуры: перелёт и обмены переставляют их.</summary>
    internal static int[] After(Move move, int[] order)
    {
        // Куда переезжает точка с каждого места.
        int[] to = move.Figure switch
        {
            Figure.Hop => move.Direction > 0 ? [2, 0, 1] : [1, 2, 0],
            Figure.SwapEnds => [2, 1, 0],
            Figure.Pair => move.Pair == 0 ? [1, 0, 2] : [0, 2, 1],
            _ => [0, 1, 2]
        };

        var next = new int[3];
        for (var slot = 0; slot < 3; slot++)
        {
            next[to[slot]] = order[slot];
        }

        return next;
    }

    /// <summary>
    /// Смещения трёх точек от центра (индекс — точка) при ходе фигуры <paramref name="progress"/> от 0
    /// до 1. Y вниз положителен, как в WPF.
    /// </summary>
    internal static Point[] Place(Move move, int[] order, double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        var points = new Point[3];
        for (var slot = 0; slot < 3; slot++)
        {
            points[order[slot]] = move.Figure switch
            {
                Figure.Wave => WaveAt(move, slot, t),
                Figure.Triangle => TriangleAt(move, slot, t),
                _ => At(move, slot, Ease(t))
            };
        }

        return points;
    }

    private static double SlotX(int slot) => (slot - 1) * Spacing;

    private static Point At(Move move, int slot, double u)
    {
        var arc = Math.Sin(Math.PI * u);
        var turn = Math.Cos(Math.PI * u);
        switch (move.Figure)
        {
            case Figure.Hop:
                {
                    var from = move.Direction > 0 ? 0 : 2;
                    return slot == from
                        ? new Point(-move.Direction * Spacing * turn, move.Side * Lift * arc)
                        : new Point(SlotX(slot) - (move.Direction * Spacing * u), 0);
                }

            case Figure.SwapEnds:
                if (slot == 1)
                {
                    return new Point(0, 0);
                }

                return slot == 0
                    ? new Point(-Spacing * turn, move.Side * Lift * arc)
                    : new Point(Spacing * turn, -move.Side * Lift * arc);

            case Figure.Spin:
                {
                    if (slot == 1)
                    {
                        return new Point(0, 0);
                    }

                    var angle = (slot == 0 ? Math.PI : 0) + (move.Direction * 2 * Math.PI * u);
                    return new Point(Spacing * Math.Cos(angle), Spacing * Math.Sin(angle));
                }

            case Figure.Pair:
                {
                    var first = move.Pair;
                    if (slot != first && slot != first + 1)
                    {
                        return new Point(SlotX(slot), 0);
                    }

                    var middle = (SlotX(first) + SlotX(first + 1)) / 2;
                    var sign = slot == first ? -1 : 1;
                    return new Point(
                        middle + (sign * (Spacing / 2) * turn),
                        (slot == first ? move.Side : -move.Side) * Lift * 0.8 * arc);
                }

            default:
                return slot == 1
                    ? new Point(0, move.Side * Lift * arc)
                    : new Point(SlotX(slot) * (1 - (0.4 * arc)), 0);
        }
    }

    /// <summary>У волны у каждой точки свой подъём, со сдвигом по очереди.</summary>
    private static Point WaveAt(Move move, int slot, double t)
    {
        var turn = move.Direction > 0 ? slot : 2 - slot;
        var own = Math.Clamp((t - (turn * 0.18)) / 0.64, 0, 1);
        return new Point(SlotX(slot), move.Side * Lift * 0.85 * Math.Sin(Math.PI * Ease(own)));
    }

    /// <summary>
    /// Треугольник в три такта: ряд складывается в него, он делает полный оборот, раскладывается
    /// обратно. Поворот идёт только сложенным: смешай ряд с уже повёрнутым треугольником — и на
    /// середине хода три точки сходились в одну.
    /// </summary>
    private static Point TriangleAt(Move move, int slot, double t)
    {
        const double radius = 5.5;
        const double fold = 0.25;
        var weight = t < fold ? Ease(t / fold) : t > 1 - fold ? Ease((1 - t) / fold) : 1;
        var spin = Ease(Math.Clamp((t - fold) / (1 - (2 * fold)), 0, 1));

        // Вершины через 120°, средняя точка — вверху.
        var angle = (((150 + (slot * 120)) * Math.PI) / 180) + (move.Direction * 2 * Math.PI * spin);
        var x = radius * Math.Cos(angle);
        var y = radius * Math.Sin(angle);
        return new Point(SlotX(slot) + ((x - SlotX(slot)) * weight), y * weight);
    }

    /// <summary>Плавный разгон и торможение.</summary>
    private static double Ease(double u) =>
        u < 0.5 ? 4 * u * u * u : 1 - (Math.Pow((-2 * u) + 2, 3) / 2);

    protected override Size MeasureOverride(Size availableSize) =>
        new((Spacing * 2) + Diameter, (Lift * 2) + Diameter);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Fill is not { } fill)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var points = _move is { } move
            ? Place(move, _order, (double)GetValue(ProgressProperty))
            : [new Point(-Spacing, 0), new Point(0, 0), new Point(Spacing, 0)];
        foreach (var offset in points)
        {
            drawingContext.DrawEllipse(fill, null, center + (Vector)offset, Diameter / 2, Diameter / 2);
        }
    }

    private void Animate()
    {
        var run = Active && IsVisible && UiMotion.Enabled;
        if (run == _running)
        {
            return;
        }

        _running = run;
        _generation++;
        if (run)
        {
            Play(NextMove(null, Random.Shared));
            return;
        }

        // Стоя точки — ровный ряд, а прерванная фигура могла держать их где угодно.
        _move = null;
        BeginAnimation(ProgressProperty, null);
        InvalidateVisual();
    }

    private void Play(Move move)
    {
        _move = move;
        var generation = _generation;
        var animation = new DoubleAnimation(0, 1, move.Duration) { BeginTime = move.Rest };
        animation.Completed += (_, _) =>
        {
            if (generation != _generation || _move is not { } done)
            {
                return;
            }

            _order = After(done, _order);
            Play(NextMove(done.Figure, Random.Shared));
        };

        BeginAnimation(ProgressProperty, animation);
    }
}
