using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Страница «Key &amp; Info» и график трат на живом WPF.
/// </summary>
/// <remarks>
/// График рисуется в <c>DrawingVisual</c>, а не элементами разметки, поэтому пересчитать его
/// глазами нельзя, а исключение внутри отрисовки не уронит окно — оно просто оставит пустое
/// место. Отсюда и проверки снимком.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class SettingsKeyPageTests
{
    private readonly WpfFixture _wpf;

    public SettingsKeyPageTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    /// <summary>
    /// Раздел «Key» стоит между «Model» и «Data & Info» — там, где его и ждут.
    /// </summary>
    [Fact]
    public void The_key_section_sits_below_the_model_one()
    {
        var order = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var nav = (Panel)VisualParentOf((UIElement)window.FindName("NavKey"));
            return nav.Children.OfType<FrameworkElement>()
                .Select(child => child.Name)
                .Where(name => name.Length > 0)
                .ToList();
        });

        var customize = order.IndexOf("NavCustomize");
        var key = order.IndexOf("NavKey");
        var data = order.IndexOf("NavData");

        Assert.True(customize < key, "раздел Key обязан идти после Customize");
        Assert.True(key < data, "раздел Key обязан идти до Data Controls");
    }

    private static DependencyObject VisualParentOf(UIElement element) =>
        LogicalTreeHelper.GetParent(element);

    /// <summary>Выбор пункта показывает ровно одну страницу — по образцу проверки Info.</summary>
    [Fact]
    public void Choosing_the_page_shows_exactly_one()
    {
        var visible = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var nav = (RadioButton)window.FindName("NavKey");
            var previous = new[] { "NavAccount", "NavAppearance", "NavBehavior", "NavCustomize", "NavInstructions", "NavData", "NavInfo" }
                .Select(name => (RadioButton)window.FindName(name))
                .FirstOrDefault(button => button.IsChecked == true);

            nav.IsChecked = true;
            window.UpdateLayout();
            try
            {
                return new[]
                {
                    "AppearancePageScroll", "BehaviorPageScroll", "CustomizePageScroll", "DataPageScroll"
                }
                .Select(name => ((FrameworkElement)window.FindName(name)).Visibility)
                .Count(state => state == Visibility.Visible)
                + (((FrameworkElement)window.FindName("KeyPage")).Visibility == Visibility.Visible ? 1 : 0)
                + (((FrameworkElement)window.FindName("InstructionsPage")).Visibility == Visibility.Visible ? 1 : 0);
            }
            finally
            {
                if (previous is not null)
                {
                    previous.IsChecked = true;
                }
            }
        });

        Assert.Equal(1, visible);
    }

    /// <summary>
    /// Страница собирается без исключения: она копирует шесть общих стилей настроек к себе,
    /// и опечатка в ключе повалила бы её разбор целиком.
    /// </summary>
    [Fact]
    public void The_page_builds_and_finds_all_its_styles() =>
        _wpf.Ui.Invoke(() =>
        {
            var page = new SettingsKeyPage();
            page.Measure(new Size(520, 460));
            page.Arrange(new Rect(0, 0, 520, 460));
            page.UpdateLayout();
            return true;
        });

    [Fact]
    public void Both_key_dialogs_start_hidden()
    {
        var (add, remove) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            return (((FrameworkElement)window.FindName("KeyOverlay")).Visibility,
                    ((FrameworkElement)window.FindName("KeyRemoveOverlay")).Visibility);
        });

        Assert.Equal(Visibility.Collapsed, add);
        Assert.Equal(Visibility.Collapsed, remove);
    }

    /// <summary>Подтверждение удаления лежит поверх модалки добавления, а та — поверх настроек.</summary>
    [Fact]
    public void The_key_dialogs_stack_above_everything_else()
    {
        var (add, remove, preset) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            return (Panel.GetZIndex((UIElement)window.FindName("KeyOverlay")),
                    Panel.GetZIndex((UIElement)window.FindName("KeyRemoveOverlay")),
                    Panel.GetZIndex((UIElement)window.FindName("PromptPresetOverlay")));
        });

        Assert.True(add > preset);
        Assert.True(remove > add);
    }

    /// <summary>Заводской отрезок — неделя, ровно как просили.</summary>
    [Fact]
    public void The_default_period_is_a_week() =>
        Assert.True(_wpf.Ui.Invoke(() =>
        {
            var page = new SettingsKeyPage();
            return ((RadioButton)page.FindName("PeriodWeek")).IsChecked;
        }));

    [Fact]
    public void All_six_periods_are_offered()
    {
        var tags = _wpf.Ui.Invoke(() =>
        {
            var page = new SettingsKeyPage();
            return ((Panel)page.FindName("PeriodChips")).Children
                .OfType<RadioButton>()
                .Select(chip => Convert.ToString(chip.Tag) ?? "")
                .ToList();
        });

        Assert.Equal(
            Enum.GetNames<SpendPeriod>().OrderBy(name => name, StringComparer.Ordinal),
            tags.OrderBy(tag => tag, StringComparer.Ordinal));
    }

    /// <summary>
    /// Переименование и удаление есть у каждой строки, в том числе у ключа из окружения.
    /// </summary>
    /// <remarks>
    /// Раньше у такой строки не было ни того, ни другого: удалить её было нельзя вовсе, а
    /// подписью ей служило имя переменной. Человек с двумя ключами различал их по маске
    /// из точек, то есть никак.
    /// </remarks>
    [Fact]
    public void Every_key_row_can_be_renamed_and_removed()
    {
        var tips = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var page = new SettingsKeyPage { Width = 520, Height = 900 };

            // В дерево окна: стили строк разрешаются по нему.
            var host = (Panel)window.FindName("MessagesPanel");
            host.Children.Add(page);
            try
            {
                page.ShowForShot(
                    new SpendReport { Status = SpendStatus.Local, Period = SpendPeriod.Week },
                    Keys());
                page.UpdateLayout();

                return Buttons((DependencyObject)page.FindName("KeyRows"))
                    .Select(button => Convert.ToString(button.ToolTip) ?? "")
                    .ToList();
            }
            finally
            {
                host.Children.Remove(page);
            }
        });

        // Три строки, и у каждой своя пара кнопок. Глаз — только у читаемых, поэтому его не
        // считаем: строка со сломанным блобом показывать нечего.
        Assert.Equal(3, tips.Count(tip => tip == Loc.Get("S.Key.Rename")));
        Assert.Equal(3, tips.Count(tip => tip == Loc.Get("S.Common.Delete")));
    }

    private static IReadOnlyList<ApiKeyEntry> Keys() =>
    [
        new("environment", "VENICE_API_KEY", "VENabcdefghijklmnop", ApiKeySource.Environment, true),
        new("k2", "Рабочий", "vk-second-key-abcdef", ApiKeySource.Stored, false),
        new("k3", "Старый", null, ApiKeySource.Stored, false)
    ];

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
            {
                yield return button;
            }

            foreach (var nested in Buttons(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Убранный ключ окружения виден строкой под списком, и её кнопка возвращает его.
    /// </summary>
    /// <remarks>
    /// Без этой строки вернуть его было бы нечем: вставить то же значение заново программа не
    /// даёт — копию переменной окружения она себе на диск не кладёт.
    /// </remarks>
    [Fact]
    public void A_removed_environment_key_leaves_a_way_back()
    {
        var (text, button) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var page = new SettingsKeyPage { Width = 520, Height = 900 };
            var host = (Panel)window.FindName("MessagesPanel");
            host.Children.Add(page);
            try
            {
                page.ShowForShot(
                    new SpendReport { Status = SpendStatus.Local, Period = SpendPeriod.Week },
                    [],
                    [new("environment", "VENICE_API_KEY", "VENabc", ApiKeySource.Environment, false)]);
                page.UpdateLayout();

                var rows = (DependencyObject)page.FindName("HiddenKeyRows");
                return (Texts(rows).FirstOrDefault() ?? "",
                        Convert.ToString(Buttons(rows).Single().Content) ?? "");
            }
            finally
            {
                host.Children.Remove(page);
            }
        });

        Assert.Equal(Loc.Format("S.Key.Removed", "VENICE_API_KEY"), text);
        Assert.Equal(Loc.Get("S.Key.Restore"), button);
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block)
            {
                yield return block.Text;
            }

            foreach (var nested in Texts(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Диалог переименования начинает спрятанным и лежит поверх вопроса об удалении.</summary>
    [Fact]
    public void The_rename_dialog_starts_hidden_above_the_others()
    {
        var (visibility, rename, remove) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            return (((FrameworkElement)window.FindName("KeyRenameOverlay")).Visibility,
                    Panel.GetZIndex((UIElement)window.FindName("KeyRenameOverlay")),
                    Panel.GetZIndex((UIElement)window.FindName("KeyRemoveOverlay")));
        });

        Assert.Equal(Visibility.Collapsed, visibility);
        Assert.True(rename > remove);
    }

    [Theory]
    [InlineData("0", "$0")]
    [InlineData("0.00004", "$0.0000")]
    [InlineData("0.0123", "$0.0123")]
    [InlineData("12.5", "$12.50")]
    public void Small_sums_keep_four_decimals(string value, string expected) =>
        Assert.Equal(
            expected,
            SettingsKeyPage.FormatUsd(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
}

/// <summary>Геометрия графика: вырожденные наборы точек не должны его ронять.</summary>
[Collection(WpfCollection.Name)]
public sealed class SpendChartTests
{
    private readonly WpfFixture _wpf;

    public SpendChartTests(WpfFixture wpf) => _wpf = wpf;

    private static IReadOnlyList<SpendPoint> Series(params decimal[] values) =>
        [.. values.Select((value, index) => new SpendPoint(new DateTime(2026, 9, 1).AddDays(index), value, 0m, 1))];

    private int Paint(IReadOnlyList<SpendPoint> points, string note = "пусто") =>
        _wpf.Ui.Invoke(() =>
        {
            var chart = new SpendChart { Width = 420, Height = 150 };
            chart.SetSeries(points, note, DateFormat.DayMonthShort);
            chart.Measure(new Size(420, 150));
            chart.Arrange(new Rect(0, 0, 420, 150));
            chart.UpdateLayout();

            var bitmap = new RenderTargetBitmap(420, 150, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chart);

            var pixels = new int[420 * 150];
            bitmap.CopyPixels(pixels, 420 * 4, 0);
            return pixels.Count(pixel => (uint)pixel >> 24 > 16);
        });

    [Fact]
    public void An_empty_series_draws_its_note_instead_of_nothing() =>
        Assert.True(Paint([]) > 50);

    /// <summary>Все нули дали бы деление на ноль в масштабе оси.</summary>
    [Fact]
    public void All_zeroes_do_not_divide_by_zero() =>
        Assert.True(Paint(Series(0m, 0m, 0m)) > 50);

    /// <summary>Одна точка: делителя «количество минус один» тут тоже нет.</summary>
    [Fact]
    public void A_single_point_does_not_collapse() =>
        Assert.True(Paint(Series(1.5m)) > 50);

    [Fact]
    public void A_real_series_draws_more_than_an_empty_one()
    {
        var empty = Paint(Series(0m, 0m, 0m, 0m, 0m));
        var drawn = Paint(Series(0.1m, 0.4m, 0.2m, 0.9m, 0.3m));

        Assert.True(drawn > empty, $"нарисовано {drawn}, на пустом {empty}");
    }

    /// <summary>Год — 365 точек. Ради этого график и рисуется одним проходом.</summary>
    [Fact]
    public void A_year_of_points_still_renders() =>
        Assert.True(Paint(Series([.. Enumerable.Range(0, 365).Select(i => (decimal)(i % 7) / 10m)])) > 50);

    [Theory]
    [InlineData(0, "$0")]
    [InlineData(0.5, "$0.5")]
    [InlineData(12.3456, "$12.35")]
    public void The_axis_keeps_small_sums_readable(double value, string expected) =>
        Assert.Equal(expected, SpendChart.FormatAxis((decimal)value));

    /// <summary>
    /// Потолок оси округляется до круглого числа. Раньше им служил сам максимум ряда, и
    /// деления подписывались его третями — на графике стояло «$0.6667» и «$0.3333».
    /// </summary>
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.42, 0.6)]
    [InlineData(0.07, 0.1)]
    [InlineData(1.1, 1.5)]
    [InlineData(4.0, 6.0)]
    [InlineData(37.0, 60.0)]
    public void The_axis_ceiling_is_a_round_number(double max, double expected) =>
        Assert.Equal((decimal)expected, SpendChart.NiceCeiling((decimal)max));

    /// <summary>Деления — трети потолка, и все три обязаны читаться без длинного хвоста.</summary>
    [Theory]
    [InlineData(0.42)]
    [InlineData(1.1)]
    [InlineData(4.0)]
    public void Every_gridline_label_is_short(double max)
    {
        var ceiling = SpendChart.NiceCeiling((decimal)max);

        for (var line = 0; line <= 3; line++)
        {
            var label = SpendChart.FormatAxis(ceiling * line / 3m);
            Assert.True(label.Length <= 6, $"подпись «{label}» длиннее, чем влезает в поле оси");
        }
    }

    /// <summary>
    /// Наведение обязано давать точку — по нему рисуется подпись с датой и суммой.
    /// </summary>
    [Fact]
    public void Hovering_reports_the_nearest_point()
    {
        var seen = _wpf.Ui.Invoke(() =>
        {
            var chart = new SpendChart { Width = 420, Height = 150 };
            SpendPoint? hovered = null;
            chart.PointHovered += point => hovered = point;
            chart.SetSeries(Series(0.1m, 0.4m, 0.2m), "", DateFormat.DayMonthShort);
            chart.Measure(new Size(420, 150));
            chart.Arrange(new Rect(0, 0, 420, 150));
            chart.UpdateLayout();

            chart.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.MouseMoveEvent
            });

            return hovered;
        });

        // Мышь стоит в нуле координат — это левее первой точки, и она обязана прижаться к ней.
        Assert.NotNull(seen);
        Assert.Equal(new DateTime(2026, 9, 1), seen.Date);
    }

    /// <summary>
    /// Плашка с датой и суммой рисуется у самой точки — в прежнем виде эти цифры стояли
    /// строкой в левом верхнем углу, далеко от того, на что человек смотрит.
    /// </summary>
    [Fact]
    public void Hovering_draws_a_callout_next_to_the_point()
    {
        var plain = Paint(Series(0.1m, 0.4m, 0.2m));
        var hovered = _wpf.Ui.Invoke(() =>
        {
            var chart = new SpendChart { Width = 420, Height = 150 };
            chart.SetSeries(Series(0.1m, 0.4m, 0.2m), "", DateFormat.DayMonthShort);
            chart.Measure(new Size(420, 150));
            chart.Arrange(new Rect(0, 0, 420, 150));
            chart.UpdateLayout();
            chart.HoverForShot(1);

            var bitmap = new RenderTargetBitmap(420, 150, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chart);
            var pixels = new int[420 * 150];
            bitmap.CopyPixels(pixels, 420 * 4, 0);
            return pixels.Count(pixel => (uint)pixel >> 24 > 16);
        });

        Assert.True(hovered > plain + 500, $"с плашкой {hovered}, без неё {plain}");
    }

    /// <summary>
    /// У правого края плашка переезжает налево от точки. Иначе у последней точки месяца её
    /// срезало бы краем карточки ровно тогда, когда на неё и смотрят.
    /// </summary>
    [Fact]
    public void The_callout_flips_to_the_left_near_the_right_edge()
    {
        var atRight = SpendChart.PlaceCallout(405, 70, 90, 44, 420, 150, 12);

        Assert.True(atRight.Right <= 420, $"плашка вылезла до {atRight.Right}");
        Assert.True(atRight.Left < 405, "у правого края плашка обязана быть слева от точки");
    }

    [Fact]
    public void The_callout_sits_to_the_right_when_there_is_room() =>
        Assert.True(SpendChart.PlaceCallout(60, 70, 90, 44, 420, 150, 12).Left > 60);

    [Theory]
    [InlineData(5, 5)]
    [InlineData(415, 145)]
    [InlineData(210, 0)]
    public void The_callout_never_leaves_the_chart(double x, double y)
    {
        var card = SpendChart.PlaceCallout(x, y, 90, 44, 420, 150, 12);

        Assert.InRange(card.Left, 0, 420 - card.Width);
        Assert.InRange(card.Top, 0, 150 - card.Height);
    }

    /// <summary>
    /// Мышь обязана попадать в график везде, а не только по нарисованным кружкам: контрол
    /// рисует в <c>DrawingVisual</c>, а WPF проверяет попадание по нарисованному, и над пустым
    /// местом между точками курсор проваливался бы сквозь него.
    /// </summary>
    [Theory]
    [InlineData(10, 10)]
    [InlineData(210, 20)]
    [InlineData(400, 140)]
    public void The_whole_surface_answers_the_mouse(double x, double y) =>
        Assert.True(_wpf.Ui.Invoke(() =>
        {
            var chart = new SpendChart { Width = 420, Height = 150 };
            chart.SetSeries(Series(0.1m, 0.4m, 0.2m), "", DateFormat.DayMonthShort);
            chart.Measure(new Size(420, 150));
            chart.Arrange(new Rect(0, 0, 420, 150));
            chart.UpdateLayout();

            // Через VisualTreeHelper, а не InputHitTest: тот работает только на элементе,
            // присоединённом к окну, а проверяется здесь именно геометрия попадания.
            return VisualTreeHelper.HitTest(chart, new Point(x, y)) is not null;
        }));
}
