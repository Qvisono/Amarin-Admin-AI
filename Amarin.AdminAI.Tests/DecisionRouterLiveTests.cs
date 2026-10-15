using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Живой замер Decisions без подмены ошибки резервным lite. Ключи только из окружения.</summary>
public sealed class DecisionRouterLiveTests
{
    // Это проверочные входы, а не часть инструкций модели. Каждый уходит отдельным запросом.
    private static readonly (string Text, string Expected)[] Cases =
    [
        ("вирус", "lite"),
        ("Что такое компьютерный вирус?", "lite"),
        ("у меня кажись вирусы", "heavy"),
        ("я скачал приложение и у меня диспетчер задач не открывается", "heavy"),
        ("После запуска скачанного файла настройки защиты стали недоступны", "heavy"),
        ("После обновления редактора не открывается один документ", "lite"),
        ("Сколько свободного места на диске?", "lite"),
        ("Только объясни значение слова, не проверяй компьютер: вредоносный", "lite")
    ];

    [Fact]
    public async Task Measure_actual_choices_and_preserve_provider_errors()
    {
        var output = Environment.GetEnvironmentVariable("AMARIN_DECISION_ROUTER_LIVE");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "OPENROUTER_API_KEY is required for a live evaluation.");
        var model = Environment.GetEnvironmentVariable("AMARIN_ROUTER_MODEL") ?? "microsoft/microsoft-decision-1";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var results = new JsonArray();
        var failures = 0;
        foreach (var (input, expected) in Cases)
        {
            var payload = new JsonObject
            {
                ["model"] = ModelRef.Bare(model),
                ["state"] = ChatEngine.BuildRouterUserMessage(input, null),
                ["questions"] = new JsonObject { ["tier"] = RoutingDecisions.ChatQuestion("MODELS\nlite -> light\nheavy -> heavy") }
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("https://openrouter.ai/api/alpha/decisions", content);
            var body = await response.Content.ReadAsStringAsync();
            string? actual = null;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.OpenRouterDecisionResponse);
                    var answer = parsed?.Answers?.GetValueOrDefault("tier");
                    actual = answer?.Type == "choice" ? answer.Choice : null;
                }
                catch (JsonException)
                {
                    // Сохраняем неразобранный ответ, а не выдаём его за выбор lite.
                }
            }

            failures += actual == expected ? 0 : 1;
            results.Add(new JsonObject
            {
                ["request"] = payload, ["expected"] = expected, ["actual"] = actual,
                ["status"] = (int)response.StatusCode, ["response"] = body
            });
            await File.WriteAllTextAsync(output, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(response.IsSuccessStatusCode, $"Provider error {(int)response.StatusCode}; see evaluation report.");
        }

        Assert.True(failures == 0, $"{failures} decision routing cases failed; see evaluation report.");
    }
}
