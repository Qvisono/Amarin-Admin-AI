using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Правило автоблокировки — без окна.</summary>
public sealed class AutoLockTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void It_locks_only_after_the_chosen_quiet_time_and_only_with_a_password()
    {
        Assert.False(AutoLock.IsDue(15, hasPassword: true, Now.AddMinutes(-14), Now));
        Assert.True(AutoLock.IsDue(15, hasPassword: true, Now.AddMinutes(-15), Now));

        // Без пароля снимать блокировку нечем — такой замок запер бы человека снаружи.
        Assert.False(AutoLock.IsDue(15, hasPassword: false, Now.AddHours(-5), Now));

        // Выключено.
        Assert.False(AutoLock.IsDue(0, hasPassword: true, Now.AddHours(-5), Now));
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(10, 15)]
    [InlineData(45, 60)]
    [InlineData(500, 60)]
    public void A_hand_edited_value_is_rounded_up_to_an_offered_one(int stored, int expected)
    {
        // Нестандартное значение округляется вверх: блокировка не наступит раньше заданного, а
        // страница и таймер понимают его одинаково.
        Assert.Equal(expected, AutoLock.Normalize(stored));
    }
}

/// <summary>
/// Экран блокировки и пароль на архив — на живом окне со своими службами.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class LockScreenUiTests : IDisposable
{
    private const string Password = "верный пароль";

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-lock-ui-" + Guid.NewGuid().ToString("N"));

    public LockScreenUiTests(WpfFixture wpf) => _wpf = wpf;

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

    private T With<T>(bool withPassword, Func<MainWindow, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(_root, "test", new NoNetwork());
        var profile = new UserProfile { Id = ProfileStore.DefaultProfileId, Name = "Тест" };
        if (withPassword)
        {
            (profile.PasswordHash, profile.PasswordSalt) = PasswordHash.Create(Password);
        }

        services.ProfileRegistry.Profiles.Add(profile);
        services.ProfileRegistry.ActiveProfileId = profile.Id;

        var window = new MainWindow();
        window.AttachServices(services);
        try
        {
            return body(window);
        }
        finally
        {
            window.Close();
        }
    });

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);

    private static T Named<T>(FrameworkElement root, string name) where T : class =>
        (T)root.FindName(name)!;

    private static void WaitUntil(Func<bool> done, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(15),
                DispatcherPriority.Background,
                (sender, _) =>
                {
                    ((DispatcherTimer)sender!).Stop();
                    frame.Continue = false;
                },
                Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    [Fact]
    public void Locking_hides_every_layer_beneath_and_the_right_password_brings_them_back()
    {
        var (locked, hiddenWhileLocked, disabledWhileLocked, unlocked, restored) = With(withPassword: true, window =>
        {
            var root = Named<Grid>(window, "ScaledRoot");
            var lockScreen = Named<LockScreen>(window, "LockOverlay");
            var layers = root.Children.OfType<UIElement>().Where(layer => !ReferenceEquals(layer, lockScreen)).ToList();

            window.LockNow();
            var isLocked = window.IsLocked;

            // Под блокировкой переписка не просвечивает и не берёт фокус: заливка окна в
            // оформлении «стекло» полупрозрачна, одной подложкой её не спрятать.
            var hidden = layers.All(layer => layer.Opacity == 0);
            var disabled = layers.All(layer => !layer.IsEnabled);

            lockScreen.Password.Password = Password;
            Call(window, "TryUnlockAsync", Password);
            WaitUntil(() => !window.IsLocked);

            return (isLocked, hidden, disabled, !window.IsLocked,
                layers.All(layer => layer.Opacity == 1 && layer.IsEnabled));
        });

        Assert.True(locked);
        Assert.True(hiddenWhileLocked);
        Assert.True(disabledWhileLocked);
        Assert.True(unlocked);
        Assert.True(restored);
    }

    [Fact]
    public void A_wrong_password_keeps_the_lock_and_says_so()
    {
        var (stillLocked, error) = With(withPassword: true, window =>
        {
            window.LockNow();
            Call(window, "TryUnlockAsync", "не тот");
            var lockScreen = Named<LockScreen>(window, "LockOverlay");
            var errorText = (TextBlock)lockScreen.FindName("ErrorText")!;
            WaitUntil(() => errorText.Visibility == Visibility.Visible);
            return (window.IsLocked, errorText.Text);
        });

        Assert.True(stillLocked);
        Assert.Equal(Loc.Get("S.Lock.WrongPassword"), error);
    }

    [Fact]
    public void Without_a_password_there_is_no_lock_to_fall_into()
    {
        var locked = With(withPassword: false, window =>
        {
            window.LockNow();
            return window.IsLocked;
        });

        Assert.False(locked);
    }

    [Fact]
    public void Typing_under_the_lock_never_reaches_the_message_box()
    {
        var composer = With(withPassword: true, window =>
        {
            window.LockNow();
            var box = Named<TextBox>(window, "MessageTextBox");
            var before = box.Text;
            var args = new System.Windows.Input.TextCompositionEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, window, "секрет"))
            {
                RoutedEvent = UIElement.PreviewTextInputEvent
            };
            window.RaiseEvent(args);
            return (before, box.Text);
        });

        Assert.Equal(composer.before, composer.Item2);
    }

    [Fact]
    public void A_password_box_keeps_what_is_typed_into_it()
    {
        // Иначе пароль архива, набранный в своём поле, окно переложило бы в поле сообщения.
        var (password, text, readOnly) = _wpf.Ui.Invoke(() => (
            MainWindow.TakesTypedText(new PasswordBox()),
            MainWindow.TakesTypedText(new TextBox()),
            MainWindow.TakesTypedText(new TextBox { IsReadOnly = true })));

        Assert.True(password);
        Assert.True(text);
        Assert.False(readOnly);
    }

    [Fact]
    public void The_archive_password_must_be_typed_twice_alike()
    {
        var (off, empty, mismatch, match, error) = With(withPassword: false, window =>
        {
            var toggle = Named<CheckBox>(window, "ExportProtectToggle");
            var first = Named<PasswordBox>(window, "ExportPasswordBox");
            var second = Named<PasswordBox>(window, "ExportPasswordRepeatBox");
            var errorText = Named<TextBlock>(window, "ExportErrorText");

            var none = (string?)Call(window, "ExportPassword");

            toggle.IsChecked = true;
            var blank = (string?)Call(window, "ExportPassword");

            first.Password = "раз";
            second.Password = "два";
            var differ = (string?)Call(window, "ExportPassword");
            var shown = errorText.Text;

            second.Password = "раз";
            var same = (string?)Call(window, "ExportPassword");
            return (none, blank, differ, same, shown);
        });

        Assert.Null(off);
        Assert.Equal("", empty);
        Assert.Equal("", mismatch);
        Assert.Equal(Loc.Get("S.Bundle.Export.PasswordMismatch"), error);
        Assert.Equal("раз", match);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
