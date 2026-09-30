using System.Windows;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Шрифты ленты (I3): размер текста сообщений и моноширинный шрифт кода.
/// </summary>
/// <remarks>
/// <para>
/// Статическое изменяемое состояние, как <see cref="UiScale"/>: ленту строят статические
/// помощники (<see cref="ChatMarkdown"/>, <see cref="CodeBlockView"/>), и протаскивать шрифт
/// параметром через каждый их вызов значило бы переписать их все. Тест, который меняет эти
/// значения, обязан вернуть прежние.
/// </para>
/// <para>
/// Интерлиньяж и размер кода считаются от размера текста в тех же пропорциях, что были
/// зашиты раньше (13,5 / 21 / 19 / 12,5): лента при заводском размере выглядит как прежде.
/// </para>
/// </remarks>
internal static class ChatFonts
{
    public const double DefaultSize = 13.5;
    public const double MinSize = 11;
    public const double MaxSize = 20;

    /// <summary>Заводской моноширинный: Consolas есть в любой Windows, Cascadia — не в каждой Windows 10.</summary>
    public const string DefaultMono = "consolas";

    private static FontFamily _mono = Family(DefaultMono);

    public static double BodySize { get; private set; } = DefaultSize;

    /// <summary>Интерлиньяж ответа модели.</summary>
    public static double AssistantLine => Math.Round(BodySize * 21 / DefaultSize, 1);

    /// <summary>Интерлиньяж сообщения человека: пузырь плотнее.</summary>
    public static double UserLine => Math.Round(BodySize * 19 / DefaultSize, 1);

    public static double CodeSize => Math.Round(BodySize * 12.5 / DefaultSize, 1);

    public static double CodeLine => Math.Round(BodySize * 18 / DefaultSize, 1);

    public static FontFamily Mono => _mono;

    public static Typeface MonoTypeface { get; private set; } = new(Family(DefaultMono), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Растёт на каждой смене: входит в ключи кэшей замера, чтобы старые ширины не пережили шрифт.</summary>
    public static int Version { get; private set; }

    /// <summary>Семейство по выбору. Через запятую — откат WPF: нет первого, берётся следующий.</summary>
    public static FontFamily Family(string? choice) => new(choice switch
    {
        "cascadia" => "Cascadia Mono, Consolas, Courier New",
        "courier" => "Courier New, Consolas",
        _ => "Consolas, Cascadia Mono, Courier New"
    });

    public static double Clamp(double? size) =>
        size is { } value && double.IsFinite(value) ? Math.Clamp(Math.Round(value * 2) / 2, MinSize, MaxSize) : DefaultSize;

    /// <summary>Применяет настройки. Возвращает, изменилось ли что-то — тогда ленту надо пересобрать.</summary>
    public static bool Apply(double? size, string? mono)
    {
        var nextSize = Clamp(size);
        var nextMono = Family(mono);
        if (nextSize == BodySize && nextMono.Source == _mono.Source)
        {
            return false;
        }

        BodySize = nextSize;
        _mono = nextMono;
        MonoTypeface = new Typeface(nextMono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Version++;
        return true;
    }

    public static bool Apply(AppSettings settings) => Apply(settings.ChatFontSize, settings.CodeFont);
}
