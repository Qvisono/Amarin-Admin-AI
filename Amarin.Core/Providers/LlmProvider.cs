namespace Amarin.Core;

/// <summary>Чей сервер отвечает на запрос к модели.</summary>
/// <remarks>
/// Имена членов попадают в <c>keys.json</c> и в <c>settings.json</c> (см. <see cref="AppJson"/>,
/// где стоит <c>JsonStringEnumConverter</c>), поэтому дописывать сюда можно, а переименовывать
/// нельзя: у людей на диске лежат записи со старыми именами.
/// </remarks>
public enum LlmProvider
{
    /// <summary>Venice — единственный провайдер до версии 1.23.0.</summary>
    Venice,

    /// <summary>OpenRouter: OpenAI-совместимый API и общий счёт на модели разных производителей.</summary>
    OpenRouter
}

/// <summary>
/// Всё, чем провайдеры отличаются друг от друга вне кода запроса: адрес, приставка к
/// идентификаторам моделей, переменная окружения с ключом и список того, чего у провайдера нет.
/// </summary>
/// <param name="Prefix">
/// Приставка к идентификатору модели. У Venice пустая: его модели лежат в настройках и
/// переписках людей без приставки с самого начала, и трогать их значило бы переписывать чужие
/// файлы ради красоты.
/// </param>
/// <param name="EnvironmentVariable">
/// Переменная окружения, из которой берётся ключ. Своя у каждого провайдера — человек мог
/// завести оба. Её имя служит и названием строки в списке, пока человек не дал ей своё
/// (<see cref="ApiKeyFile.EnvironmentLabels"/>).
/// </param>
/// <param name="HasImages">Есть ли эндпоинт рисования картинок.</param>
/// <param name="HasScrape">Есть ли эндпоинт чтения страницы.</param>
/// <param name="HasUsageHistory">
/// Отдаёт ли провайдер журнал списаний. Нет — график трат строится из собственного журнала
/// программы (<see cref="SpendLedger"/>).
/// </param>
public sealed record ProviderSpec(
    LlmProvider Provider,
    string Name,
    string BaseUrl,
    string Prefix,
    string EnvironmentVariable,
    string KeysUrl,
    bool HasImages,
    bool HasScrape,
    bool HasUsageHistory)
{
    private static readonly ProviderSpec VeniceSpec = new(
        LlmProvider.Venice,
        "Venice",
        "https://api.venice.ai/api/v1",
        Prefix: "",
        "VENICE_API_KEY",
        "https://venice.ai/settings/api",
        HasImages: true,
        HasScrape: true,
        HasUsageHistory: true);

    private static readonly ProviderSpec OpenRouterSpec = new(
        LlmProvider.OpenRouter,
        "OpenRouter",
        "https://openrouter.ai/api/v1",
        Prefix: "openrouter:",
        "OPENROUTER_API_KEY",
        "https://openrouter.ai/settings/keys",
        // Ни картинок, ни чтения страниц: у OpenRouter только /chat/completions и всё, что
        // вокруг него. Инструменты, которым эти эндпоинты нужны, ходят в Venice своим ключом.
        HasImages: false,
        HasScrape: false,
        // GET /activity открыт только ключам, выданным через provisioning API; обычный ключ
        // получает отказ — как журнал Venice без админ-ключа.
        HasUsageHistory: false);

    /// <summary>Все провайдеры в порядке появления. Порядок виден человеку в диалоге ключа.</summary>
    public static IReadOnlyList<ProviderSpec> All { get; } = [VeniceSpec, OpenRouterSpec];

    public static ProviderSpec For(LlmProvider provider) =>
        provider == LlmProvider.OpenRouter ? OpenRouterSpec : VeniceSpec;
}
