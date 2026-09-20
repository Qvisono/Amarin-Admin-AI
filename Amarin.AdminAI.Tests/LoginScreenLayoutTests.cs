using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Экран входа: кнопка смены пользователя не наезжает на «Отмена» и «Войти» ни на каком языке.
/// </summary>
/// <remarks>
/// Наезжала она вот почему: ряд кнопок был Grid без колонок, то есть одной ячейкой, в которой
/// подпись «Сменить пользователя» и правая пара кнопок разведены только выравниванием. Места
/// друг под друга они не резервировали, ширина окна прибита к 400, а кнопка росла вместе с
/// переводом. Теперь смена пользователя — иконка в углу карточки, и мерить это надо
/// геометрией: на глаз такое ловится только на том языке, где уже сломалось.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class LoginScreenLayoutTests
{
    private readonly WpfFixture _wpf;

    public LoginScreenLayoutTests(WpfFixture wpf) => _wpf = wpf;

    private static UserProfile Locked() => new() { Id = "second", Name = "Розка", PasswordHash = "x" };

    /// <summary>Окно входа с включённым переключателем профилей, как его поднимает запуск.</summary>
    private static PasswordWindow Screen()
    {
        var registry = new ProfileRegistry
        {
            ActiveProfileId = "second",
            Profiles = [new UserProfile { Id = "first", Name = "Гость" }, Locked()]
        };

        var window = (PasswordWindow)Activator.CreateInstance(
            typeof(PasswordWindow),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [Locked(), false],
            culture: null)!;

        typeof(PasswordWindow)
            .GetMethod("EnableProfileSwitching", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new ProfileStore(), registry]);

        return window;
    }

    [Fact]
    public void The_switch_button_never_runs_under_the_other_buttons()
    {
        var (card, switcher, cancel, ok) = _wpf.Ui.Invoke(() =>
        {
            var window = Screen();
            try
            {
                // Нарочно длинная подпись: на такой прежняя кнопка и уезжала под соседей.
                window.HeadingText.Text = new string('Ш', 40);
                window.SubtitleText.Text = string.Join(" ", Enumerable.Repeat("Профиль защищён паролем.", 6));

                var shell = (FrameworkElement)window.Content;
                shell.Measure(new Size(window.Width, double.PositiveInfinity));
                shell.Arrange(new Rect(new Point(0, 0), shell.DesiredSize));
                shell.UpdateLayout();

                var frame = (FrameworkElement)window.FindName("Card")!;
                return (
                    new Rect(new Point(0, 0), frame.RenderSize),
                    Bounds(window.SwitchUserButton, frame),
                    Bounds(window.CancelButton, frame),
                    Bounds(window.OkButton, frame));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.False(switcher.IntersectsWith(cancel), $"кнопка смены {switcher} наехала на «Отмена» {cancel}");
        Assert.False(switcher.IntersectsWith(ok), $"кнопка смены {switcher} наехала на «Войти» {ok}");
        Assert.True(card.Contains(switcher), $"кнопка смены {switcher} вышла за карточку {card}");
    }

    [Fact]
    public void The_switch_button_wears_an_icon_and_keeps_its_tooltip()
    {
        // Подписи у кнопки больше нет, и без подсказки человек не узнает, что она делает.
        // Ключ при этом остаётся прежним — переводы его уже знают.
        var (tooltip, hasIcon) = _wpf.Ui.Invoke(() =>
        {
            var window = Screen();
            try
            {
                window.SwitchUserButton.ApplyTemplate();
                var glyph = (System.Windows.Shapes.Path)window.SwitchUserButton.Template
                    .FindName("Glyph", window.SwitchUserButton);

                return (window.SwitchUserButton.ToolTip, glyph.Data is not null && !glyph.Data.IsEmpty());
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(Loc.Get("S.Password.SwitchUser"), tooltip);
        Assert.True(hasIcon, "у кнопки смены профиля пустая иконка");
    }

    [Fact]
    public void Only_a_second_profile_brings_the_button_out()
    {
        var alone = _wpf.Ui.Invoke(() =>
        {
            var window = (PasswordWindow)Activator.CreateInstance(
                typeof(PasswordWindow),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [Locked(), false],
                culture: null)!;
            try
            {
                typeof(PasswordWindow)
                    .GetMethod("EnableProfileSwitching", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new ProfileStore(), new ProfileRegistry { Profiles = [Locked()] }]);

                return window.SwitchUserButton.Visibility;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(Visibility.Collapsed, alone);
    }

    private static Rect Bounds(FrameworkElement element, Visual within) =>
        element.TransformToAncestor(within)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
}
