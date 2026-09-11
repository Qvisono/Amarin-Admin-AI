using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Владение замком единственного экземпляра. Освобождается вместе с процессом.</summary>
internal sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex? _mutex;

    internal SingleInstanceLock(Mutex? mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    /// <summary>Этот процесс — первый. Второй экземпляр обязан отдать работу первому и выйти.</summary>
    public bool IsOwner { get; }

    public void Dispose() => _mutex?.Dispose();
}

/// <summary>
/// Один процесс на пользователя: два экземпляра дерутся за <c>profiles.json</c> и
/// <c>chats/index.json</c>.
/// </summary>
/// <remarks>
/// Замок <c>Local\</c>, а не <c>Global\</c>: данные лежат в <c>%APPDATA%</c> конкретного
/// пользователя, и двое вошедших на одну машину законно запускают программу каждый.
/// Профиль в имя не входит — общие файлы лежат в корне независимо от того, какой профиль
/// активен, а активный выбирается уже после старта и меняется на ходу.
/// </remarks>
internal static class SingleInstance
{
    /// <summary>
    /// Имя литеральное, не из имени сборки: переименование сборки молча расщепило бы замок.
    /// И без версии — старый и новый exe это одна и та же программа.
    /// </summary>
    private const string LockName = @"Local\AmarinAdminAI.SingleInstance.{2f1c7a54-9d3e-4b60-8c11-5a7de0f4b912}";

    /// <summary>Сообщение «подними своё окно». Регистрируется по имени, поэтому число одно на систему.</summary>
    internal static readonly uint ActivateMessage =
        RegisterWindowMessage("AmarinAdminAI.Activate.{2f1c7a54}");

    /// <summary>
    /// Пытается взять замок. Владение определяется по <c>createdNew</c>, без <c>WaitOne</c>:
    /// так не нужно возиться с <c>AbandonedMutexException</c> — если первый процесс упал,
    /// ядро освободило хэндл и следующий запуск снова становится владельцем.
    /// </summary>
    public static SingleInstanceLock TryAcquire(string? name = null)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, name ?? LockName, out var createdNew);
            return new SingleInstanceLock(mutex, createdNew);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            // Замок недоступен — лучше запуститься вторым экземпляром, чем не запуститься вовсе.
            return new SingleInstanceLock(null, isOwner: true);
        }
    }

    /// <summary>Просит первый экземпляр показаться. Широковещательно: его HWND нам неизвестен.</summary>
    public static void Activate() =>
        PostMessage(HwndBroadcast, ActivateMessage, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Вешает на окно приём этого сообщения.
    /// </summary>
    /// <remarks>
    /// Окно создаётся до <c>Show()</c>, поэтому источник может быть ещё не готов — тогда
    /// цепляемся на <c>SourceInitialized</c>. Тот же приём, что в <see cref="WindowMaximizeFix"/>.
    /// </remarks>
    public static void Attach(Window window, Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(onActivate);

        if (!TryHook(window, onActivate))
        {
            window.SourceInitialized += OnSourceInitialized;
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            TryHook(window, onActivate);
        }
    }

    private static bool TryHook(Window window, Action onActivate)
    {
        // PresentationSource.FromVisual здесь возвращает null: дерево визуалов ещё не привязано
        // к источнику. Идём от хэндла — см. ту же ловушку в WindowMaximizeFix.
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source)
        {
            return false;
        }

        // Обработчик свой у каждого окна, а не общий статический: тесты поднимают по нескольку
        // окон, и один общий колбэк указывал бы на последнее созданное — в том числе закрытое.
        source.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if (ActivateMessage == 0 || msg != (int)ActivateMessage)
            {
                return IntPtr.Zero;
            }

            handled = true;
            try
            {
                onActivate();
            }
            catch (Exception ex)
            {
                PerfLog.Write("single_instance activate_failed " + ex.Message);
            }

            return IntPtr.Zero;
        });

        return true;
    }

    private static readonly IntPtr HwndBroadcast = 0xFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
