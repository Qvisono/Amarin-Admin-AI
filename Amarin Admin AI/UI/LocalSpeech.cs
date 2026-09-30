using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Recognition;

namespace Amarin.UI;

/// <summary>
/// Распознавание речи на этом ПК (D14) — распознаватели SAPI, которые установлены в Windows.
/// Языков у них немного и какие именно — зависит от системы; список берётся у самой Windows,
/// а недоступный язык честно называется недоступным.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LocalSpeech
{
    /// <summary>Языки установленных распознавателей. Пусто — локально распознавать нечем.</summary>
    public static IReadOnlyList<CultureInfo> Languages()
    {
        try
        {
            return SpeechRecognitionEngine.InstalledRecognizers().Select(info => info.Culture).Distinct().ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    /// <summary>
    /// Распознаватель для языка: точное совпадение, иначе тот же язык другой страны (en-GB для en-US).
    /// </summary>
    public static RecognizerInfo? Find(CultureInfo culture)
    {
        try
        {
            var all = SpeechRecognitionEngine.InstalledRecognizers();
            return all.FirstOrDefault(info => info.Culture.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(info => info.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Текст из WAV. Для рабочего потока: распознаватель работает синхронно.</summary>
    public static Task<string> RecognizeAsync(byte[] wav, RecognizerInfo recognizer, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var engine = new SpeechRecognitionEngine(recognizer);
            engine.LoadGrammar(new DictationGrammar());
            using var stream = new MemoryStream(wav);
            engine.SetInputToWaveStream(stream);

            // Весь поток разом: по кускам между паузами, пока не кончится файл.
            var parts = new List<string>();
            var done = new ManualResetEventSlim();
            engine.SpeechRecognized += (_, e) => parts.Add(e.Result.Text);
            engine.RecognizeCompleted += (_, _) => done.Set();
            engine.RecognizeAsync(RecognizeMode.Multiple);
            using (cancellationToken.Register(() => engine.RecognizeAsyncCancel()))
            {
                done.Wait(cancellationToken);
            }

            return string.Join(" ", parts).Trim();
        }, cancellationToken);
}
