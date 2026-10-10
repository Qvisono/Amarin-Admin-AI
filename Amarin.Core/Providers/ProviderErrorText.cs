using System.Net;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>
/// Отказ провайдера словами, которые можно показать человеку.
/// </summary>
/// <remarks>
/// Тело отказа бывает не JSON, а целой HTML-страницей: так отвечает, например, защита сайта
/// OpenRouter адресу из страны, которой сервис не обслуживает («Access Denied»). Прежде такая
/// страница уходила в текст исключения целиком, и в плашке выбора модели вместо объяснения
/// стояла простыня разметки.
/// </remarks>
internal static partial class ProviderErrorText
{
    /// <summary>Сколько знаков чужого текста оставлять в сообщении: дальше — уже не объяснение.</summary>
    internal const int DetailLimit = 160;

    private const string NoDetails = "No details";

    /// <summary>
    /// Текст из тела отказа, не являющегося JSON: заголовок HTML-страницы или её текст без
    /// разметки, сжатый в одну строку и обрезанный.
    /// </summary>
    /// <remarks>
    /// Сообщение из JSON сюда не попадает и не обрезается: по его словам узнаются отказы,
    /// которые программа обходит сама (грамматика вызовов, усилие рядом с инструментами).
    /// </remarks>
    public static string Detail(string? body)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0)
        {
            return NoDetails;
        }

        if (LooksLikeHtml(text))
        {
            text = FromHtml(text);
        }

        text = Spaces().Replace(text, " ").Trim();
        if (text.Length == 0)
        {
            return NoDetails;
        }

        return text.Length <= DetailLimit ? text : text[..DetailLimit].TrimEnd() + "…";
    }

    /// <summary>
    /// Почему не загрузился список моделей — строкой для плашки выбора.
    /// </summary>
    /// <remarks>
    /// Отказ доступа назван прямо и с вероятной причиной: на 403 от геоблока человек иначе
    /// шёл бы менять исправный ключ.
    /// </remarks>
    public static string ForCatalog(Exception error, string provider) => error switch
    {
        VeniceApiException { StatusCode: 401 or 403 } denied =>
            Loc.Format("S.Models.LoadDenied", provider, denied.StatusCode),
        VeniceApiException { StatusCode: 429 } =>
            Loc.Format("S.Models.LoadBusy", provider),
        VeniceApiException { StatusCode: >= 500 } broken =>
            Loc.Format("S.Models.LoadServer", provider, broken.StatusCode),
        HttpRequestException =>
            Loc.Format("S.Models.LoadOffline", provider),
        TimeoutException or OperationCanceledException =>
            Loc.Format("S.Models.LoadTimeout", provider),
        _ => Loc.Format("S.Models.LoadFailed", provider, Detail(error.Message))
    };

    private static bool LooksLikeHtml(string text) =>
        text.StartsWith('<') && (text.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("<title", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("<h1", StringComparison.OrdinalIgnoreCase));

    private static string FromHtml(string html)
    {
        // Заголовок страницы — уже готовое объяснение («Access Denied», «502 Bad Gateway»), а в
        // тексте под ним обычно номер запроса и ссылка на поддержку чужого сайта.
        var title = Title().Match(html);
        var text = title.Success && !string.IsNullOrWhiteSpace(title.Groups[1].Value)
            ? title.Groups[1].Value
            : Tags().Replace(Hidden().Replace(html, " "), " ");

        return WebUtility.HtmlDecode(text);
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
