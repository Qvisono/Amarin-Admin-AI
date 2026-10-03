using System.Reflection;
using Amarin.Core;
using Microsoft.Extensions.Configuration;

namespace Amarin.Composition;

/// <summary>
/// Что программа берёт снаружи до всяких профилей: адрес API, модель по умолчанию, предел раундов,
/// поиск, белый список загрузок из <c>appsettings.json</c> и ключи из окружения.
/// </summary>
/// <param name="VeniceKey">Ключ из <c>VENICE_API_KEY</c> (или user-secrets). Пусто — его нет.</param>
/// <param name="OpenRouterKey">То же для <c>OPENROUTER_API_KEY</c>.</param>
internal sealed record AppConfiguration(
    string VeniceKey,
    string OpenRouterKey,
    string BaseUrl,
    string Model,
    int MaxToolRounds,
    string WebSearch,
    bool EnableWebCitations,
    bool? EnableXSearch,
    DownloadOptions Download)
{
    /// <summary>Конфигурация этого exe: <c>appsettings.json</c> рядом с ним, user-secrets, окружение.</summary>
    public static AppConfiguration Read() =>
        From(new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build());

    /// <summary>Разбор уже собранной конфигурации. Отдельно от чтения — его зовут тесты.</summary>
    public static AppConfiguration From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // По ключу на провайдера: человек мог завести оба, и решать за него, какой из них
        // «настоящий», программа не вправе — выбор он делает на странице «Key & Info».
        return new AppConfiguration(
            ReadEnvironmentKey(configuration, "VENICE_API_KEY"),
            ReadEnvironmentKey(configuration, "OPENROUTER_API_KEY"),
            configuration["Venice:BaseUrl"] ?? "https://api.venice.ai/api/v1",
            configuration["Venice:Model"] ?? "grok-4-6",
            int.TryParse(configuration["Venice:MaxToolRounds"], out var rounds) ? rounds : 30,
            configuration["Venice:WebSearch"] ?? "off",
            !bool.TryParse(configuration["Venice:EnableWebCitations"], out var citations) || citations,
            bool.TryParse(configuration["Venice:EnableXSearch"], out var xSearch) ? xSearch : null,
            ReadDownloadOptions(configuration));
    }

    /// <summary>
    /// Ключ провайдера снаружи программы: переменная окружения, затем user-secrets. На диск
    /// программы он не переписывается — человек сознательно держал его снаружи.
    /// </summary>
    private static string ReadEnvironmentKey(IConfiguration configuration, string name) =>
        Environment.GetEnvironmentVariable(name) ?? configuration[name] ?? string.Empty;

    private static DownloadOptions ReadDownloadOptions(IConfiguration configuration)
    {
        var configuredDomains = configuration.GetSection("Download:AllowedDomains")
            .GetChildren()
            .Select(item => item.Value)
            .OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return new DownloadOptions
        {
            AllowedDomains = configuredDomains.Length > 0
                ? configuredDomains
                : new DownloadOptions().AllowedDomains,
            MaxSizeBytes = long.TryParse(configuration["Download:MaxSizeMb"], out var maxMb)
                ? maxMb * 1024L * 1024L
                : new DownloadOptions().MaxSizeBytes
        };
    }
}
