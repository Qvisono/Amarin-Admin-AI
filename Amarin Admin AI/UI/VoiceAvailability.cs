using System.Globalization;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Чем сейчас будет распознаваться речь — для микрофона и строки состояния в настройках.</summary>
internal enum VoiceRoute
{
    /// <summary>Распознаватель Windows для языка речи.</summary>
    Local,

    /// <summary>Облачная модель, для провайдера которой есть ключ.</summary>
    Cloud,

    /// <summary>Выбрано «На этом ПК», а распознавателя для языка в Windows нет.</summary>
    NoLocal,

    /// <summary>Облако нужно, но ни модели с ключом, ни ключа Venice или OpenRouter нет.</summary>
    NoKey
}

/// <summary>
/// Есть ли чем распознать речь (D14) — без записи. По нему микрофон у поля ввода показывается или
/// прячется: кнопка, которая после записи отвечает «не настроено», только отнимала время.
/// </summary>
/// <remarks>
/// <para>
/// Чистая функция от настроек: распознаватель Windows и наличие ключа приходят делегатами, чтобы
/// правило проверялось тестом без SAPI и без ключей. Правило то же, что у распознавания:
/// «Авто» и «На этом ПК» берут распознаватель Windows для языка, «Авто» и «В облаке» — облачную
/// модель, если для неё есть ключ.
/// </para>
/// <para>
/// Облачная модель без выбора человека — Whisper Large V3 у Venice, иначе у OpenRouter (1.37.0):
/// до этого поле модели было пустым, и у человека с ключом, но без распознавателя Windows для
/// своего языка (русского в Windows нет вовсе) микрофон не появлялся ни при каких настройках.
/// </para>
/// </remarks>
internal static class VoiceAvailability
{
    /// <summary>Модели по умолчанию, по порядку: первая, к провайдеру которой есть ключ.</summary>
    public static readonly string[] DefaultCloudModels = ["openai/whisper-large-v3", "openrouter:openai/whisper-large-v3"];

    /// <summary>Язык речи: из настройки, а пусто — язык интерфейса.</summary>
    public static CultureInfo SpeechCulture(AppSettings settings, string interfaceLanguage)
    {
        var language = string.IsNullOrWhiteSpace(settings.VoiceLanguage) ? interfaceLanguage : settings.VoiceLanguage!.Trim();
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentUICulture;
        }
    }

    /// <summary>Облачная модель: выбранная человеком, иначе первая из заводских, к которой есть ключ.</summary>
    public static string? CloudModel(AppSettings settings, Func<string, bool> hasKeyFor) =>
        settings.VoiceModel is { } chosen && chosen.Trim().Length > 0
            ? chosen.Trim()
            : DefaultCloudModels.FirstOrDefault(hasKeyFor);

    public static VoiceRoute Route(
        AppSettings settings,
        CultureInfo culture,
        Func<CultureInfo, bool> hasLocalRecognizer,
        Func<string, bool> hasKeyFor)
    {
        if (settings.VoiceEngine != VoiceEngine.Cloud && hasLocalRecognizer(culture))
        {
            return VoiceRoute.Local;
        }

        if (settings.VoiceEngine == VoiceEngine.Local)
        {
            return VoiceRoute.NoLocal;
        }

        return CloudModel(settings, hasKeyFor) is { } model && hasKeyFor(model) ? VoiceRoute.Cloud : VoiceRoute.NoKey;
    }

    public static bool IsAvailable(
        AppSettings settings,
        CultureInfo culture,
        Func<CultureInfo, bool> hasLocalRecognizer,
        Func<string, bool> hasKeyFor) =>
        Route(settings, culture, hasLocalRecognizer, hasKeyFor) is VoiceRoute.Local or VoiceRoute.Cloud;
}
