using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.Fox;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Роза в настоящем окне (1.35.0): приходит только по старту, садится на кромку поля ввода, слой
/// лежит между настройками и вопросами и не мешает ни клавишам выбора, ни щелчкам сквозь себя.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class FoxWindowTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-fox-window-" + Guid.NewGuid().ToString("N"));

    public FoxWindowTests(WpfFixture wpf) => _wpf = wpf;

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
    public void In_tests_rosa_comes_only_when_started_and_then_sits_on_the_composer()
    {
        var (before, layerBefore, after, gap) = With(window =>
        {
            var early = window.Fox is not null;
            var collapsed = Layer(window).Visibility;
            var sprite = Arrive(window);
            var composer = (FrameworkElement)window.FindName("ComposerBorder")!;
            var bottom = sprite.TransformToAncestor(window).TransformBounds(new Rect(sprite.RenderSize)).Bottom;
            var top = composer.TransformToAncestor(window).TransformBounds(new Rect(composer.RenderSize)).Top;
            return (early, collapsed, window.Fox is not null, bottom - top);
        });

        Assert.False(before);
        Assert.Equal(Visibility.Collapsed, layerBefore);
        Assert.True(after);

        // Лапы на кромке: последняя строка рисунка заходит на рамку поля, выше — ни точки воздуха.
        Assert.InRange(gap, 0, 4);
    }

    /// <summary>Выше настроек — он садится на их карточку; ниже всех вопросов: у каждого затемнение на весь слой.</summary>
    [Fact]
    public void The_fox_layer_lies_above_settings_and_below_every_question()
    {
        var (fox, settings, others) = With(window =>
        {
            var root = (Grid)window.FindName("ScaledRoot")!;
            var layer = Layer(window);
            var settingsOverlay = (UIElement)window.FindName("SettingsOverlay")!;
            var rest = root.Children.OfType<UIElement>().Skip(1)
                .Where(child => !ReferenceEquals(child, layer) && !ReferenceEquals(child, settingsOverlay))
                .Select(child => (Name: (child as FrameworkElement)?.Name ?? child.GetType().Name, Z: Panel.GetZIndex(child)))
                .ToList();
            return (Panel.GetZIndex(layer), Panel.GetZIndex(settingsOverlay), rest);
        });

        Assert.Equal(27, fox);
        Assert.True(settings < fox);
        Assert.All(others, layer => Assert.True(layer.Z > fox, $"{layer.Name} ({layer.Z}) лежит под Розой"));
    }

    /// <summary>Слой Розы виден всегда, пока он здесь, но ничего не закрывает: Ctrl+A и Shift+Del в колонке работают.</summary>
    [Fact]
    public void With_rosa_in_the_window_the_main_layout_still_counts_as_uncovered()
    {
        var uncovered = With(window =>
        {
            Arrive(window);
            return (bool)typeof(MainWindow).GetMethod("MainLayoutUncovered", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        });

        Assert.True(uncovered);
    }

    [Fact]
    public void A_click_on_a_transparent_pixel_of_rosa_goes_through_and_on_his_fur_hits_him()
    {
        var (corner, body) = With(window =>
        {
            var sprite = Arrive(window);
            var side = sprite.ActualWidth;
            var origin = sprite.TransformToAncestor(window).Transform(new Point(0, 0));

            // Левый верхний угол кадра пуст у сидящего, середина нижней части — шерсть.
            var empty = window.InputHitTest(origin + new Vector(1, 1)) as DependencyObject;
            var fur = window.InputHitTest(origin + new Vector(side * 0.5, side * 0.75)) as DependencyObject;
            return (IsWithin(empty, sprite), IsWithin(fur, sprite));
        });

        Assert.False(corner);
        Assert.True(body);
    }

    /// <summary>Нажатие на Розу гасится: иначе его подхватили бы перетаскивание окна и рамка колонки.</summary>
    [Fact]
    public void A_press_on_rosa_is_handled_by_him()
    {
        var handled = With(window =>
        {
            var sprite = Arrive(window);
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            sprite.RaiseEvent(press);
            var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            sprite.RaiseEvent(release);
            return press.Handled && release.Handled;
        });

        Assert.True(handled);
    }

    /// <summary>Роза в руках: Esc отпускает его и не останавливает ответ.</summary>
    [Fact]
    public void Escape_lets_go_of_a_held_rosa()
    {
        var (holding, stopped, after) = With(window =>
        {
            Arrive(window);
            var stage = Stage(window);
            stage.BeginDrag(new Point(300, 200));
            var held = window.Fox!.Holding;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent
            };
            var result = (bool)typeof(MainWindow).GetMethod("TryStopByEscape", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [args])!;
            return (held, result, stage.Mode);
        });

        Assert.True(holding);
        Assert.True(stopped);
        Assert.NotEqual(FoxMode.Drag, after);
    }

    /// <summary>
    /// Поле разговора с Розой — обычное поле с текстом: окно не перекладывает набранное в поле
    /// сообщения (иначе разговор с лисёнком ушёл бы в чат с моделью), а Esc закрывает его.
    /// </summary>
    [Fact]
    public void The_talk_field_keeps_its_text_and_escape_closes_it()
    {
        var (takes, above, closed) = With(window =>
        {
            var sprite = Arrive(window);
            var stage = Stage(window);
            stage.OpenTalk();
            window.UpdateLayout();
            var field = Layer(window).Children.OfType<Border>().Single(border => border.Child is TextBox);
            var box = (TextBox)field.Child;
            var fieldBottom = field.TransformToAncestor(window).TransformBounds(new Rect(field.RenderSize)).Bottom;
            var spriteTop = sprite.TransformToAncestor(window).TransformBounds(new Rect(sprite.RenderSize)).Top;
            stage.LetGo();
            return (MainWindow.TakesTypedText(box), fieldBottom <= spriteTop + 1, field.Visibility == Visibility.Collapsed);
        });

        Assert.True(takes);
        Assert.True(above);
        Assert.True(closed);
    }

    /// <summary>Подстраница «Лисёнок Роза» отражает его настройки и пишет их; выключили — уходит из окна.</summary>
    [Fact]
    public void The_rosa_subpage_writes_his_settings_and_turning_him_off_removes_him()
    {
        var (loaded, size, mischief, fox, children) = With(window =>
        {
            Arrive(window);
            var services = Services(window);
            var block = (FoxSettingsBlock)window.FindSetting("FoxSettings");
            var loadedShow = block.ShowToggle.IsChecked == true;
            block.SizeLarge.IsChecked = true;
            block.MischiefToggle.IsChecked = false;
            block.ShowToggle.IsChecked = false;
            var saved = services.SettingsStore.Load().Fox;
            return (loadedShow, saved.Size, saved.Mischief, window.Fox, Layer(window).Children.Count);
        });

        Assert.True(loaded);
        Assert.Equal(FoxSettings.LargeSize, size);
        Assert.False(mischief);
        Assert.Null(fox);
        Assert.Equal(0, children);
    }

    /// <summary>Дружба и дневник видны и у выключенного Розы — из его файлов; «Начать заново» дневник не трогает.</summary>
    [Fact]
    public void Friendship_and_diary_read_his_files_and_a_reset_keeps_the_diary()
    {
        var (friendship, diary, affection, entries) = With(window =>
        {
            var services = Services(window);
            using (var store = new FoxStore(services.Profile.DataRoot, () => false))
            {
                store.Update(state =>
                {
                    state.MetAt = new DateTime(2026, 9, 1);
                    state.Affection = 90;
                });
                store.Write(new FoxDiaryEntry { Date = "2026-10-11", Text = "старая запись", Annoyed = 1, Pleased = 5 });
                store.Write(new FoxDiaryEntry { Date = "2026-10-12", Text = "свежая запись", Annoyed = 0, Pleased = 9 });
            }

            var friendshipText = Texts(FoxPanels.Friendship(window, services));
            var diaryText = Texts(FoxPanels.Diary(window, services));
            FoxPanels.ResetOnDisk(services);
            using var after = new FoxStore(services.Profile.DataRoot, () => false);
            return (friendshipText, diaryText, after.Snapshot().Affection, after.Diary().Count);
        });

        Assert.Contains(Loc.Get("S.Fox.Level.Best"), friendship);
        Assert.True(diary.IndexOf("свежая запись", StringComparison.Ordinal) < diary.IndexOf("старая запись", StringComparison.Ordinal));
        Assert.Equal(FoxState.StartAffection, affection);
        Assert.Equal(2, entries);
    }

    [Fact]
    public void Stopping_rosa_empties_and_collapses_the_layer()
    {
        var (children, visibility) = With(window =>
        {
            Arrive(window);
            window.StopFox();
            var layer = Layer(window);
            return (layer.Children.Count, layer.Visibility);
        });

        Assert.Equal(0, children);
        Assert.Equal(Visibility.Collapsed, visibility);
    }

    /// <summary>
    /// Сборка Розы не грузится до её прихода: окно держит только <c>IFoxHooks</c> на простых типах,
    /// а тип с Розой называет один метод — <c>CreateFox</c>. Поле, параметр или возврат с типом из
    /// <c>Amarin.Fox</c> в самом окне загрузили бы её вместе с окном, на первом кадре.
    /// </summary>
    [Fact]
    public void The_window_names_no_rosa_type_outside_its_one_factory()
    {
        var fox = typeof(FoxStage).Assembly;
        bool FromFox(Type type) =>
            type.Assembly == fox || (type.IsGenericType && type.GetGenericArguments().Any(FromFox)) ||
            (type.HasElementType && FromFox(type.GetElementType()!));

        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var window = typeof(MainWindow);
        Assert.DoesNotContain(window.GetFields(all), field => FromFox(field.FieldType));
        Assert.DoesNotContain(window.GetProperties(all), property => FromFox(property.PropertyType));
        Assert.DoesNotContain(window.GetMethods(all), method =>
            FromFox(method.ReturnType) || method.GetParameters().Any(parameter => FromFox(parameter.ParameterType)));

        var hooks = window.Assembly.GetType("Amarin.UI.IFoxHooks")!;
        Assert.DoesNotContain(hooks.GetMethods(), method =>
            FromFox(method.ReturnType) || method.GetParameters().Any(parameter => FromFox(parameter.ParameterType)));

        // В исходниках приложения Розу знают только переходник и одна строка фабрики.
        var app = SourceTree.ProjectDirectory;
        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                                    !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            // Переходник и окна дружбы и дневника грузятся только по делу — когда Роза пришёл или
            // человек открыл одно из окон.
            var name = Path.GetFileName(file);
            if (name is "MainWindow.FoxHost.cs" or "FoxPanels.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("using Amarin.Fox", text);
            Assert.False(Regex.IsMatch(text, @"\bFox(Stage|Store|Sprites|Brain|Signal|SignalKind|Situation|Edge|Mode|State)\b"), name);
            var factory = Regex.Matches(text, @"\bFoxWindowHost\b").Count;
            Assert.True(factory == 0 || (name == "MainWindow.Fox.cs" && factory <= 2), name);
        }
    }

    private static FoxSprite Arrive(MainWindow window)
    {
        window.StartFox(inTests: true, delay: TimeSpan.FromMilliseconds(1));
        PumpUntil(() => Layer(window).Children.OfType<FoxSprite>().Any(sprite => sprite.IsVisible && sprite.ActualWidth > 0));
        window.UpdateLayout();
        return Layer(window).Children.OfType<FoxSprite>().Single();
    }

    private static FoxStage Stage(MainWindow window)
    {
        var host = window.Fox!;
        return (FoxStage)host.GetType().GetField("_stage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    }

    private static Canvas Layer(MainWindow window) => (Canvas)window.FindName("FoxLayer")!;

    private static AppServices Services(MainWindow window) =>
        (AppServices)typeof(MainWindow).GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    /// <summary>Весь текст элемента подряд — сверять, что показано.</summary>
    private static string Texts(DependencyObject root)
    {
        var text = new System.Text.StringBuilder();
        void Walk(DependencyObject node)
        {
            if (node is TextBlock block)
            {
                text.AppendLine(block.Text);
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                Walk(child);
            }
        }

        Walk(root);
        return text.ToString();
    }

    private static bool IsWithin(DependencyObject? hit, DependencyObject target)
    {
        for (var node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Крутит очередь окна, пока условие не выполнится: старт Розы — таймер, рабочий поток и возврат на поток окна.</summary>
    private static void PumpUntil(Func<bool> done, int milliseconds = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.ElapsedMilliseconds < milliseconds, "Роза не пришёл");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private T With<T>(Func<MainWindow, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]), "k", new HttpClientHandler());
        var window = new MainWindow
        {
            Width = 1100,
            Height = 800,
            Left = -32000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.AttachServices(services);
        window.Show();

        // Без анимаций Роза садится сразу, а не выбегает из-за края кадрами отрисовки.
        var motion = UiMotion.Enabled;
        UiMotion.Enabled = false;
        try
        {
            window.UpdateLayout();
            return body(window);
        }
        finally
        {
            UiMotion.Enabled = motion;
            window.StopFox();
            window.Close();
        }
    });
}
