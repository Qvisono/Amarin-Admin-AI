using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Точки «внизу идёт работа» на кнопке «В конец чата» (1.37.0-beta.9): фигуры, стыки, разнообразие.
/// </summary>
public sealed class WorkDotsTests
{
    private const double Room = WorkDots.Spacing + 1e-6;

    private static readonly double[] Row = [-WorkDots.Spacing, 0, WorkDots.Spacing];

    /// <summary>Каждая фигура в каждом варианте.</summary>
    private static IEnumerable<WorkDots.Move> AllMoves() =>
        from figure in Enum.GetValues<WorkDots.Figure>()
        from direction in new[] { -1, 1 }
        from side in new[] { -1, 1 }
        from pair in new[] { 0, 1 }
        select new WorkDots.Move(figure, direction, side, pair, TimeSpan.FromMilliseconds(500), TimeSpan.Zero);

    private static int[][] Orders() => [[0, 1, 2], [2, 0, 1], [1, 2, 0], [2, 1, 0]];

    /// <summary>Где стоит каждая точка, если ряд расставлен по <paramref name="order"/>.</summary>
    private static double[] RowOf(int[] order)
    {
        var x = new double[3];
        for (var slot = 0; slot < 3; slot++)
        {
            x[order[slot]] = Row[slot];
        }

        return x;
    }

    private static void AssertRow(Point[] points, int[] order)
    {
        var expected = RowOf(order);
        for (var dot = 0; dot < 3; dot++)
        {
            Assert.Equal(expected[dot], points[dot].X, 6);
            Assert.Equal(0, points[dot].Y, 6);
        }
    }

    [Fact]
    public void Every_figure_starts_and_ends_in_an_even_row_in_the_order_it_promises()
    {
        foreach (var move in AllMoves())
        {
            foreach (var order in Orders())
            {
                AssertRow(WorkDots.Place(move, order, 0), order);
                AssertRow(WorkDots.Place(move, order, 1), WorkDots.After(move, order));
            }
        }
    }

    [Fact]
    public void Every_figure_moves_and_stays_inside_the_button()
    {
        foreach (var move in AllMoves())
        {
            var farthest = 0.0;
            for (var step = 0; step <= 100; step++)
            {
                var points = WorkDots.Place(move, [0, 1, 2], step / 100.0);
                var row = RowOf([0, 1, 2]);
                for (var dot = 0; dot < 3; dot++)
                {
                    Assert.InRange(points[dot].X, -Room, Room);
                    Assert.InRange(points[dot].Y, -Room, Room);
                    farthest = Math.Max(farthest, Math.Abs(points[dot].Y) + Math.Abs(points[dot].X - row[dot]));
                }
            }

            Assert.True(farthest > 2, $"{move.Figure} hardly moves");
        }
    }

    /// <summary>
    /// Первый треугольник смешивал ряд с уже повёрнутой фигурой, и на середине хода три точки
    /// сходились в одну.
    /// </summary>
    [Fact]
    public void Dots_never_run_into_each_other()
    {
        foreach (var move in AllMoves())
        {
            for (var step = 0; step <= 200; step++)
            {
                var points = WorkDots.Place(move, [0, 1, 2], step / 200.0);
                for (var a = 0; a < 3; a++)
                {
                    for (var b = a + 1; b < 3; b++)
                    {
                        Assert.True((points[a] - points[b]).Length >= 4,
                            $"{move.Figure} at {step / 200.0}: dots {a} and {b} overlap");
                    }
                }
            }
        }
    }

    [Fact]
    public void The_next_figure_is_never_the_same_twice_in_a_row_and_all_of_them_come_up()
    {
        var random = new Random(37);
        var seen = new HashSet<WorkDots.Figure>();
        WorkDots.Figure? previous = null;
        for (var i = 0; i < 500; i++)
        {
            var move = WorkDots.NextMove(previous, random);
            Assert.NotEqual(previous, move.Figure);
            Assert.InRange(move.Rest.TotalMilliseconds, 120, 600);
            Assert.InRange(move.Duration.TotalMilliseconds, 400, 1300);
            seen.Add(move.Figure);
            previous = move.Figure;
        }

        Assert.Equal(Enum.GetValues<WorkDots.Figure>().Length, seen.Count);
    }

    [Fact]
    public void Figures_join_without_a_jump()
    {
        var random = new Random(9);
        int[] order = [0, 1, 2];
        var move = WorkDots.NextMove(null, random);
        for (var i = 0; i < 50; i++)
        {
            var end = WorkDots.Place(move, order, 1);
            order = WorkDots.After(move, order);
            move = WorkDots.NextMove(move.Figure, random);
            var start = WorkDots.Place(move, order, 0);
            for (var dot = 0; dot < 3; dot++)
            {
                Assert.Equal(end[dot].X, start[dot].X, 6);
                Assert.Equal(end[dot].Y, start[dot].Y, 6);
            }
        }
    }
}

/// <summary>Кнопка «В конец чата» показывает точки, пока в открытом чате идёт ход.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ScrollBottomWorkTests
{
    private readonly WpfFixture _wpf;

    public ScrollBottomWorkTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void While_the_open_chat_answers_the_to_the_end_button_runs_dots_instead_of_the_arrow()
    {
        var (idle, working, atBottom, finished, otherChat) = _wpf.Ui.Invoke(() =>
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var field = typeof(MainWindow).GetField("_session", hidden)!;
            var original = field.GetValue(window);
            void Call(string name) => typeof(MainWindow).GetMethod(name, hidden)!.Invoke(window, null);

            var icon = (FrameworkElement)window.FindSetting("ScrollBottomIcon")!;
            var dots = (WorkDots)window.FindSetting("ScrollBottomDots")!;
            var viewer = (ScrollViewer)window.FindSetting("ChatScrollViewer")!;
            (bool Arrow, bool Dots, bool Active) State()
            {
                window.UpdateLayout();
                Call("UpdateScrollJump");
                return (icon.Visibility == Visibility.Visible, dots.Visibility == Visibility.Visible, dots.Active);
            }

            RunningTurn? turn = null;
            try
            {
                var chat = new ChatSession { Id = "work-dots", Title = "Длинный разговор" };
                for (var i = 0; i < 40; i++)
                {
                    chat.Messages.Add(new ChatDisplayMessage
                    {
                        Role = i % 2 == 0 ? "user" : "assistant",
                        Id = "m" + i,
                        Text = string.Join(' ', Enumerable.Repeat("слово", 120))
                    });
                }

                field.SetValue(window, chat);
                Call("RenderSession");
                window.UpdateLayout();
                viewer.ScrollToTop();
                var before = State();

                turn = window.Turns.TryStart(chat, TurnKind.Send, DateTime.Now).Turn!;
                window.UpdateScrollBottomWork();
                var during = State();

                viewer.ScrollToEnd();
                var bottom = State();
                viewer.ScrollToTop();
                _ = State();

                // Ход идёт в одном чате, а открыт другой: внизу этого чата работы нет.
                field.SetValue(window, new ChatSession { Id = "work-dots-other", Title = "Другой" });
                Call("RenderSession");
                var elsewhere = State();

                field.SetValue(window, chat);
                Call("RenderSession");
                window.FinishTurn(turn);
                turn = null;
                viewer.ScrollToTop();
                return (before, during, bottom, State(), elsewhere);
            }
            finally
            {
                if (turn is not null)
                {
                    window.FinishTurn(turn);
                }

                field.SetValue(window, original);
                Call("RenderSession");
            }
        });

        Assert.Equal((true, false, false), idle);
        Assert.Equal((false, true, true), working);
        Assert.Equal((false, true, false), atBottom);
        Assert.Equal((true, false, false), finished);
        Assert.Equal((true, false, false), otherChat);
    }
}
