namespace Amarin.Core;

/// <summary>Настройки лисёнка Розы (1.35.0). Сам Роза живёт в сборке <c>Amarin.Fox</c> (папка ProjectFox).</summary>
/// <remarks>
/// Здесь, а не в ProjectFox: настройки — часть <see cref="AppSettings"/>, а ядро не может ссылаться
/// на сборку Розы. Это настройки профиля, а не машины: с архивом данных они уезжают целиком.
/// </remarks>
public sealed class FoxSettings
{
    /// <summary>Маленький Роза: кадр 48 точек в натуральную величину, сидящий — 46 точек.</summary>
    public const int SmallSize = 48;

    /// <summary>
    /// Средний — заводской: кадр в полтора раза больше, сидящий Роза — около 70 точек. Ровно
    /// посередине между маленьким и крупным.
    /// </summary>
    public const int MediumSize = 72;

    /// <summary>Крупный Роза: тот же кадр, пиксель рисунка — два пикселя экрана.</summary>
    public const int LargeSize = 96;

    /// <summary>Все размеры по порядку: строки выбора на подстранице и сверка руками поправленного файла.</summary>
    public static readonly int[] Sizes = [SmallSize, MediumSize, LargeSize];

    /// <summary>Показывать Розу в окне. Выключенный не загружается вовсе и денег не тратит.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Сторона кадра на экране в точках: одно из <see cref="Sizes"/>.</summary>
    public int Size { get; set; } = MediumSize;

    /// <summary>Шалить, когда разозлили или скучно: тыкать «Новый чат», толкать колонку чатов.</summary>
    public bool Mischief { get; set; } = true;

    /// <summary>
    /// Читать начало ваших сообщений своей моделью, чтобы реагировать на смысл. Выключено —
    /// реагирует только по словам, без запросов к модели, а дневник пишет по шаблону.
    /// </summary>
    public bool ReadChat { get; set; } = true;

    /// <summary>Искать в интернете, когда с ним разговаривают.</summary>
    public bool WebSearch { get; set; } = true;

    /// <summary>
    /// Думать о своём своей моделью: не чаще раза в 20 минут и не больше 24 раз в день, без
    /// текста переписки. Каждая мысль — запрос, поэтому выключатель отдельный.
    /// </summary>
    public bool Thoughts { get; set; } = true;

    /// <summary>
    /// Приводит руками поправленный файл к допустимым значениям. <c>true</c> — что-то поменялось.
    /// </summary>
    public bool Normalize()
    {
        if (Sizes.Contains(Size))
        {
            return false;
        }

        // Ближайший из допустимых; посередине — меньший.
        var written = Size;
        Size = Sizes.OrderBy(size => Math.Abs(size - written)).ThenBy(size => size).First();
        return true;
    }
}
