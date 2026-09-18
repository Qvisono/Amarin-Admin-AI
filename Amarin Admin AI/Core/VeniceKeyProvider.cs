namespace Amarin.Core;

/// <summary>
/// Ключ Venice, которым программа платит прямо сейчас. Одна штука на весь процесс.
/// </summary>
/// <remarks>
/// Отдельный объект, а не поле в <see cref="AgentOptions"/>, потому что настройки раздаются
/// копиями: агент, маршрутизатор, заголовок чата и сводка собирают себе свои
/// <see cref="AgentOptions"/> и разъехались бы на ключе, с которым программа запустилась.
/// Провайдер у всех копий общий, поэтому смена ключа доходит до них сразу.
/// <para>
/// Читают его из разных потоков — ход чата, прогон агента и страница настроек живут
/// параллельно, — поэтому поле <c>volatile</c>: рвать здесь замок незачем, присваивание
/// ссылки и так атомарно, а видимость даёт именно <c>volatile</c>.
/// </para>
/// </remarks>
public sealed class VeniceKeyProvider
{
    private volatile string _current;

    public VeniceKeyProvider(string initial = "") => _current = initial ?? string.Empty;

    /// <summary>Ключ для следующего запроса. Пустая строка — ключа нет вовсе.</summary>
    public string Current => _current;

    /// <summary>
    /// Поднимается только при настоящей смене. На него подписаны вещи, которые после смены
    /// ключа врут: остаток на плашке и список моделей — у другого ключа другой тариф.
    /// </summary>
    public event Action<string>? Changed;

    public void Use(string? key)
    {
        var next = key ?? string.Empty;
        if (string.Equals(_current, next, StringComparison.Ordinal))
        {
            return;
        }

        _current = next;
        Changed?.Invoke(next);
    }
}
