using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Synthesis;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Чтение ответов вслух синтезатором Windows (System.Speech, SAPI): на этом ПК, без сети и денег.
/// </summary>
/// <remarks>
/// <para>
/// Синтезатор создаётся при первом чтении, а не при запуске: сборка SAPI и голоса грузятся
/// заметное время, а читать вслух в этом запуске могут и не попросить.
/// </para>
/// <para>
/// Голос — выбранный в настройках, а если он не задан или удалён из Windows — первый установленный
/// голос языка текста (кириллица — русский), иначе голос Windows по умолчанию. Читает один источник
/// за раз: новое чтение обрывает прежнее.
/// </para>
/// <para>
/// System.Speech видит только голоса SAPI. Голоса OneCore из «Параметров» Windows ему не видны,
/// поэтому русского голоса может не оказаться — тогда текст читает голос по умолчанию.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class SpeechOutput : IDisposable
{
    private SpeechSynthesizer? _synthesizer;
    private Prompt? _current;

    /// <summary>Чтение закончилось само (true) или его остановили (false).</summary>
    public event Action<bool>? Finished;

    /// <summary>Что сейчас читается: ключ источника (id сообщения) или null.</summary>
    public string? Speaking { get; private set; }

    /// <summary>Имена установленных голосов — для выбора в настройках.</summary>
    public static IReadOnlyList<string> Voices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return [.. synthesizer.GetInstalledVoices().Where(voice => voice.Enabled).Select(voice => voice.VoiceInfo.Name)];
    }

    /// <summary>Шаг скорости из настройки — в шкалу SAPI (−10…10).</summary>
    public static int Rate(int setting) => Math.Clamp(setting, -10, 10);

    public void Speak(string key, string text, string? voice, int rate)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(text))
        {
            Finished?.Invoke(true);
            return;
        }

        var synthesizer = _synthesizer ??= Create();
        Choose(synthesizer, voice, text);
        synthesizer.Rate = Rate(rate);
        Speaking = key;
        _current = synthesizer.SpeakAsync(text);
    }

    public void Stop()
    {
        if (_synthesizer is null || _current is null)
        {
            return;
        }

        _current = null;
        Speaking = null;
        _synthesizer.SpeakAsyncCancelAll();
    }

    public void Dispose()
    {
        _current = null;
        Speaking = null;
        _synthesizer?.Dispose();
        _synthesizer = null;
    }

    private SpeechSynthesizer Create()
    {
        var synthesizer = new SpeechSynthesizer();
        synthesizer.SetOutputToDefaultAudioDevice();

        // Событие приходит в контекст потока, создавшего синтезатор, — здесь это поток окна.
        synthesizer.SpeakCompleted += (_, e) =>
        {
            if (!ReferenceEquals(e.Prompt, _current) && _current is not null)
            {
                return;
            }

            var completed = !e.Cancelled && e.Error is null && _current is not null;
            _current = null;
            Speaking = null;
            Finished?.Invoke(completed);
        };
        return synthesizer;
    }

    private static void Choose(SpeechSynthesizer synthesizer, string? voice, string text)
    {
        var installed = synthesizer.GetInstalledVoices().Where(item => item.Enabled).Select(item => item.VoiceInfo).ToList();
        var chosen = installed.FirstOrDefault(item => string.Equals(item.Name, voice, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(item => item.Culture.TwoLetterISOLanguageName == Language(text))
            ?? installed.FirstOrDefault();
        if (chosen is not null)
        {
            synthesizer.SelectVoice(chosen.Name);
        }
    }

    /// <summary>Язык текста по буквам: кириллица — русский, иначе язык интерфейса Windows.</summary>
    private static string Language(string text)
    {
        var letters = text.Where(char.IsLetter).Take(400).ToList();
        var cyrillic = letters.Count(ch => ch is >= 'А' and <= 'я' or 'ё' or 'Ё');
        return letters.Count > 0 && cyrillic * 2 >= letters.Count ? "ru" : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    }
}
