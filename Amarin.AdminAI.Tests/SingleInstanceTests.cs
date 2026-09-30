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
    public void Applying_an_update_never_consults_the_lock_either()
    {
        // Подмену просит живая программа, а замок держит как раз она: спросив его, повышенный
        // процесс ушёл бы передавать ей запрос вместо работы — и обновление не состоялось бы.
        var asked = false;
        var route = StartupRouter.Decide(
            StartupArgs.Parse(["--apply-update", @"C:\tmp\new.exe"]),
            () =>
            {
                asked = true;
                return false;
            });

        Assert.Equal(StartupRoute.ApplyUpdate, route);
        Assert.False(asked, "шлюз спросили там, где он не нужен");
    }

    [Theory]
    [InlineData("--await-exit", "4242")]
    [InlineData("--await-exit=4242", null)]
    public void The_pid_to_wait_for_is_read_from_the_command_line(string first, string? second)
    {
        // Так возвращается программа после обновления: новый процесс ждёт, пока старый отпустит
        // замок, иначе видит живого владельца и молча выходит.
        var args = second is null ? new[] { first } : [first, second];

        Assert.Equal(4242, StartupArgs.Parse(args).AwaitExitPid);
    }

    [Theory]
    [InlineData("--await-exit", "не число")]
    [InlineData("--await-exit", "-1")]
    public void A_nonsense_pid_is_no_pid_at_all(string first, string second) =>
        Assert.Null(StartupArgs.Parse([first, second]).AwaitExitPid);

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
    public void A_prompt_from_the_command_line_is_only_sent_with_the_send_flag()
    {
        // До 1.28.0 первый запуск отправлял запрос сам, а переданный открытому окну — нет;
        // теперь в обоих случаях решает явный --send.
        Assert.False(StartupArgs.Parse(["--prompt", "проверь диск"]).ShouldSend);
        Assert.True(StartupArgs.Parse(["--prompt", "проверь диск", "--send"]).ShouldSend);
        Assert.True(StartupArgs.Parse(["--SEND", "-p=проверь диск"]).ShouldSend);

        // Флаг без запроса отправлять нечего.
        Assert.False(StartupArgs.Parse(["--send"]).ShouldSend);
    }

    [Fact]
    public void The_send_flag_travels_with_its_own_prompt_through_the_handoff()
    {
        var root = TempRoot();
        try
        {
            SingleInstanceHandoff.Write(root, "первый", send: true);
            Thread.Sleep(5);
            SingleInstanceHandoff.Write(root, "второй");

            var taken = SingleInstanceHandoff.TryTakeAll(root);

            // Берётся последний запрос вместе со своим флагом: --send первого запуска не должен
            // отправить текст, который второй велел лишь положить в поле. Порядок файлов —
            // по имени, поэтому сверяем по содержимому, а не по положению.
            Assert.Equal(2, taken.Count);
            Assert.True(taken.Single(item => item.Prompt == "первый").Send);
            Assert.False(taken.Single(item => item.Prompt == "второй").Send);
            var latest = HandoffRequest.Latest([
                new HandoffRequest { Prompt = "первый", Send = true },
                new HandoffRequest { Prompt = "второй" },
                new HandoffRequest { Prompt = "  ", Send = true }
            ]);
            Assert.Equal("второй", latest!.Prompt);
            Assert.False(latest.Send);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_handoff_file_from_an_older_version_is_only_placed_in_the_field()
    {
        var root = TempRoot();
        try
        {
            var directory = SingleInstanceHandoff.DirectoryFor(root);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "old.json"), """{ "prompt": "старый запрос" }""");

            var request = Assert.Single(SingleInstanceHandoff.TryTakeAll(root));

            Assert.Equal("старый запрос", request.Prompt);
            Assert.False(request.Send);
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
[Trait(WpfCollection.Category, WpfCollection.Trait)]
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
