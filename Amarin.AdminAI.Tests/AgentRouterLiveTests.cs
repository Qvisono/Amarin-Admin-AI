using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Живой замер маршрутизатора уровня агента на Decisions с правилом о работе в окнах
/// (<see cref="AgentTierRouter.WindowsRule"/>): работа в окнах уходит модели со зрением, а
/// остальное правило не трогает. Каждая фраза идёт дважды — с правилом и без, чтобы было видно,
/// что меняет именно оно. Ключ только из окружения.
/// </summary>
/// <remarks>
/// Запуск: <c>AMARIN_AGENT_ROUTER_LIVE=&lt;файл отчёта&gt;</c> и <c>OPENROUTER_API_KEY</c>; модель —
/// <c>AMARIN_ROUTER_MODEL</c> или заводская. Блок моделей подставной: у быстрого уровня зрения нет,
/// у лёгкого и тяжёлого есть, — только так правило вообще может на что-то повлиять.
/// </remarks>
public sealed class AgentRouterLiveTests
{
    private const string Fast = "deepseek-v4-flash-0731";
    private const string Lite = "claude-haiku-5-5";
    private const string Heavy = "claude-sonnet-5-5";

    // Это проверочные входы, а не часть инструкций модели. «?» — только смотрим.
    private static readonly (string Task, string Expected)[] Cases =
    [
        ("Открой Telegram и напиши Маше, что я опоздаю на 10 минут", "vision"),
        ("В окне «Параметры Windows» включи тёмную тему", "vision"),
        ("Открой в браузере почту и найди последнее письмо от банка", "vision"),
        ("Покажи мне, где в Диспетчере устройств отключается веб-камера", "vision"),
        ("Сколько свободного места на диске C?", "?"),
        ("Очисти временные файлы", "?"),
        ("Перезапусти службу печати", "?")
    ];

    [Fact]
    public async Task Measure_window_work_routing_with_and_without_the_rule()
    {
        var output = Environment.GetEnvironmentVariable("AMARIN_AGENT_ROUTER_LIVE");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "OPENROUTER_API_KEY is required for a live evaluation.");
        var model = Environment.GetEnvironmentVariable("AMARIN_ROUTER_MODEL") ?? AppSettings.FactoryDecisionModelId;
        var models = ModelBriefing.ForAgentRouter(Fast, Lite, Heavy, id => new VeniceModelInfo
        {
            Id = id,
            ModelSpec = new VeniceModelSpec
            {
                Capabilities = new VeniceModelCapabilities { SupportsVision = id != Fast, SupportsFunctionCalling = true }
            }
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var results = new JsonArray();
        var failures = 0;
        foreach (var (task, expected) in Cases)
        {
            var with = await AskAsync(http, model, task, models + "\n" + AgentTierRouter.WindowsRule, Blind);
            var without = await AskAsync(http, model, task, models, null);
            var wrong = expected == "vision" && with.Tier is not ("lite" or "heavy");
            failures += wrong ? 1 : 0;
            results.Add(new JsonObject
            {
                ["task"] = task, ["expected"] = expected,
                ["withRule"] = with.Tier, ["withoutRule"] = without.Tier,
                ["status"] = with.Status, ["response"] = with.Body
            });
            await File.WriteAllTextAsync(output, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(with.Status == 200, $"Provider error {with.Status}; see evaluation report.");
        }

        Assert.True(failures == 0, $"{failures} window tasks went to a model that can't see; see evaluation report.");
    }

    /// <summary>Как у программы: уровень, чья модель не видит экран, получает запрет в критерий.</summary>
    private static readonly HashSet<string> Blind = ["fast"];

    private static async Task<(string? Tier, int Status, string Body)> AskAsync(
        HttpClient http, string model, string task, string models, IReadOnlySet<string>? blind)
    {
        var payload = new JsonObject
        {
            ["model"] = ModelRef.Bare(model),
            ["state"] = AgentTierRouter.BuildUserMessage(task, null),
            ["questions"] = new JsonObject { ["tier"] = RoutingDecisions.AgentQuestion(models, blind) }
        };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("https://openrouter.ai/api/alpha/decisions", content);
        var body = await response.Content.ReadAsStringAsync();
        string? tier = null;
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var answer = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.OpenRouterDecisionResponse)?.Answers?.GetValueOrDefault("tier");
                tier = answer?.Type == "choice" ? answer.Choice : null;
            }
            catch (JsonException)
            {
                // Сохраняем неразобранный ответ, а не выдаём его за выбор.
            }
        }

        return (tier, (int)response.StatusCode, body);
    }
}
