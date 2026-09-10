namespace Amarin.Core;

/// <summary>What is painted behind the whole window, under the translucent panels.</summary>
public enum BackdropMode
{
    /// <summary>Nothing — the palette's <c>Bg.Window</c> stays opaque. Cheapest, the default.</summary>
    None,
    Gradient,
    Image
}

/// <summary>How a gradient backdrop moves. All motion stops when animations are off.</summary>
public enum BackdropMotion
{
    None,

    /// <summary>The gradient axis slowly rotates around the window.</summary>
    Drift,

    /// <summary>Stops slide along the axis, like light moving across glass.</summary>
    Aurora,

    /// <summary>The whole backdrop breathes between two brightness levels.</summary>
    Pulse
}

/// <summary>How a background image is fitted to the window.</summary>
public enum BackdropFit
{
    /// <summary>Cover the window, cropping the overflow. Almost always what you want.</summary>
    Fill,

    /// <summary>Fit the whole picture inside, letterboxing the rest.</summary>
    Fit,

    /// <summary>Repeat the picture at its own size.</summary>
    Tile
}

/// <summary>
/// Everything on the Appearance page that is not the palette preset itself: the backdrop,
/// the "behind glass" treatment of the panels, the accent override and the layout knobs.
/// <para>
/// Persisted inside <see cref="AppSettings"/>, so an old settings.json simply deserialises
/// to the defaults below — which reproduce the shipped look exactly.
/// </para>
/// </summary>
public sealed class AppearanceSettings
{
    /// <summary>Master switch. Off means: preset palette only, no backdrop, no glass.</summary>
    public bool Enabled { get; set; }

    /// <summary>Accent override as <c>#RRGGBB</c>. Empty keeps the preset's own accent.</summary>
    public string AccentColor { get; set; } = "";

    public BackdropMode BackdropMode { get; set; } = BackdropMode.None;

    // ───────── Градиент ─────────

    /// <summary>Two to five stops, <c>#RRGGBB</c>, painted in order along the axis.</summary>
    public List<string> GradientColors { get; set; } = ["#1B2735", "#2C5364", "#0F2027"];

    /// <summary>Axis direction in degrees, 0 = left→right, 90 = top→bottom.</summary>
    public double GradientAngle { get; set; } = 135;

    public BackdropMotion GradientMotion { get; set; } = BackdropMotion.None;

    /// <summary>Motion speed multiplier, 0.25..3. One full cycle at 1.0 takes 40 s.</summary>
    public double MotionSpeed { get; set; } = 1.0;

    // ───────── Изображение ─────────

    /// <summary>Absolute path to the picture. A missing file falls back to the gradient.</summary>
    public string BackgroundImagePath { get; set; } = "";

    public BackdropFit ImageFit { get; set; } = BackdropFit.Fill;

    /// <summary>0.2..1.6. Below 1 the picture is dimmed, above 1 it is lifted.</summary>
    public double ImageBrightness { get; set; } = 0.75;

    /// <summary>0 = greyscale, 1 = original, 2 = doubled. Costs a pixel pass, so it is debounced.</summary>
    public double ImageSaturation { get; set; } = 1.0;

    /// <summary>Blur radius in device-independent pixels, 0..80 — the "thickness" of the crystal.</summary>
    public double ImageBlur { get; set; } = 14;

    // ───────── Стекло ─────────

    /// <summary>
    /// How solid the panels are over the backdrop, 0.15..1. Lower lets more of the picture
    /// through; 1 is indistinguishable from no backdrop at all.
    /// </summary>
    public double GlassOpacity { get; set; } = 0.62;

    /// <summary>Extra frost laid over the backdrop itself, 0..0.6. Softens busy pictures.</summary>
    public double GlassFrost { get; set; } = 0.12;

    /// <summary>Diagonal sheen across the backdrop — the "crystal" highlight.</summary>
    public bool GlassSheen { get; set; } = true;

    /// <summary>Darken the window edges so the chat text keeps its contrast.</summary>
    public bool Vignette { get; set; } = true;

    // ───────── Компоновка ─────────

    /// <summary>Composer corner radius, 0..20. 6 matches the shipped look.</summary>
    public double CornerRadius { get; set; } = 6;

    /// <summary>
    /// Interface font. Empty means the Windows default; otherwise one of the families shipped in
    /// <c>Fonts/</c> — <c>Urbanist</c>, <c>Outfit</c>, <c>Rubik</c>, <c>Arimo</c>.
    /// </summary>
    public string FontFamily { get; set; } = "";

    /// <summary>Maximum width of the message column, in DIPs. 1070 is the shipped value.</summary>
    public double ChatColumnWidth { get; set; } = 1070;

    /// <summary>Off freezes every backdrop motion and the composer collapse animation.</summary>
    public bool AnimationsEnabled { get; set; } = true;

    // ───────── Компактный ввод ─────────

    /// <summary>Collapse the composer to a pill while it is empty and unattended.</summary>
    public bool CompactComposer { get; set; }

    /// <summary>Idle time before the composer folds up, 300..5000 ms.</summary>
    public int CompactDelayMs { get; set; } = 800;

    /// <summary>Pill width as a share of the expanded composer, 30..100 %.</summary>
    public double CompactWidthPercent { get; set; } = 58;

    /// <summary>How close the pointer must come, in pixels, to unfold the pill. 25..400.</summary>
    public double CompactHoverRadius { get; set; } = 40;

    /// <summary>
    /// Does the pill react to the mouse at all. Off means the pointer is ignored entirely —
    /// no hover unfolds it and nothing folds it back when the pointer leaves; it opens when the
    /// caret goes into it and folds again once focus moves away and it is empty.
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
    /// Clamps every numeric field into its documented range and drops unusable colours.
    /// Called on load, so a hand-edited settings.json can never render the window unusable.
    /// </summary>
    /// <returns><c>true</c> when something had to be corrected.</returns>
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
    /// Accepts <c>RGB</c>, <c>RRGGBB</c> and <c>AARRGGBB</c> with or without the hash and
    /// returns the canonical <c>#RRGGBB</c>; anything else becomes the empty string.
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
            CompactHoverRadius, AccentColor, string.Join(',', GradientColors));
}
