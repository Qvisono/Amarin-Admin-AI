namespace Amarin.Core;

public sealed class AgentOptions
{
    private readonly string _apiKey = string.Empty;

    /// <summary>
    /// Общий на программу держатель активного ключа. <c>null</c> — ключ берётся из
    /// <see cref="ApiKey"/>, как было до появления страницы «Key &amp; Info»; на этом стоят тесты,
    /// которым хватает одной строки.
    /// </summary>
    public VeniceKeyProvider? Keys { get; init; }

    /// <summary>
    /// Куда записать списание: ключ, цена, за что. Собственный журнал трат программы, из
    /// которого строится график на странице «Key &amp; Info».
    /// </summary>
    /// <remarks>
    /// Через настройки, а не полем клиента: клиентов в программе несколько — общий, по одному
    /// на прогон агента, на маршрутизатор, на SynGuard и на служебные генераторы, — и журнал
    /// у всех обязан быть один. <c>null</c> — ничего не пишем; на этом стоят тесты.
    /// </remarks>
    public Action<string, VeniceCost, string>? SpendSink { get; init; }

    /// <summary>
    /// Ключ для запроса. Читается заново на каждом обращении: человек может сменить ключ, пока
    /// программа работает, а копии настроек у агента и служебных генераторов уже розданы.
    /// </summary>
    public string ApiKey
    {
        get => Keys?.Current ?? _apiKey;
        init => _apiKey = value;
    }

    public string BaseUrl { get; init; } = "https://api.venice.ai/api/v1";
    public string Model { get; set; } = "grok-4-6";
    public int MaxToolRounds { get; init; } = 30;

    /// <summary>Agent-only. The shared launch <see cref="AgentOptions"/> must stay off so chat cannot leak here.</summary>
    public bool DisableThinking { get; init; } = true;

    public string? ReasoningEffort { get; init; }

    public ReasoningChoice Reasoning => new(DisableThinking, ReasoningEffort);

    public string WebSearch { get; init; } = "off";

    public bool EnableWebCitations { get; init; } = true;

    /// <summary>Native xAI search for SearchWebAsync on Grok models. Agent chat always disables it.</summary>
    public bool? EnableXSearch { get; init; }

    public DownloadOptions Download { get; init; } = new();
}
