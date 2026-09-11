namespace Amarin.Core;

/// <summary>
/// Строки интерфейса для кода. Разметка берёт их напрямую через
/// <c>{DynamicResource S.Ключ}</c>, а здесь тот же набор, но доступный из C#.
/// </summary>
/// <remarks>
/// Класс в Core и без WPF — им пользуются и типы, которым интерфейс не положено знать
/// (<see cref="ReasoningPolicy"/>, каталог моделей, проверка обновлений). Наполняет его
/// <c>LanguageManager</c> из того же словаря, который подставляет в ресурсы приложения, — иначе
/// строки в разметке и в коде разъехались бы.
/// </remarks>
internal static class Loc
{
    /// <summary>Название текущего языка на нём самом: «Русский», «English», «日本語».</summary>
    /// <remarks>
    /// Не для показа, а для подстановки в промпты: модель должна знать, на каком языке писать.
    /// Перевод на новый язык кладёт сюда имя, которое человек назвал в диалоге.
    /// </remarks>
    public const string LanguageNameKey = "S.Language.NativeName";

    private static readonly Lock Gate = new();

    // Русский как запас: без интерфейса — в тестах, в консольном прогоне — словарь языка никто
    // не подставляет, и подпись выродилась бы в сам ключ.
    private static Dictionary<string, string> _strings =
        new(StringsRu.Values, StringComparer.Ordinal);

    /// <summary>Заменяет весь набор строк. Зовётся при старте и при смене языка.</summary>
    public static void Use(IReadOnlyDictionary<string, string> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        lock (Gate)
        {
            // Русский снизу и здесь: у перевода может не хватать ключа, и подпись обязана
            // остаться — так же, как это делает словарь ресурсов.
            _strings = new Dictionary<string, string>(StringsRu.Values, StringComparer.Ordinal);
            foreach (var (key, value) in strings)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _strings[key] = value;
                }
            }
        }
    }

    /// <summary>
    /// Строка по ключу. Если ключа нет — возвращается <paramref name="fallback"/>, а без него
    /// сам ключ: пустая подпись в интерфейсе хуже, чем видимое «S.Что.То», по которому сразу
    /// понятно, что забыли.
    /// </summary>
    public static string Get(string key, string? fallback = null)
    {
        lock (Gate)
        {
            if (_strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return fallback ?? key;
    }

    /// <summary>Строка с подстановками: «Пропущено файлов: {0}».</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(Get(key), args);

    public static bool Has(string key)
    {
        lock (Gate)
        {
            return _strings.ContainsKey(key);
        }
    }
}
