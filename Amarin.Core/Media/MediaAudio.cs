namespace Amarin.Core;

/// <summary>
/// Звук из записи или видео — в WAV 16 кГц, 16 бит, моно, как пишет микрофон программы: этот WAV
/// принимают и распознаватель Windows, и облачная расшифровка.
/// </summary>
/// <remarks>
/// Перекодирует сам Windows (<c>Windows.Media.Transcoding</c>) — теми кодеками, что в нём есть: MP3,
/// AAC и M4A, WMA, FLAC, WAV, звук из MP4, MOV, WMV и AVI. Своего декодера и внешних программ нет.
/// У Windows N без пакета мультимедиа кодеков нет — тогда понятный отказ.
/// </remarks>
internal static class MediaAudio
{
    /// <summary>Звук и видео, которые программа расшифровывает при вложении.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(
        [".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".mp4", ".m4v", ".mov", ".wmv", ".avi"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Сколько ждать перекодирования: час записи Windows перекодирует за минуту-другую.</summary>
    private const int TimeoutSeconds = 1800;

    private const string Body = """
        [Windows.Media.Transcoding.MediaTranscoder, Windows.Media.Transcoding, ContentType = WindowsRuntime] | Out-Null
        [Windows.Media.Transcoding.PrepareTranscodeResult, Windows.Media.Transcoding, ContentType = WindowsRuntime] | Out-Null
        [Windows.Media.MediaProperties.MediaEncodingProfile, Windows.Media.MediaProperties, ContentType = WindowsRuntime] | Out-Null
        [Windows.Media.MediaProperties.AudioEncodingQuality, Windows.Media.MediaProperties, ContentType = WindowsRuntime] | Out-Null
        $source = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
        $folder = Await ([Windows.Storage.StorageFolder]::GetFolderFromPathAsync($outFolder)) ([Windows.Storage.StorageFolder])
        $target = Await ($folder.CreateFileAsync($outName, [Windows.Storage.CreationCollisionOption]::ReplaceExisting)) ([Windows.Storage.StorageFile])
        $profile = [Windows.Media.MediaProperties.MediaEncodingProfile]::CreateWav([Windows.Media.MediaProperties.AudioEncodingQuality]::Low)
        $profile.Audio.SampleRate = 16000
        $profile.Audio.ChannelCount = 1
        $profile.Audio.BitsPerSample = 16
        $transcoder = [Windows.Media.Transcoding.MediaTranscoder]::new()
        $prepared = Await ($transcoder.PrepareFileTranscodeAsync($source, $target, $profile)) ([Windows.Media.Transcoding.PrepareTranscodeResult])
        if (-not $prepared.CanTranscode) { 'CANNOT ' + $prepared.FailureReason; return }
        AwaitProgress ($prepared.TranscodeAsync()) ([double])
        'DONE'
        """;

    public static bool IsMedia(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>Перекодирует звук файла во временный WAV и отдаёт его путь; удаляет его вызывающий.</summary>
    /// <exception cref="InvalidDataException">Windows не может прочитать звук этого файла.</exception>
    /// <exception cref="TimeoutException">Перекодирование не уложилось в срок.</exception>
    public static async Task<string> ToWavAsync(string path, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "amarin-audio");
        Directory.CreateDirectory(folder);
        var name = Guid.NewGuid().ToString("N") + ".wav";
        var target = Path.Combine(folder, name);
        var script =
            "$path = " + WinRtScript.Literal(Path.GetFullPath(path)) + "\n" +
            "$outFolder = " + WinRtScript.Literal(folder) + "\n" +
            "$outName = " + WinRtScript.Literal(name) + "\n" +
            Body;
        try
        {
            var (success, output) = await WinRtScript.RunAsync(script, TimeoutSeconds, cancellationToken, longOperation: true).ConfigureAwait(false);
            if (output.Contains("WINRT_TIMEOUT", StringComparison.Ordinal))
            {
                throw new TimeoutException("transcoding took too long");
            }

            if (!success || !output.Contains("DONE", StringComparison.Ordinal) || !File.Exists(target))
            {
                throw new InvalidDataException(output.Trim());
            }

            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временный файл в чужом замке удалится Windows вместе с прочим мусором TEMP.
        }
    }
}
