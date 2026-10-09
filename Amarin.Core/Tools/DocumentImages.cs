using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Картинки для документа: ссылка <c>amarin-image:</c> из чата или файл на диске.</summary>
/// <remarks>
/// Из сети документ картинки не тянет: адрес мог прийти из чужой страницы, и сборка документа
/// стала бы скрытой загрузкой. Сетевую картинку модель сперва приносит в чат (<c>fetch_image</c>)
/// и ставит в документ её ссылку.
/// </remarks>
internal static class DocumentImages
{
    private const long MaxBytes = 25L * 1024 * 1024;

    public static byte[]? Resolve(string source)
    {
        if (ChatImageRegistry.IsHandle(source))
        {
            return ChatImageRegistry.Find(source) is { } image ? Decode(image.Base64) : null;
        }

        var path = Environment.ExpandEnvironmentVariables(source.Trim().Trim('"'));
        if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            path = Uri.UnescapeDataString(path["file:///".Length..]).Replace('/', '\\');
        }

        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || SensitivePaths.IsSensitive(path, out _) ||
                new FileInfo(path).Length > MaxBytes)
            {
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[]? Decode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
