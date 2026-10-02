using System.Globalization;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Есть ли чем распознать речь (D14) — без записи. По нему микрофон у поля ввода показывается или
/// прячется: кнопка, которая после записи отвечает «не настроено», только отнимала время.
/// </summary>
/// <remarks>
/// Чистая функция от настроек: распознаватель Windows и наличие ключа приходят делегатами, чтобы
/// правило проверялось тестом без SAPI и без ключей. Правило то же, что у распознавания:
/// «Авто» и «На этом ПК» берут распознаватель Windows для языка, «Авто» и «В облаке» — модель
/// провайдера, если для неё есть ключ.
/// </remarks>
internal static class VoiceAvailability
{
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

    public static bool IsAvailable(
        AppSettings settings,
        CultureInfo culture,
        Func<CultureInfo, bool> hasLocalRecognizer,
        Func<string, bool> hasKeyFor)
    {
        if (settings.VoiceEngine != VoiceEngine.Cloud && hasLocalRecognizer(culture))
        {
            return true;
        }

        var model = settings.VoiceModel?.Trim();
        return settings.VoiceEngine != VoiceEngine.Local && !string.IsNullOrEmpty(model) && hasKeyFor(model);
    }
}
