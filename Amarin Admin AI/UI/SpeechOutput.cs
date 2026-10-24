using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Synthesis;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Чтение ответов вслух: синтезатор Windows (System.Speech, SAPI) или модель озвучки провайдера —
/// Venice или OpenRouter (1.37.0).
/// </summary>
/// <remarks>
/// <para>
/// Синтезатор Windows создаётся при первом чтении, а не при запуске: сборка SAPI и голоса грузятся
/// заметное время, а читать вслух в этом запуске могут и не попросить. Он видит только голоса SAPI,
/// поэтому русского голоса может не оказаться — тогда русский читает модель провайдера.
/// </para>
/// <para>
/// Модель провайдера берёт ограниченный кусок текста, поэтому ответ уходит частями по
/// предложениям (<see cref="SpeechText.Chunks"/>): следующая часть озвучивается, пока звучит
/// текущая, — паузы между ними нет. Звук пишется во временный файл и играет
/// <see cref="MediaPlayer"/> (MP3 он читает только из файла); файл удаляется, как только сыгран
/// или чтение остановили.
/// </para>
/// <para>
/// Читает один источник за раз: новое чтение обрывает прежнее.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class SpeechOutput : IDisposable
{
    /// <summary>Часть текста для модели: с запасом до 4096 знаков, которые принимает Venice.</summary>
    private const int CloudChunk = 3500;

    private SpeechSynthesizer? _synthesizer;
    private Prompt? _current;
    private MediaPlayer? _player;
    private CancellationTokenSource? _cloud;
    private string? _playing;

    /// <summary>Модель провайдера не смогла озвучить: текст для человека.</summary>
    public event Action<string>? Failed;

    /// <summary>Что сейчас читается: ключ источника (id сообщения) или null.</summary>
    public string? Speaking { get; private set; }

    /// <summary>Имена установленных голосов Windows — для выбора в настройках.</summary>
    public static IReadOnlyList<string> Voices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return [.. synthesizer.GetInstalledVoices().Where(voice => voice.Enabled).Select(voice => voice.VoiceInfo.Name)];
    }

    /// <summary>Шаг скорости из настройки — в шкалу SAPI (−10…10).</summary>
    public static int Rate(int setting) => Math.Clamp(setting, -10, 10);

    /// <summary>Шаг скорости из настройки — в множитель модели провайдера: «медленно» 0,8, «быстро» 1,25.</summary>
    public static double Speed(int setting) => setting switch
    {
        < 0 => 0.8,
        > 0 => 1.25,
        _ => 1.0
    };

    /// <summary>Читает синтезатором Windows.</summary>
    public void Speak(string key, string text, string? voice, int rate)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var synthesizer = _synthesizer ??= Create();
        Choose(synthesizer, voice, text);
        synthesizer.Rate = Rate(rate);
        Speaking = key;
        _current = synthesizer.SpeakAsync(text);
    }

    /// <summary>Читает моделью провайдера: <paramref name="synthesize"/> озвучивает одну часть текста.</summary>
    public void SpeakWithModel(string key, string text, Func<string, CancellationToken, Task<SpeechAudio>> synthesize)
    {
        Stop();
        var chunks = SpeechText.Chunks(text, CloudChunk);
        if (chunks.Count == 0)
        {
            return;
        }

        var cancel = _cloud = new CancellationTokenSource();
        Speaking = key;
        Detached.Run(PlayAsync(chunks, synthesize, cancel), "speech_cloud");
    }

    public void Stop()
    {
        if (_cloud is { } cloud)
        {
            _cloud = null;
            cloud.Cancel();
            _player?.Stop();
            Forget();
            Speaking = null;
            return;
        }

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
        _cloud?.Cancel();
        _cloud = null;
        _player?.Close();
        Forget();
        _current = null;
        Speaking = null;
        _synthesizer?.Dispose();
        _synthesizer = null;
    }

    private async Task PlayAsync(IReadOnlyList<string> chunks, Func<string, CancellationToken, Task<SpeechAudio>> synthesize, CancellationTokenSource cancel)
    {
        var token = cancel.Token;
        var pending = synthesize(chunks[0], token);
        try
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                var audio = await pending;

                // Следующая часть озвучивается, пока звучит эта: между частями нет паузы.
                if (i + 1 < chunks.Count)
                {
                    pending = synthesize(chunks[i + 1], token);
                }

                token.ThrowIfCancellationRequested();
                await PlayFileAsync(Write(audio), token);
            }

            if (ReferenceEquals(_cloud, cancel))
            {
                _cloud = null;
                Speaking = null;
                Forget();
            }
        }
        catch (OperationCanceledException)
        {
            // Остановили: Stop уже всё убрал.
        }
        catch (Exception ex) when (ex is HttpRequestException or VeniceApiException or TimeoutException or IOException or InvalidOperationException)
        {
            if (ReferenceEquals(_cloud, cancel))
            {
                _cloud = null;
                Speaking = null;
                Forget();
                Failed?.Invoke(ex.Message);
            }
        }
        finally
        {
            // Озвученная наперёд часть, которую уже не сыграют: её сбой никто не ждёт.
            _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            cancel.Dispose();
        }
    }

    /// <summary>Играет файл и ждёт его конца; остановка — отменой.</summary>
    private Task PlayFileAsync(string path, CancellationToken token)
    {
        var player = _player ??= new MediaPlayer();
        var done = new TaskCompletionSource();
        void Ended(object? sender, EventArgs e) => done.TrySetResult();
        void Broken(object? sender, ExceptionEventArgs e) => done.TrySetException(new IOException(e.ErrorException.Message));
        player.MediaEnded += Ended;
        player.MediaFailed += Broken;
        var registration = token.Register(() => done.TrySetCanceled(token));
        Forget();
        _playing = path;
        player.Open(new Uri(path));
        player.Play();
        return done.Task.ContinueWith(
            task =>
            {
                player.MediaEnded -= Ended;
                player.MediaFailed -= Broken;
                registration.Dispose();
                return task;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
    }

    /// <summary>Звук во временный файл; голые отсчёты PCM получают заголовок WAV.</summary>
    private static string Write(SpeechAudio audio)
    {
        var type = audio.MediaType.ToLowerInvariant();
        var folder = Path.Combine(Path.GetTempPath(), "amarin-speech");
        Directory.CreateDirectory(folder);
        string extension;
        var data = audio.Data;
        if (type.Contains("pcm", StringComparison.Ordinal))
        {
            var rate = type.Split(';').Select(part => part.Trim()).FirstOrDefault(part => part.StartsWith("rate=", StringComparison.Ordinal)) is { } found &&
                       int.TryParse(found[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 24_000;
            data = WavFile.Build(data, rate);
            extension = ".wav";
        }
        else
        {
            extension = type.Contains("wav", StringComparison.Ordinal) ? ".wav"
                : type.Contains("ogg", StringComparison.Ordinal) || type.Contains("opus", StringComparison.Ordinal) ? ".ogg"
                : type.Contains("aac", StringComparison.Ordinal) ? ".aac"
                : type.Contains("flac", StringComparison.Ordinal) ? ".flac"
                : ".mp3";
        }

        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Удаляет сыгранный файл; плеер сперва отпускает его.</summary>
    private void Forget()
    {
        if (_playing is not { } path)
        {
            return;
        }

        _playing = null;
        _player?.Close();
        MediaAudio.TryDelete(path);
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

            _current = null;
            Speaking = null;
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
