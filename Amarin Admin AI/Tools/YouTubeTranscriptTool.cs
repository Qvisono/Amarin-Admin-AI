using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>
/// Pulls the subtitle track off a YouTube video and returns it as plain text, using yt-dlp.
/// <para>
/// yt-dlp is an external program and is not shipped with the app — YouTube changes its player
/// often enough that a vendored copy would rot. When it is missing the tool says so plainly
/// rather than failing with a decoder error nobody can act on.
/// </para>
/// </summary>
public sealed partial class YouTubeTranscriptTool : ITool
{
    /// <summary>Generous: yt-dlp negotiates with YouTube before it downloads anything.</summary>
    private const int TimeoutSeconds = 120;

    /// <summary>A long video's transcript can be enormous; past this it stops fitting a prompt.</summary>
    private const int MaxCharacters = 120_000;

    public string Name => "youtube_transcript";

    public string Description =>
        "Fetch the subtitle transcript of a YouTube video as plain text. Use it whenever the " +
        "user asks to summarize, analyse or quote a video. Requires yt-dlp to be installed.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "YouTube video URL or bare video id"
            },
            "language": {
              "type": "string",
              "description": "Preferred subtitle language code, e.g. ru or en. Defaults to the video's own."
            }
          },
          "required": ["url"]
        }
        """);

    /// <summary>Outcome of a transcript fetch. <paramref name="Text"/> is set only on success.</summary>
    public sealed record TranscriptResult(bool Success, string Text, string? Error)
    {
        public static TranscriptResult Ok(string text) => new(true, text, null);
        public static TranscriptResult Fail(string error) => new(false, "", error);
    }

    /// <summary>
    /// Fetches a transcript without going through the tool protocol, so the infographic flow can
    /// use it directly — that path deliberately runs without a tool loop.
    /// </summary>
    public static async Task<TranscriptResult> FetchTranscriptAsync(
        string url,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (TryReadVideoId(url ?? "") is not { } videoId)
        {
            return TranscriptResult.Fail(
                "Не похоже на ссылку YouTube. Ожидается youtube.com/watch?v=… , youtu.be/… или id видео.");
        }

        if (FindYtDlp() is not { } executable)
        {
            return TranscriptResult.Fail(MissingYtDlpMessage);
        }

        var safeLanguage = SafeLanguage(language);
        var workDirectory = Path.Combine(Path.GetTempPath(), "amarin-yt-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(workDirectory);
            var run = await RunYtDlpAsync(executable, videoId, safeLanguage, workDirectory, cancellationToken);
            if (!run.Success)
            {
                return TranscriptResult.Fail(run.Output);
            }

            var subtitle = Directory
                .EnumerateFiles(workDirectory, "*.vtt")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (subtitle is null)
            {
                return TranscriptResult.Fail(
                    "У этого видео нет субтитров — ни собственных, ни автоматических, — " +
                    "поэтому расшифровку получить нельзя.");
            }

            var text = ParseVtt(await File.ReadAllTextAsync(subtitle, cancellationToken));
            if (text.Length == 0)
            {
                return TranscriptResult.Fail("Файл субтитров пуст.");
            }

            if (text.Length > MaxCharacters)
            {
                text = text[..MaxCharacters] + "\n\n[расшифровка обрезана по длине]";
            }

            return TranscriptResult.Ok(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TranscriptResult.Fail($"Не удалось прочитать субтитры: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>Stated once so the tool and the infographic flow refuse identically.</summary>
    public const string MissingYtDlpMessage =
        "yt-dlp не найден. Он нужен, чтобы получить субтитры видео.\n" +
        "Установите его одним из способов и повторите:\n" +
        "  winget install yt-dlp.yt-dlp\n" +
        "  либо положите yt-dlp.exe рядом с приложением\n" +
        "Страница проекта: https://github.com/yt-dlp/yt-dlp";

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("url", out var urlProp) ||
            string.IsNullOrWhiteSpace(urlProp.GetString()))
        {
            return ToolResult.Fail("Missing required parameter: url");
        }

        var url = urlProp.GetString()!;
        var language = arguments.TryGetProperty("language", out var langProp)
            ? langProp.GetString()
            : null;

        var result = await FetchTranscriptAsync(url, language, cancellationToken);
        return result.Success
            ? ToolResult.Ok($"Расшифровка видео {TryReadVideoId(url)}:\n\n{result.Text}")
            : ToolResult.Fail(result.Error ?? "Не удалось получить расшифровку.");
    }

    private static async Task<ToolResult> RunYtDlpAsync(
        string executable,
        string videoId,
        string? language,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDirectory
        };

        // ArgumentList quotes each entry itself, so nothing the user typed is ever parsed as a
        // flag — and the URL is rebuilt from a validated id rather than passed through raw.
        psi.ArgumentList.Add("--skip-download");
        psi.ArgumentList.Add("--write-subs");
        psi.ArgumentList.Add("--write-auto-subs");
        psi.ArgumentList.Add("--sub-format");
        psi.ArgumentList.Add("vtt");
        psi.ArgumentList.Add("--sub-langs");
        psi.ArgumentList.Add(language is null ? "ru.*,en.*,-live_chat" : $"{language}.*,-live_chat");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(Path.Combine(workDirectory, "%(id)s.%(ext)s"));
        psi.ArgumentList.Add($"https://www.youtube.com/watch?v={videoId}");

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Не удалось запустить yt-dlp: {ex.Message}");
        }

        // Drain both pipes off-thread; yt-dlp is chatty enough to fill a pipe buffer and wedge.
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return ToolResult.Fail($"yt-dlp не ответил за {TimeoutSeconds} с.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        if (process.ExitCode == 0)
        {
            return ToolResult.Ok("");
        }

        var error = (await stderr).Trim();
        if (error.Length == 0)
        {
            error = (await stdout).Trim();
        }

        return ToolResult.Fail(
            $"yt-dlp завершился с кодом {process.ExitCode}.\n{Shorten(error, 1500)}");
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or not ours to kill.
        }
    }

    /// <summary>
    /// Turns a WebVTT file into readable prose: no timestamps, no cue settings, and none of the
    /// line-by-line repetition that auto-generated captions are full of.
    /// </summary>
    internal static string ParseVtt(string vtt)
    {
        var lines = vtt.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder();
        var previous = "";

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 ||
                line.StartsWith("WEBVTT", StringComparison.Ordinal) ||
                line.StartsWith("NOTE", StringComparison.Ordinal) ||
                line.StartsWith("Kind:", StringComparison.Ordinal) ||
                line.StartsWith("Language:", StringComparison.Ordinal) ||
                line.Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            // Cue numbers on their own line.
            if (line.Length < 5 && line.All(char.IsDigit))
            {
                continue;
            }

            var text = CueTags().Replace(line, "").Trim();
            if (text.Length == 0 || text == previous)
            {
                continue;
            }

            // Rolling captions repeat the tail of the previous cue as the head of the next one.
            if (previous.Length > 0 && previous.EndsWith(text, StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(text).Append(' ');
            previous = text;
        }

        return CollapseSpaces().Replace(builder.ToString(), " ").Trim();
    }

    /// <summary>Accepts a full URL or a bare id, and returns only a well-formed video id.</summary>
    internal static string? TryReadVideoId(string input)
    {
        var value = input.Trim();
        if (VideoId().IsMatch(value))
        {
            return value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var host = uri.Host.TrimStart('w', '.').ToLowerInvariant();
        if (host is not ("youtube.com" or "youtu.be" or "m.youtube.com" or "music.youtube.com"))
        {
            return null;
        }

        if (host == "youtu.be")
        {
            var candidate = uri.AbsolutePath.Trim('/');
            return VideoId().IsMatch(candidate) ? candidate : null;
        }

        // /watch?v=ID
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query)["v"];
        if (query is not null && VideoId().IsMatch(query))
        {
            return query;
        }

        // /shorts/ID, /embed/ID, /live/ID
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            segments[0] is "shorts" or "embed" or "live" or "v" &&
            VideoId().IsMatch(segments[1]))
        {
            return segments[1];
        }

        return null;
    }

    /// <summary>Never let a caller-supplied string become a yt-dlp flag.</summary>
    private static string? SafeLanguage(string? language)
    {
        var value = language?.Trim();
        return string.IsNullOrEmpty(value) || !LanguageCode().IsMatch(value) ? null : value;
    }

    /// <summary>PATH first, then next to the app, then the repo's bin folder.</summary>
    private static string? FindYtDlp()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "yt-dlp.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry — skip it.
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "yt-dlp.exe");
        yield return Path.Combine(baseDirectory, "bin", "yt-dlp.exe");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp files; Windows will get them eventually.
        }
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex LanguageCode();

    /// <summary>WebVTT inline markup: &lt;c.colorE5E5E5&gt;, &lt;00:00:01.000&gt;, &lt;i&gt; and friends.</summary>
    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex CueTags();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseSpaces();
}
