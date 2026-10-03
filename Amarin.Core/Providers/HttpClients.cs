using System.Net;

namespace Amarin.Core;

internal static class HttpClients
{
    /// <summary>
    /// Chrome на Windows 11. Без User-Agent Cloudflare и большинство CDN отвечают 403 — только
    /// поэтому внешние картинки и не грузились. Здесь, чтобы все загрузчики представлялись одинаково.
    /// </summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Потолок на служебный запрос: решение маршрутизатора, защитника, выбор продолжения.
    /// </summary>
    /// <remarks>
    /// Такие запросы отвечают одним словом и идут к быстрой модели. Минуты хватает с запасом,
    /// а больше держать нельзя: человек ждёт ответа, а не служебного вопроса о нём.
    /// </remarks>
    public static readonly TimeSpan ServiceTimeout = TimeSpan.FromMinutes(1);

    /// <param name="browserIdentity">
    /// Слать заголовки как у браузера — для всего, что ходит на обычные сайты; API провайдера
    /// маскировка не нужна.
    /// </param>
    public static HttpClient Create(TimeSpan timeout, bool browserIdentity = false)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        if (browserIdentity)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept",
                "image/avif,image/webp,image/apng,image/svg+xml,image/*,text/html;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru,en;q=0.9");
        }

        return client;
    }
}
