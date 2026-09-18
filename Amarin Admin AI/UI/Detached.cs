using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Запуск работы, которую никто не ждёт: обновить список, сходить за объяснением, проверить
/// обновление.
/// </summary>
/// <remarks>
/// Такие вызовы писались как <c>_ = SomethingAsync()</c>, и отказ в них исчезал бесследно —
/// задачу никто не наблюдает, а <c>TaskScheduler</c> об этом молчит. Здесь он хотя бы попадает
/// в журнал аварий, откуда его можно достать, разбирая жалобу. Человеку при этом не показывается
/// ничего: он этой работы не заказывал.
/// </remarks>
internal static class Detached
{
    /// <param name="what">Что запускали — попадёт в запись журнала рядом с отказом.</param>
    public static void Run(Task task, string what)
    {
        ArgumentNullException.ThrowIfNull(task);

        _ = task.ContinueWith(
            finished => CrashLog.Write($"detached {what}: {finished.Exception!.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
