using System.Globalization;
using System.Text;

namespace Amarin.Core;

/// <summary>Распознанный текст страницы (у картинки страница одна).</summary>
internal sealed record OcrPage(int Number, string Text);

/// <summary>
/// Распознавание текста на этом ПК встроенным OCR Windows — без сети и без денег: картинка или
/// страницы PDF-скана.
/// </summary>
/// <remarks>
/// <para>
/// Нужен там, где модель картинку не видит (модель без зрения) или видит, но текст важен дословно, и
/// для скана PDF: у него нет текстового слоя, и чтение (<see cref="PdfTextReader"/>) возвращает
/// пустые страницы. Страницу PDF рисует сам Windows (<c>Windows.Data.Pdf</c>), так что сторонней
/// библиотеки для отрисовки не нужно.
/// </para>
/// <para>
/// Язык распознавания ставится в Windows отдельно («Распознавание текста» в языковом пакете). Его
/// нет — понятный отказ с тем, где его доставить, и списком тех, что есть.
/// </para>
/// </remarks>
internal static class LocalOcr
{
    /// <summary>Сколько страниц PDF распознавать за один раз: около секунды на страницу.</summary>
    public const int MaxPages = 30;

    public const int DefaultPages = 10;

    private const string Body = """
        [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
        [Windows.Graphics.Imaging.BitmapTransform, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
        [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
        [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
        [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
        [Windows.Globalization.Language, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType = WindowsRuntime] | Out-Null
        $engine = $null
        if ($language) {
          $wanted = [Windows.Globalization.Language]::new($language)
          if ([Windows.Media.Ocr.OcrEngine]::IsLanguageSupported($wanted)) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($wanted) }
        } else {
          $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        }
        if (-not $engine) {
          'NO_OCR ' + ((@([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages) | ForEach-Object { $_.LanguageTag }) -join ', ')
          return
        }
        'LANGUAGE ' + $engine.RecognizerLanguage.LanguageTag
        function Recognize($stream) {
          $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
          $transform = [Windows.Graphics.Imaging.BitmapTransform]::new()
          $max = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension
          $width = $decoder.OrientedPixelWidth; $height = $decoder.OrientedPixelHeight
          if ($width -gt $max -or $height -gt $max) {
            $scale = [Math]::Min($max / $width, $max / $height)
            $transform.ScaledWidth = [uint32]([Math]::Floor($decoder.PixelWidth * $scale))
            $transform.ScaledHeight = [uint32]([Math]::Floor($decoder.PixelHeight * $scale))
          }
          $bitmap = Await ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied, $transform, [Windows.Graphics.Imaging.ExifOrientationMode]::RespectExifOrientation, [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
          $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
          $bitmap.Dispose()
          @($result.Lines | ForEach-Object { $_.Text }) -join "`n"
        }
        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
        if ($pages.Count -eq 0) {
          $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
          '=== PAGE 1'
          Recognize $stream
          $stream.Dispose()
        } else {
          $pdf = Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
          foreach ($number in $pages) {
            if ($number -gt $pdf.PageCount) { continue }
            $page = $pdf.GetPage([uint32]($number - 1))
            $options = [Windows.Data.Pdf.PdfPageRenderOptions]::new()
            $options.DestinationWidth = [uint32]([Math]::Min(2600, [Math]::Max(1600, $page.Size.Width * 2.5)))
            $stream = [Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
            AwaitAction ($page.RenderToStreamAsync($stream, $options))
            '=== PAGE ' + $number
            Recognize $stream
            $stream.Dispose()
            $page.Dispose()
          }
        }
        """;

    /// <summary>Распознаёт картинку (<paramref name="pages"/> пуст) или перечисленные страницы PDF.</summary>
    /// <param name="language">Тег языка (<c>ru</c>, <c>en-US</c>); null — языки профиля Windows.</param>
    /// <exception cref="DocumentException">OCR недоступен, файл не прочесть или распознавание не уложилось в срок.</exception>
    public static async Task<(string Language, IReadOnlyList<OcrPage> Pages)> RecognizeAsync(
        string path, IReadOnlyList<int> pages, string? language, CancellationToken cancellationToken)
    {
        var script = new StringBuilder()
            .Append("$path = ").Append(WinRtScript.Literal(path)).Append('\n')
            .Append("$language = ").Append(language is { Length: > 0 } ? WinRtScript.Literal(language.Trim()) : "$null").Append('\n')
            .Append("$pages = @(").Append(string.Join(",", pages.Select(page => page.ToString(CultureInfo.InvariantCulture)))).Append(")\n")
            .Append(Body)
            .ToString();
        var timeout = 60 + 15 * Math.Max(1, pages.Count);
        var (success, output) = await WinRtScript.RunAsync(script, timeout, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            throw new DocumentException(output.Contains("WINRT_TIMEOUT", StringComparison.Ordinal)
                ? "Text recognition did not finish in time. Recognize fewer pages at once."
                : "Windows could not recognize text in this file: " + Shorten(output) + ". It may be damaged, password-protected or not a picture.");
        }

        return Parse(output);
    }

    /// <summary>Разбор вывода скрипта: строка языка, затем страницы с заголовками <c>=== PAGE n</c>.</summary>
    internal static (string Language, IReadOnlyList<OcrPage> Pages) Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.FirstOrDefault(line => line.StartsWith("NO_OCR", StringComparison.Ordinal)) is { } missing)
        {
            var available = missing[6..].Trim();
            throw new DocumentException(
                "Windows has no text recognition for this language. The user can add it in Settings > Time & language > Language & region: " +
                "the language's options, Optical character recognition. " +
                (available.Length > 0 ? "Installed recognition languages: " + available + "." : "No recognition language is installed."));
        }

        var language = "";
        var pages = new List<OcrPage>();
        int? number = null;
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("LANGUAGE ", StringComparison.Ordinal))
            {
                language = line[9..].Trim();
            }
            else if (line.StartsWith("=== PAGE ", StringComparison.Ordinal) && int.TryParse(line[9..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var next))
            {
                Flush();
                number = next;
            }
            else if (number is not null)
            {
                text.Append(line).Append('\n');
            }
        }

        Flush();
        return (language, pages);

        void Flush()
        {
            if (number is { } page)
            {
                pages.Add(new OcrPage(page, text.ToString().Trim()));
            }

            text.Clear();
        }
    }

    private static string Shorten(string text)
    {
        var line = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return line.Length <= 300 ? line : line[..300] + "...";
    }
}
