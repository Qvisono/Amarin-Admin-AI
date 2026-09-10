namespace Amarin.Core;

/// <summary>
/// Мост между <c>download_file</c> и окном: инструмент упёрся в белый список — спрашиваем
/// пользователя, добавить ли домен, и продолжаем ровно тогда, когда он согласился.
/// </summary>
/// <remarks>
/// Обработчик ставит окно, поэтому в тестах и в консольном режиме его нет — тогда запрос
/// сразу считается отклонённым и поведение остаётся прежним: домен заблокирован.
/// Режим «подтверждать всё автоматически» здесь намеренно ни при чём: это не разовое
/// действие, а постоянная запись в списке разрешённых источников.
/// </remarks>
public static class DownloadAccessBroker
{
    private static Func<string, CancellationToken, Task<bool>>? _handler;

    public static void SetHandler(Func<string, CancellationToken, Task<bool>>? handler) => _handler = handler;

    public static bool CanAsk => _handler is not null;

    /// <summary><c>true</c>, когда пользователь разрешил домен; список к этому моменту уже обновлён.</summary>
    public static async Task<bool> RequestAsync(string host, CancellationToken cancellationToken = default)
    {
        var handler = _handler;
        if (handler is null || string.IsNullOrWhiteSpace(host) || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            return await handler(host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
