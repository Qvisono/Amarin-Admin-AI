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

    /// <summary>
    /// Кромки настоящего окна (D4): пол — в промежутках без кнопок поля ввода, верх нижнего блока
    /// колонки — пока колонка развёрнута. Подставной хозяин сцены этого не проверяет: здесь
    /// координаты считаются по живой раскладке, и переименованный элемент окна дал бы пустой пол
    /// или кромку поверх кнопок.
    /// </summary>
    [Fact]
    public void The_floor_avoids_the_composer_buttons_and_the_column_edge_goes_with_the_sidebar()
    {
        var (spans, buttons, floor, column, collapsed) = With(window =>
        {
            Arrive(window);
            var host = (IFoxHost)window.Fox!;
            Rect Bounds(string name)
            {
                var element = (FrameworkElement)window.FindName(name)!;
                return element.IsVisible
                    ? element.TransformToVisual(Layer(window)).TransformBounds(new Rect(element.RenderSize))
                    : Rect.Empty;
            }

            var shown = host.FloorSpans();
            Rect[] row = [Bounds("ModelButton"), Bounds("MicButton"), Bounds("SendButton")];
            var edge = host.Column();
            Call(window, "SetSidebarCollapsed", true);
            window.UpdateLayout();
            return (shown, row.Where(rect => !rect.IsEmpty).ToList(), host.Floor(), edge, host.Column());
        });

        Assert.Equal(3, spans.Count);
        Assert.NotEmpty(buttons);
        Assert.All(spans, span =>
        {
            Assert.Equal(floor, span.Y);
            Assert.All(buttons, button => Assert.True(span.Right <= button.Left || span.Left >= button.Right,
                $"{span.Left:0}..{span.Right:0} накрывает кнопку {button.Left:0}..{button.Right:0}"));
        });

        Assert.NotNull(column);
        Assert.True(column.Value.Y < floor);
        Assert.Null(collapsed);
    }

    /// <summary>
    /// «Что Роза помнит» в «Нашей дружбе»: заметки видны человеку, и «Забыть» стирает их и у
    /// выключенного Розы — прямо в его файле.
    /// </summary>
    [Fact]
    public void Our_friendship_shows_his_notes_and_forgetting_erases_them()
    {
        var (shown, left) = With(window =>
        {
            var services = Services(window);
            using (var store = new FoxStore(services.Profile.DataRoot, () => false))
            {
                store.Update(state =>
                {
                    state.MetAt = DateTime.Now.AddDays(-3);
                    FoxMemory.Remember(state, "Любит дождь", DateTime.Now);
                });
            }

            var text = Texts(FoxPanels.Friendship(window, services));
            FoxPanels.ForgetOnDisk(services);
            using var again = new FoxStore(services.Profile.DataRoot, () => false);
            return (text, again.Snapshot().Notes.Count);
        });

        Assert.Contains("Любит дождь", shown);
        Assert.Equal(0, left);
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

    /// <summary>
    /// Открыт журнал — слой Розы поднимается выше него и выше «Состояния ПК», чтобы он выглядывал
    /// из-за карточки; под ним остаётся только то, что и так под журналом. Вопрос о домене той же
    /// высоты объявлен позже и остаётся выше, остальные вопросы — выше по числу. Закрыли — обратно.
    /// </summary>
    [Fact]
    public void Over_the_journal_the_layer_rises_and_still_stays_under_every_question()
    {
        var (raised, below, domainAfter, lowered) = With(window =>
        {
            Arrive(window);
            var root = (Grid)window.FindName("ScaledRoot")!;
            var layer = Layer(window);
            var journal = (UIElement)window.FindName("JournalOverlay")!;
            journal.Visibility = Visibility.Visible;
            window.UpdateLayout();
            var z = Panel.GetZIndex(layer);
            var children = root.Children.OfType<UIElement>().ToList();
            var under = children
                .Where(child => !ReferenceEquals(child, layer) && Panel.GetZIndex(child) >= 33 && Panel.GetZIndex(child) < z)
                .Select(child => (child as FrameworkElement)?.Name ?? "")
                .ToList();
            var domain = (UIElement)window.FindName("DomainOverlay")!;
            var after = Panel.GetZIndex(domain) == z && children.IndexOf(domain) > children.IndexOf(layer);
            journal.Visibility = Visibility.Collapsed;
            return (z, under, after, Panel.GetZIndex(layer));
        });

        Assert.Equal(35, raised);
        Assert.Equal(["HealthOverlay", "JournalOverlay"], below.Order());
        Assert.True(domainAfter, "вопрос о домене оказался под Розой");
        Assert.Equal(27, lowered);
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

    /// <summary>Роза в руках: Esc отпускает его и не останавливает идущий ответ.</summary>
    [Fact]
    public void Escape_lets_go_of_a_held_rosa()
    {
        var (handled, after, cancelled) = With(window =>
        {
            Arrive(window);
            var stage = Stage(window);
            var turn = StartTurn(window);
            stage.BeginDrag(new Point(300, 200));
            return (PressEscape(window), stage.Mode, turn.Cancellation.IsCancellationRequested);
        });

        Assert.True(handled);
        Assert.NotEqual(FoxMode.Drag, after);
        Assert.False(cancelled);
    }

    /// <summary>
    /// Пока идёт ответ, игра Esc не забирает: её Роза затевает и сам, пока человек ждёт, — а Esc
    /// человек жмёт, чтобы остановить ответ. Без ответа тот же Esc заканчивает игру.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void During_an_answer_escape_stops_the_answer_and_not_the_game(bool answering)
    {
        var (handled, game, cancelled) = With(window =>
        {
            Arrive(window);
            var stage = Stage(window);
            var turn = answering ? StartTurn(window) : null;
            stage.PlayGame(FoxWill.Catch);
            return (PressEscape(window), stage.Game, turn?.Cancellation.IsCancellationRequested == true);
        });

        Assert.True(handled);
        Assert.Equal(answering, cancelled);
        Assert.Equal(answering ? FoxWill.Catch : null, game);
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

    /// <summary>
    /// Переписка (1.36.0) — маленькая панель над Розой, а не окно поверх программы: чат не
    /// заслонён и не заблокирован. Она показывает разговоры из памяти и шлёт сказанное тем же
    /// путём, что поле над головой; ключа у тестового окна нет, модель не отвечает — и панель не
    /// ждёт вечно, а показывает «слова убежали».
    /// </summary>
    [Fact]
    public void The_chat_is_a_small_panel_above_rosa_that_answers_what_is_said()
    {
        var (modal, above, narrow, history, waited, lines, closed) = With(window =>
        {
            var sprite = Arrive(window);
            var host = window.Fox!;
            var store = (FoxStore)host.GetType().GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            store.Update(state => FoxMemory.AddExchange(state, "как дела?", "Хвост пушистый, всё хорошо!", DateTime.Now));

            var stage = Stage(window);
            stage.OpenChat();
            window.UpdateLayout();
            var chat = stage.Chat!;
            var panel = chat.TransformToAncestor(window).TransformBounds(new Rect(chat.RenderSize));
            var fox = sprite.TransformToAncestor(window).TransformBounds(new Rect(sprite.RenderSize));
            var blocked = window.IsNoticeOpen || window.ChatBlocked;
            var shown = chat.Lines.ToList();
            chat.Send("привет, Роза");
            var waiting = chat.Waiting;
            PumpUntil(() => !chat.Waiting);
            var after = chat.Lines.ToList();
            stage.CloseChat();
            return (blocked, panel.Bottom <= fox.Top + 1, panel.Width <= FoxChat.PanelWidth + 1, shown, waiting, after,
                !Layer(window).Children.OfType<FoxChat>().Any());
        });

        Assert.False(modal);
        Assert.True(above, "панель не над Розой");
        Assert.True(narrow);
        Assert.Equal(["как дела?", "Хвост пушистый, всё хорошо!"], history);
        Assert.True(waited);
        Assert.Equal("привет, Роза", lines[^2]);
        Assert.Contains(lines[^1], new[] { "S.Fox.Say.Mute.1", "S.Fox.Say.Mute.2" }.Select(key => Loc.Get(key)));
        Assert.True(closed);
    }

    /// <summary>
    /// Крестик закрывает переписку. Прежде нажатие на него никто не брал, оно всплывало к корню
    /// окна, и тот начинал им перетаскивание окна: Windows уводил мышь в свой цикл, отпускание до
    /// крестика не доходило — панель не закрывалась.
    /// </summary>
    [Fact]
    public void The_chat_closes_by_its_cross_and_the_press_does_not_drag_the_window()
    {
        var (handled, closed) = With(window =>
        {
            Arrive(window);
            var stage = Stage(window);
            stage.OpenChat();
            window.UpdateLayout();
            var cross = stage.Chat!.CloseButton;
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent };
            cross.RaiseEvent(down);
            cross.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            return (down.Handled, stage.Chat is null && !Layer(window).Children.OfType<FoxChat>().Any());
        });

        Assert.True(handled, "нажатие ушло окну, и оно начало бы перетаскивание");
        Assert.True(closed, "крестик не закрыл переписку");
    }

    /// <summary>Esc закрывает переписку — кроме случая, когда он останавливает идущий ответ.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Escape_closes_the_chat_unless_it_stops_an_answer(bool answering)
    {
        var (open, cancelled) = With(window =>
        {
            Arrive(window);
            var stage = Stage(window);
            stage.OpenChat();
            window.UpdateLayout();
            var turn = answering ? StartTurn(window) : null;
            _ = window.MessageTextBox.Focus();
            _ = PressEscape(window);
            return (stage.Chat is not null, turn?.Cancellation.IsCancellationRequested == true);
        });

        Assert.Equal(answering, open);
        Assert.Equal(answering, cancelled);
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

    /// <summary>
    /// Смена профиля читает галочку нового профиля: там Роза выключен — уходит из окна. До
    /// 1.37.0-beta.7 он только переезжал на память нового профиля и оставался.
    /// </summary>
    [Fact]
    public void A_profile_where_rosa_is_off_takes_him_away()
    {
        var (fox, children) = With(window =>
        {
            Arrive(window);

            // Настройки нового профиля уже живые (UseProfile), страница их только показывает.
            Services(window).Settings.Fox.Enabled = false;
            Call(window, "ApplyFoxForProfile");
            return (window.Fox, Layer(window).Children.Count);
        });

        Assert.Null(fox);
        Assert.Equal(0, children);
    }

    /// <summary>
    /// Память прежнего профиля ложится в его папку по его галочке шифрования, а не по галочке
    /// нового: до 1.37.0-beta.7 у профиля с шифрованием она уходила на диск открытым текстом,
    /// если в профиле, куда перешли, шифрования нет.
    /// </summary>
    [Fact]
    public void Leaving_a_profile_writes_rosas_memory_with_that_profiles_encryption()
    {
        var (encrypted, written) = With(window =>
        {
            Arrive(window);
            var services = Services(window);
            var first = services.Profile.DataRoot;
            services.Settings.EncryptChats = true;
            services.SettingsStore.Save(services.Settings);
            var host = window.Fox!;
            var store = (FoxStore)host.GetType().GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            store.Update(state => state.Boredom = 42);

            services.UseProfile(Path.Combine(_root, "second-" + Guid.NewGuid().ToString("N")[..8]));
            services.Settings.EncryptChats = false;
            Call(window, "ApplyFoxForProfile");

            var bytes = File.ReadAllBytes(FoxData.StatePath(first));
            return (AtRestCipher.IsEncrypted(bytes), bytes.Length > 0);
        });

        Assert.True(written);
        Assert.True(encrypted, "память профиля с шифрованием легла на диск открытым текстом");
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

    /// <summary>Ход открытого чата — вручную, без движка: так видно, что Esc сделал с ответом.</summary>
    private static RunningTurn StartTurn(MainWindow window)
    {
        var session = (ChatSession)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        return window.Turns.TryStart(session, TurnKind.Send, DateTime.Now).Turn ?? throw new InvalidOperationException("ход не завёлся");
    }

    /// <summary>Esc, как его разбирает окно: <c>true</c> — нажатие кто-то забрал.</summary>
    private static bool PressEscape(MainWindow window)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent
        };
        return (bool)typeof(MainWindow).GetMethod("TryStopByEscape", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [args])!;
    }

    private static void Call(MainWindow window, string method, params object[] args) =>
        _ = typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

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
