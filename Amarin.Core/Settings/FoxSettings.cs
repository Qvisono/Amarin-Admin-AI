namespace Amarin.Core;

/// <summary>Настройки лисёнка Розы (1.35.0). Сам Роза живёт в сборке <c>Amarin.Fox</c> (папка ProjectFox).</summary>
/// <remarks>
/// Здесь, а не в ProjectFox: настройки — часть <see cref="AppSettings"/>, а ядро не может ссылаться
/// на сборку Розы. Это настройки профиля, а не машины: с архивом данных они уезжают целиком.
/// </remarks>
public sealed class FoxSettings
{
    /// <summary>Маленький Роза: кадр 48 точек в натуральную величину.</summary>
    public const int SmallSize = 48;

    /// <summary>Крупный Роза: тот же кадр, пиксель рисунка — два пикселя экрана.</summary>
    public const int LargeSize = 96;

    /// <summary>Показывать Розу в окне. Выключенный не загружается вовсе и денег не тратит.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Размер на экране в точках: <see cref="SmallSize"/> или <see cref="LargeSize"/>.</summary>
    public int Size { get; set; } = SmallSize;

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
    /// Приводит руками поправленный файл к допустимым значениям. <c>true</c> — что-то поменялось.
    /// </summary>
    public bool Normalize()
    {
        if (Size is SmallSize or LargeSize)
        {
            return false;
        }

        Size = Size > (SmallSize + LargeSize) / 2 ? LargeSize : SmallSize;
        return true;
    }
}
