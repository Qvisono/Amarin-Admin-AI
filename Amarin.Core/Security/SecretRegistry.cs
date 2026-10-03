namespace Amarin.Core;

/// <summary>
/// Секреты, которые вырезаются из отчёта о сбое и из строк журнала аудита: ключи из окружения и
/// все ключи профиля.
/// </summary>
/// <remarks>
/// Один на программу, заводит его корень композиции. До 1.30.0 это было общее изменяемое
/// <c>CrashHandler.Secrets</c> в слое окна: писали в него и запуск, и службы при смене ключа, а
/// журнал аудита читал его лямбдой через слой. Список подменяется целиком, поэтому читателю
/// замок не нужен — он берёт ссылку и работает с ней.
/// </remarks>
internal sealed class SecretRegistry
{
    private volatile IReadOnlyList<string?> _secrets = [];

    /// <summary>Текущий список. Пустые и null — допустимы: их вырезать нечего.</summary>
    public IReadOnlyList<string?> Current => _secrets;

    /// <summary>Заменяет список целиком.</summary>
    public void Use(IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = [.. secrets];
    }
}
