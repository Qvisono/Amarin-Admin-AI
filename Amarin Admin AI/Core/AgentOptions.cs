namespace Amarin.Core;

public sealed class AgentOptions
{
    private readonly string _apiKey = string.Empty;

    /// <summary>
    /// Общий на программу держатель активного ключа. <c>null</c> — ключ берётся из
    /// <see cref="ApiKey"/>, как было до появления страницы «Key &amp; Info»; на этом стоят тесты,
    /// которым хватает одной строки.
    /// </summary>
    public ApiKeyProvider? Keys { get; init; }

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
    /// Куда записать увиденный остаток ключа: плашка в композере складывает из них сумму.
    /// </summary>
    /// <remarks>
    /// Через настройки, по образцу <see cref="SpendSink"/>: клиентов в программе несколько,
    /// а книга остатков одна. Сообщаются остатки всех ключей, не только выбранного, — платят
    /// теперь несколько, и одного числа больше не хватает.
    /// </remarks>
    public Action<ApiCredential, VeniceBalance>? BalanceSink { get; init; }

    /// <summary>
    /// Ключ для запроса. Читается заново на каждом обращении: человек может сменить ключ, пока
    /// программа работает, а копии настроек у агента и служебных генераторов уже розданы.
    /// </summary>
    public string ApiKey
    {
        get => Binding?.Secret ?? Keys?.Current ?? _apiKey;
        init => _apiKey = value;
    }

    /// <summary>
    /// Переопределение адреса Venice из <c>appsettings.json</c> — и только его.
    /// </summary>
    /// <remarks>
    /// Не вычисляется по провайдеру намеренно: значение копируют в свои настройки агент
    /// (<c>AgentHost.CloneOptions</c>), генератор заголовков и сводка, и вычисляемое значение
    /// они заморозили бы на том провайдере, который был активен в миг копирования. Куда ехать
    /// на самом деле, решает <c>VeniceClient.Request</c> — заново на каждом запросе и по тем
    /// учётным данным, с которыми запрос отправляют.
    /// </remarks>
    public string BaseUrl { get; init; } = "https://api.venice.ai/api/v1";

    /// <summary>
    /// Ключ этой копии настроек: она собрана под конкретный слот моделей.
    /// </summary>
    /// <remarks>
    /// Свой клиент есть у агента, у маршрутизатора ярусов, у SynGuard, у заголовков и у сводок,
    /// и каждый из них работает своей моделью — а с версии 1.23.0 модель слота может быть
    /// у другого провайдера, чем выбранный ключ. Поле ставится в миг сборки копии и дальше не
    /// меняется: внутренние вызовы этих путей ничего про ключи не знают и знать не должны.
    /// <c>null</c> — платим выбранным ключом, как раньше.
    /// </remarks>
    public ApiCredential? Binding { get; init; }

    /// <summary>Чей сервер отвечает прямо сейчас.</summary>
    public LlmProvider Provider =>
        Binding?.Provider ?? Keys?.CurrentProvider ?? LlmProvider.Venice;

    /// <summary>
    /// Ключ Venice, каким бы ни был активный: рисование картинок и чтение страниц живут только
    /// у Venice, а платить человек может и OpenRouter. Пустой — такого ключа нет.
    /// </summary>
    public ApiCredential VeniceCredential =>
        Keys?.VeniceCredential ?? new ApiCredential(LlmProvider.Venice, _apiKey);

    /// <summary>Ключ вместе с провайдером — то, что нужно, чтобы собрать запрос.</summary>
    public ApiCredential Credential =>
        Binding ?? Keys?.Credential ?? new ApiCredential(LlmProvider.Venice, _apiKey);

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
