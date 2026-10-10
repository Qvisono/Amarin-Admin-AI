namespace Amarin.Core;

/// <summary>Каким протоколом сервер принимает запрос к модели.</summary>
public enum WireProtocol
{
    /// <summary>OpenAI-совместимый <c>/chat/completions</c>.</summary>
    OpenAiChat,

    /// <summary>Родной протокол Anthropic, <c>/v1/messages</c>.</summary>
    AnthropicMessages
}

/// <summary>Форма тела OpenAI-совместимого запроса: у Venice и OpenRouter свои надстройки.</summary>
public enum PayloadDialect
{
    /// <summary>Надстройки <c>venice_parameters</c>, усилие размышления верхним полем.</summary>
    Venice,

    /// <summary>Вложенный <c>reasoning</c>, просьба посчитать цену, плагин поиска.</summary>
    OpenRouter,

    /// <summary>Только то, что знает любой OpenAI-совместимый сервер.</summary>
    Generic
}

/// <summary>
/// Как OpenAI-совместимому серверу сказать «думай» и «не думай» — у провайдеров это разные поля.
/// </summary>
public enum ReasoningDialect
{
    /// <summary>Одно поле <c>reasoning_effort</c>; «не думать» — его значение <c>none</c>, если модель его знает.</summary>
    Effort,

    /// <summary>Выключатель <c>thinking: {type}</c> и рядом уровень <c>reasoning_effort</c>.</summary>
    ThinkingSwitch,

    /// <summary>Флаг <c>enable_thinking</c> без уровней.</summary>
    EnableThinking
}

/// <summary>Откуда берётся остаток на ключе.</summary>
public enum BalanceSource
{
    /// <summary>Провайдер остатка не сообщает: опрашивать незачем.</summary>
    None,

    /// <summary>Заголовки ответов Venice и <c>api_keys/rate_limits</c>.</summary>
    VeniceRateLimits,

    /// <summary><c>GET /key</c> и <c>GET /credits</c> OpenRouter.</summary>
    OpenRouterCredits
}

/// <summary>
/// Всё, чем провайдеры отличаются друг от друга вне кода запроса: адрес, приставка к
/// идентификаторам моделей, протокол, форма тела, что у провайдера есть и чего нет.
/// </summary>
/// <remarks>
/// До 1.34.0 почти каждое различие в коде было двоичным: «Venice — иначе OpenRouter». Новый
/// провайдер молча получал бы поведение OpenRouter, а то и ключ отправлялся бы на сервер Venice.
/// Теперь решение принимается по свойству спецификации, а у незнакомого провайдера адреса нет
/// вовсе — запрос к нему не уходит (<see cref="For"/>).
/// </remarks>
public sealed record ProviderSpec
{
    public required LlmProvider Provider { get; init; }

    /// <summary>Как провайдер называется у людей.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Адрес API без завершающей косой черты. Пустой — провайдер программе незнаком, и запрос
    /// к нему не уходит.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Приставка к идентификатору модели. У Venice пустая: его модели лежат в настройках и
    /// переписках людей без приставки с самого начала, и трогать их значило бы переписывать чужие
    /// файлы ради красоты.
    /// </summary>
    public string Prefix { get; init; } = "";

    /// <summary>
    /// Переменная окружения, из которой берётся ключ. Только у Venice и OpenRouter: ключи других
    /// провайдеров в окружении (<c>OPENAI_API_KEY</c>, <c>ANTHROPIC_API_KEY</c>) ставят для
    /// других программ, и подхватить их молча значило бы платить за этот чат чужими деньгами.
    /// </summary>
    public string EnvironmentVariable { get; init; } = "";

    /// <summary>Страница, где человек берёт ключ.</summary>
    public string KeysUrl { get; init; } = "";

    public WireProtocol Protocol { get; init; } = WireProtocol.OpenAiChat;

    public PayloadDialect Dialect { get; init; } = PayloadDialect.Generic;

    /// <summary>Как сказать о размышлении, если тело запроса <see cref="PayloadDialect.Generic"/>.</summary>
    public ReasoningDialect Reasoning { get; init; } = ReasoningDialect.Effort;

    /// <summary>
    /// Уходит ли в запрос температура. У Venice и OpenRouter — да, как всегда. Прямым провайдерам
    /// её не шлём: рассуждающие модели OpenAI и Claude 5.5 отвечают на чужое значение отказом.
    /// </summary>
    public bool SendsTemperature { get; init; }

    public BalanceSource Balance { get; init; } = BalanceSource.None;

    /// <summary>Есть ли эндпоинт рисования картинок.</summary>
    public bool HasImages { get; init; }

    /// <summary>Есть ли эндпоинт чтения страницы.</summary>
    public bool HasScrape { get; init; }

    /// <summary>
    /// Отдаёт ли провайдер журнал списаний. Нет — график трат строится из собственного журнала
    /// программы (<see cref="SpendLedger"/>).
    /// </summary>
    public bool HasUsageHistory { get; init; }

    /// <summary>Умеет ли искать в интернете надстройкой на обычном запросе.</summary>
    public bool HasWebSearch { get; init; }

    /// <summary>Есть ли пакетная очередь (<c>…:batch</c>).</summary>
    public bool HasBatch { get; init; }

    /// <summary>
    /// Отвечает ли <c>/chat/completions</c> отказом на усилие размышления рядом с инструментами
    /// у моделей GPT-5.4+ и GPT-6. Так ведёт себя сам OpenAI и всё, что передаёт ему запрос как
    /// есть; OpenRouter такой запрос переводит и принимает.
    /// </summary>
    public bool BlocksEffortWithTools { get; init; }

    /// <summary>
    /// Знают ли провайдера версии до 1.34.0. Только их ключи пишутся в <c>keys.json</c>: прежняя
    /// версия, встретив там незнакомое имя, сочла бы файл повреждённым и потеряла бы все ключи.
    /// </summary>
    public bool IsLegacy => Provider == LlmProvider.Venice || Provider == LlmProvider.OpenRouter;

    private static readonly ProviderSpec VeniceSpec = new()
    {
        Provider = LlmProvider.Venice,
        Name = "Venice",
        BaseUrl = "https://api.venice.ai/api/v1",
        EnvironmentVariable = "VENICE_API_KEY",
        KeysUrl = "https://venice.ai/settings/api",
        Dialect = PayloadDialect.Venice,
        SendsTemperature = true,
        Balance = BalanceSource.VeniceRateLimits,
        HasImages = true,
        HasScrape = true,
        HasUsageHistory = true,
        HasWebSearch = true,
        BlocksEffortWithTools = true
    };

    private static readonly ProviderSpec OpenRouterSpec = new()
    {
        Provider = LlmProvider.OpenRouter,
        Name = "OpenRouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Prefix = "openrouter:",
        EnvironmentVariable = "OPENROUTER_API_KEY",
        KeysUrl = "https://openrouter.ai/settings/keys",
        Dialect = PayloadDialect.OpenRouter,
        SendsTemperature = true,
        Balance = BalanceSource.OpenRouterCredits,

        // Ни картинок, ни чтения страниц: у OpenRouter только /chat/completions и всё, что
        // вокруг него. Инструменты, которым эти эндпоинты нужны, ходят в Venice своим ключом.
        // GET /activity открыт только ключам, выданным через provisioning API; обычный ключ
        // получает отказ — как журнал Venice без админ-ключа.
        HasWebSearch = true,
        HasBatch = true
    };

    /// <summary>Встроенные провайдеры в порядке, в каком их видит человек.</summary>
    public static IReadOnlyList<ProviderSpec> All { get; } = [VeniceSpec, OpenRouterSpec];

    /// <summary>
    /// Описание провайдера. Незнакомому — пустое, без адреса: запрос к нему не уйдёт.
    /// </summary>
    /// <remarks>
    /// Прежде любой провайдер, кроме OpenRouter, получал описание Venice — и с ним адрес Venice.
    /// Ключ незнакомого провайдера ушёл бы на чужой сервер.
    /// </remarks>
    public static ProviderSpec For(LlmProvider provider)
    {
        foreach (var spec in All)
        {
            if (spec.Provider == provider)
            {
                return spec;
            }
        }

        return new ProviderSpec { Provider = provider, Name = provider.ToString(), BaseUrl = "" };
    }
}
