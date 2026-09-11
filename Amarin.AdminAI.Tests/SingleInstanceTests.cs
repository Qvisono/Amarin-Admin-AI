using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Второй запуск программы должен отдавать работу первому, а не поднимать вторую копию: два
/// процесса дерутся за общие profiles.json и chats/index.json. Проверяется по швам — замок,
/// оконное сообщение и передача текста порознь, — чтобы не запускать второй exe.
/// </summary>
public sealed class SingleInstanceTests
{
    private static string Name() => @"Local\amarin-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void The_second_acquire_of_the_same_name_is_not_the_owner()
    {
        var name = Name();
        using var first = SingleInstance.TryAcquire(name);
        using var second = SingleInstance.TryAcquire(name);

        Assert.True(first.IsOwner);
        Assert.False(second.IsOwner);
    }

    [Fact]
    public void After_the_owner_lets_go_the_next_start_owns_it_again()
    {
        // Так же, как после падения первого процесса: ядро освобождает хэндл, и следующий
        // запуск снова становится владельцем — без возни с AbandonedMutexException.
        var name = Name();
        using (var first = SingleInstance.TryAcquire(name))
        {
            Assert.True(first.IsOwner);
        }

        using var next = SingleInstance.TryAcquire(name);
        Assert.True(next.IsOwner);
    }

    [Fact]
    public void Smoke_tools_never_consults_the_lock()
    {
        // Прогон инструментов консольный и обязан работать при уже запущенной программе.
        var asked = false;
        var route = StartupRouter.Decide(
            StartupArgs.Parse(["--smoke-tools"]),
            () =>
            {
                asked = true;
                return true;
            });

        Assert.Equal(StartupRoute.SmokeTools, route);
        Assert.False(asked, "шлюз спросили там, где он не нужен");
    }

    [Fact]
    public void A_normal_start_runs_or_hands_off_by_the_lock()
    {
        Assert.Equal(StartupRoute.Run, StartupRouter.Decide(StartupArgs.Parse([]), () => true));
        Assert.Equal(StartupRoute.HandedOff, StartupRouter.Decide(StartupArgs.Parse([]), () => false));
    }

    [Fact]
    public void A_prompt_round_trips_through_the_handoff_folder()
    {
        var root = TempRoot();
        try
        {
            SingleInstanceHandoff.Write(root, "проверь диск C");

            var taken = SingleInstanceHandoff.TryTakeAll(root);

            Assert.Equal("проверь диск C", Assert.Single(taken).Prompt);

            // Прочитал — стёр: иначе тот же текст всплыл бы при следующей активации.
            Assert.Empty(SingleInstanceHandoff.TryTakeAll(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_empty_prompt_leaves_nothing_behind()
    {
        var root = TempRoot();
        try
        {
            SingleInstanceHandoff.Write(root, "   ");
            SingleInstanceHandoff.Write(root, null);

            Assert.Empty(SingleInstanceHandoff.TryTakeAll(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_corrupt_handoff_file_is_skipped_not_fatal()
    {
        var root = TempRoot();
        try
        {
            var directory = SingleInstanceHandoff.DirectoryFor(root);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "aaa.json"), "{ это не json");
            SingleInstanceHandoff.Write(root, "живой запрос");

            var taken = SingleInstanceHandoff.TryTakeAll(root);

            Assert.Equal("живой запрос", Assert.Single(taken).Prompt);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reading_an_absent_folder_is_quiet()
    {
        Assert.Empty(SingleInstanceHandoff.TryTakeAll(
            Path.Combine(Path.GetTempPath(), "amarin-missing-" + Guid.NewGuid().ToString("N"))));
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

/// <summary>
/// Приёмная сторона: сообщение доходит до окна и разворачивает его. Шлём адресно, а не
/// широковещательно, чтобы тест не тревожил чужие окна в системе.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SingleInstanceWindowTests
{
    private readonly WpfFixture _wpf;

    public SingleInstanceWindowTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void A_posted_activate_message_restores_a_minimized_window()
    {
        var restored = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var previous = window.WindowState;
            try
            {
                window.WindowState = WindowState.Minimized;
                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, handle);

                SendMessage(handle, SingleInstance.ActivateMessage, IntPtr.Zero, IntPtr.Zero);
                return window.WindowState;
            }
            finally
            {
                window.WindowState = previous;
            }
        });

        Assert.NotEqual(WindowState.Minimized, restored);
    }

    [Fact]
    public void A_handed_off_prompt_lands_in_the_composer()
    {
        // Кладём текст туда, откуда его заберёт активация, и дёргаем её напрямую.
        var root = Amarin.Core.AppPaths.Root;
        var directory = SingleInstanceHandoff.DirectoryFor(root);
        var before = Directory.Exists(directory) ? Directory.GetFiles(directory) : [];

        var text = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var box = (TextBox)window.FindName("MessageTextBox")!;
            var previous = box.Text;
            try
            {
                SingleInstanceHandoff.Write(root, "передано вторым запуском");
                window.ActivateFromSecondInstance();
                return box.Text;
            }
            finally
            {
                box.Text = previous;
            }
        });

        Assert.Equal("передано вторым запуском", text);

        // Файлов после нас остаться не должно — активация их забирает.
        var after = Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
        Assert.Equal(before.Length, after.Length);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
