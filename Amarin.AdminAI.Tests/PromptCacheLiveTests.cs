using System.Globalization;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Живая проверка кэша: два одинаковых запроса к настоящим серверам, в файл пишется таблица токенов.
/// Не утверждение, а таблица: срабатывает ли кэш, решает провайдер, и только его числа это показывают.
/// </summary>
/// <remarks>
/// Работает только при <c>AMARIN_CACHE_LIVE=&lt;файл&gt;</c>; обычный прогон сразу выходит. Модели —
/// <c>AMARIN_CACHE_MODELS</c>, идентификаторы программы через запятую. Ключ провайдера — в
/// <c>AMARIN_CACHE_KEY_&lt;ID&gt;</c> (<c>AMARIN_CACHE_KEY_OPENROUTER</c>, <c>AMARIN_CACHE_KEY_ANTHROPIC</c>);
/// у Venice — <c>AMARIN_CACHE_KEY_VENICE</c>. Папку данных программы тест не читает. Стоит центы:
/// на модель два запроса с одним словом ответа.
/// </remarks>
public sealed class PromptCacheLiveTests
{
    /// <summary>Промпт длиннее любого порога кэша: у части моделей он около четырёх тысяч токенов.</summary>
    private static readonly string LongPrompt = string.Join(
        "\n",
        Enumerable.Range(1, 900).Select(line => string.Create(CultureInfo.InvariantCulture, $"Rule {line}: keep the answer to one word.")));

    [Fact]
    public async Task The_second_request_reads_what_the_first_one_wrote()
    {
        var report = Environment.GetEnvironmentVariable("AMARIN_CACHE_LIVE");
        if (string.IsNullOrWhiteSpace(report))
        {
            return;
        }

        var models = (Environment.GetEnvironmentVariable("AMARIN_CACHE_MODELS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var table = new StringBuilder("model | request | prompt | read | written | error\n");
        foreach (var model in models)
        {
            var provider = ModelRef.Of(model);
            var secret = Environment.GetEnvironmentVariable("AMARIN_CACHE_KEY_" + provider.Id.ToUpperInvariant());
            if (string.IsNullOrWhiteSpace(secret))
            {
                table.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{model} | - | - | - | - | no key"));
                continue;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var client = new VeniceClient(http, new AgentOptions
            {
                Keys = new ApiKeyProvider(secret, provider),
                BaseUrl = "https://api.venice.ai/api/v1",
                Model = model
            });

            var key = Guid.NewGuid().ToString("N");
            for (var request = 1; request <= 2; request++)
            {
                table.AppendLine(await AskAsync(client, model, key, request));

                // Запись становится читаемой не сразу: второй запрос впритык её не застал бы.
                await Task.Delay(TimeSpan.FromSeconds(4));
            }
        }

        await File.WriteAllTextAsync(report, table.ToString());
    }

    private static async Task<string> AskAsync(VeniceClient client, string model, string key, int request)
    {
        try
        {
            var response = await client.CreateChatCompletionAsync(
                model,
                [
                    new ChatMessage { Role = "system", Content = ChatContent.Text(LongPrompt) },
                    new ChatMessage { Role = "user", Content = ChatContent.Text("Say hello.") }
                ],
                null,
                null,
                new VeniceParameters(),
                cacheKey: key);

            var usage = response.Usage;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{model} | {request} | {usage?.PromptTokens} | {usage?.CacheReadTokens} | {usage?.CacheWriteTokens} | ");
        }
        catch (VeniceApiException exception)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{model} | {request} | - | - | - | {exception.Message}");
        }
    }
}
