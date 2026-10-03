namespace Amarin.Core;

/// <summary>Что рисуется за всем окном, под полупрозрачными панелями.</summary>
public enum BackdropMode
{
    /// <summary>Ничего: <c>Bg.Window</c> палитры остаётся непрозрачным. Дешевле всего, по умолчанию.</summary>
    None,
    Gradient,
    Image
}

/// <summary>Как движется фон-градиент. С выключенными анимациями движения нет вовсе.</summary>
public enum BackdropMotion
{
    None,

    /// <summary>Ось градиента медленно вращается.</summary>
    Drift,

    /// <summary>Цвета скользят вдоль оси, как свет по стеклу.</summary>
    Aurora,

    /// <summary>Весь фон «дышит» между двумя уровнями яркости.</summary>
    Pulse
}

/// <summary>How a background image is fitted to the window.</summary>
public enum BackdropFit
{
    /// <summary>Заполнить окно, обрезав лишнее. Почти всегда это и нужно.</summary>
    Fill,

    /// <summary>Вписать картинку целиком, оставив поля.</summary>
    Fit,

    /// <summary>Repeat the picture at its own size.</summary>
    Tile
}

/// <summary>
/// Всё со страницы «Оформление», кроме самой палитры: фон, «стекло» панелей, свой акцент и
/// настройки раскладки.
/// <para>
/// Хранится внутри <see cref="AppSettings"/>: старый settings.json просто читается со значениями
/// по умолчанию ниже, а они в точности повторяют заводской вид.
/// </para>
/// </summary>
public sealed class AppearanceSettings
{
    /// <summary>Общий выключатель. Выключено — только палитра темы, без фона и стекла.</summary>
    public bool Enabled { get; set; }

    /// <summary>Свой акцент, <c>#RRGGBB</c>. Пусто — акцент темы.</summary>
    public string AccentColor { get; set; } = "";

    public BackdropMode BackdropMode { get; set; } = BackdropMode.None;

    // ───────────────────────── Градиент ─────────────────────────

    /// <summary>От двух до пяти цветов <c>#RRGGBB</c> по порядку вдоль оси.</summary>
    public List<string> GradientColors { get; set; } = ["#1B2735", "#2C5364", "#0F2027"];

    /// <summary>Axis direction in degrees, 0 = left→right, 90 = top→bottom.</summary>
    public double GradientAngle { get; set; } = 135;

    public BackdropMotion GradientMotion { get; set; } = BackdropMotion.None;

    /// <summary>Множитель скорости движения, 0.25..3. При 1.0 полный цикл — 40 с.</summary>
    public double MotionSpeed { get; set; } = 1.0;

    // ───────────────────────── Изображение ─────────────────────────

    /// <summary>Путь к картинке. Нет файла — рисуется градиент.</summary>
    public string BackgroundImagePath { get; set; } = "";

    public BackdropFit ImageFit { get; set; } = BackdropFit.Fill;

    /// <summary>0.2..1.6. Below 1 the picture is dimmed, above 1 it is lifted.</summary>
    public double ImageBrightness { get; set; } = 0.75;

    /// <summary>0 — серое, 1 — как есть, 2 — вдвое насыщеннее. Стоит прохода по пикселям, поэтому применяется с паузой.</summary>
    public double ImageSaturation { get; set; } = 1.0;

    /// <summary>Радиус размытия в независимых пикселях, 0..80 — «толщина» стекла.</summary>
    public double ImageBlur { get; set; } = 14;

    // ───────────────────────── Стекло ─────────────────────────

    /// <summary>
    /// Насколько плотны панели над фоном, 0.15..1. Меньше — больше картинки видно; 1 не отличить
    /// от окна без фона.
    /// </summary>
    public double GlassOpacity { get; set; } = 0.62;

    /// <summary>Дополнительная матовость поверх фона, 0..0.6. Смягчает пёстрые картинки.</summary>
    public double GlassFrost { get; set; } = 0.12;

    /// <summary>Диагональный отблеск по фону — блик «стекла».</summary>
    public bool GlassSheen { get; set; } = true;

    /// <summary>Затемнять края окна, чтобы текст чата не терял контраст.</summary>
    public bool Vignette { get; set; } = true;

    // ───────────────────────── Компоновка ─────────────────────────

    /// <summary>Радиус углов поля ввода, 0..20. 6 — заводской вид.</summary>
    public double CornerRadius { get; set; } = 6;

    /// <summary>
    /// Матовое зерно поверх окна, 0..1. <c>null</c> — брать значение темы: матовые пресеты
    /// просят своё, остальные тридцать один — ноль, и выглядят ровно как раньше.
    /// <para>
    /// Нужен именно nullable, а не ноль по умолчанию: иначе "я ничего не выбирал" и
    /// "я увёл ползунок в ноль" неразличимы, и выбор темы Matte либо молча перетирал бы
    /// осознанный ноль, либо никогда не показывал бы зерно. Двигая ползунок, человек
    /// переходит на явное значение; «Сбросить оформление» возвращает null, то есть снова
    /// к теме.
    /// </para>
    /// </summary>
    public double? Grain { get; set; }

    /// <summary>
    /// Шрифт интерфейса. Пусто — шрифт Windows; иначе один из шрифтов в <c>Fonts/</c>:
    /// <c>Urbanist</c>, <c>Outfit</c>, <c>Rubik</c>, <c>Arimo</c>.
    /// </summary>
    public string FontFamily { get; set; } = "";

    /// <summary>Наибольшая ширина колонки сообщений, в DIP. 1070 — заводское значение.</summary>
    public double ChatColumnWidth { get; set; } = 1070;

    /// <summary>Выключено — фон не движется, поле ввода сворачивается без анимации.</summary>
    public bool AnimationsEnabled { get; set; } = true;

    // ───────────────────────── Компактный ввод ─────────────────────────

    /// <summary>Сворачивать пустое и оставленное поле ввода в полоску.</summary>
    public bool CompactComposer { get; set; }

    /// <summary>Idle time before the composer folds up, 300..5000 ms.</summary>
    public int CompactDelayMs { get; set; } = 800;

    /// <summary>Pill width as a share of the expanded composer, 30..100 %.</summary>
    public double CompactWidthPercent { get; set; } = 58;

    /// <summary>На сколько пикселей нужно поднести указатель, чтобы полоска развернулась. 25..400.</summary>
    public double CompactHoverRadius { get; set; } = 40;

    /// <summary>
    /// Реагирует ли полоска на мышь. Выключено — указатель не учитывается: наведение её не
    /// разворачивает, уход не сворачивает; она открывается, когда в неё ставят каретку, и
    /// сворачивается, когда фокус ушёл, а она пуста.
    /// </summary>
    public bool CompactHoverEnabled { get; set; } = true;

    public AppearanceSettings Clone() => new()
    {
        Enabled = Enabled,
        AccentColor = AccentColor,
        BackdropMode = BackdropMode,
        GradientColors = [.. GradientColors],
        GradientAngle = GradientAngle,
        GradientMotion = GradientMotion,
        MotionSpeed = MotionSpeed,
        BackgroundImagePath = BackgroundImagePath,
        ImageFit = ImageFit,
        ImageBrightness = ImageBrightness,
        ImageSaturation = ImageSaturation,
        ImageBlur = ImageBlur,
        GlassOpacity = GlassOpacity,
        GlassFrost = GlassFrost,
        GlassSheen = GlassSheen,
        Vignette = Vignette,
        CornerRadius = CornerRadius,
        Grain = Grain,
        FontFamily = FontFamily,
        ChatColumnWidth = ChatColumnWidth,
        AnimationsEnabled = AnimationsEnabled,
        CompactComposer = CompactComposer,
        CompactDelayMs = CompactDelayMs,
        CompactWidthPercent = CompactWidthPercent,
        CompactHoverRadius = CompactHoverRadius,
        CompactHoverEnabled = CompactHoverEnabled
    };

    /// <summary>
    /// Загоняет числа в их диапазоны и выбрасывает негодные цвета. Зовётся при чтении: поправленный
    /// руками settings.json не сломает окно.
    /// </summary>
    /// <returns><c>true</c>, если что-то пришлось поправить.</returns>
    public bool Normalize()
    {
        var before = Describe();

        GradientAngle = Wrap360(GradientAngle);
        MotionSpeed = Clamp(MotionSpeed, 0.25, 3.0);
        ImageBrightness = Clamp(ImageBrightness, 0.2, 1.6);
        ImageSaturation = Clamp(ImageSaturation, 0.0, 2.0);
        ImageBlur = Clamp(ImageBlur, 0, 80);
        GlassOpacity = Clamp(GlassOpacity, 0.15, 1.0);
        GlassFrost = Clamp(GlassFrost, 0.0, 0.6);
        CornerRadius = Clamp(CornerRadius, 0, 20);
        if (Grain is { } grain)
        {
            Grain = Clamp(grain, 0, 1);
        }

        ChatColumnWidth = Clamp(ChatColumnWidth, 480, 100000);
        CompactDelayMs = (int)Clamp(CompactDelayMs, 300, 5000);
        CompactWidthPercent = Clamp(CompactWidthPercent, 30, 100);
        CompactHoverRadius = Clamp(CompactHoverRadius, 25, 400);

        AccentColor = NormalizeHex(AccentColor);

        GradientColors = [.. GradientColors.Select(NormalizeHex).Where(c => c.Length > 0).Take(5)];
        while (GradientColors.Count < 2)
        {
            GradientColors.Add(GradientColors.Count == 0 ? "#1B2735" : "#0F2027");
        }

        return Describe() != before;
    }

    /// <summary>
    /// Принимает <c>RGB</c>, <c>RRGGBB</c> и <c>AARRGGBB</c> с решёткой и без, возвращает
    /// <c>#RRGGBB</c>; всё прочее — пустая строка.
    /// </summary>
    public static string NormalizeHex(string? value)
    {
        var text = (value ?? "").Trim().TrimStart('#');
        if (text.Length is not (3 or 6 or 8) || !text.All(Uri.IsHexDigit))
        {
            return "";
        }

        if (text.Length == 3)
        {
            text = string.Concat(text.Select(c => new string(c, 2)));
        }
        else if (text.Length == 8)
        {
            text = text[2..];
        }

        return "#" + text.ToUpperInvariant();
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Clamp(value, min, max);

    private static double Wrap360(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        var wrapped = value % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }

    private string Describe() =>
        string.Join('|',
            GradientAngle, MotionSpeed, ImageBrightness, ImageSaturation, ImageBlur,
            GlassOpacity, GlassFrost, CornerRadius, ChatColumnWidth, CompactDelayMs, CompactWidthPercent,
            CompactHoverRadius, AccentColor, Grain, string.Join(',', GradientColors));
}
