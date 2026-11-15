using System.Text.Json;
using Amarin.Composition;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>Экспорт настоящих промптов и схем для отдельного парного прогона без выполнения инструментов.</summary>
/// <remarks>
/// Только при AMARIN_PROMPT_EXPORT=&lt;путь JSON&gt; и AMARIN_PROMPT_CANDIDATE=&lt;путь TXT&gt;.
/// Кандидат не включается в приложение. Ключи, настройки и данные пользователя не читаются;
/// сетевые запросы запрещены даже при случайном изменении пути сборки промпта.
/// </remarks>
public sealed class ChatPromptEvaluationExportTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [Fact]
    public async Task Export_actual_prompt_pairs_and_tool_definitions_when_requested()
    {
        var output = Environment.GetEnvironmentVariable("AMARIN_PROMPT_EXPORT");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var candidatePath = Environment.GetEnvironmentVariable("AMARIN_PROMPT_CANDIDATE");
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new InvalidOperationException("Set AMARIN_PROMPT_CANDIDATE to an experimental prompt file.");
        }
        var baselineTech = ChatEngine.DefaultTechPrompt;
        var candidateTech = (await File.ReadAllTextAsync(candidatePath)).Trim();
        Assert.False(string.IsNullOrWhiteSpace(candidateTech));

        var root = Path.Combine(Path.GetTempPath(), "amarin-prompt-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient(new NoNetworkHandler());
            var options = new AgentOptions { ApiKey = "test", Model = "test-model", MaxToolRounds = 1 };
            var client = new VeniceClient(http, options);
            List<ITool> tools =
            [
                .. AppComposition.FileTools(new FileToolState()),
                new AnalyzeTableTool(),
                new RecognizeTextTool(),
                new WebSearchTool((_, _) => Task.FromResult("")),
                new GenerateImageTool((_, _, _, _, _) => Task.FromResult("")),
                new FetchImageTool(),
                new YouTubeTranscriptTool(),
                new InitAgentTool(new AgentSlotLimiter(), null!),
                new MessageAgentTool(new AgentSlotLimiter(), null!),
                new AgentRefusedTool(new AgentSlotLimiter(), null!),
                new ReadInstructionTool(new InstructionLibrary(root)),
                new DeferredTaskTool(new DeferredBook(root))
            ];
            var registry = new ToolRegistry(tools);
            Assert.Equal(
                ToolCatalog.Chat.Concat(ToolCatalog.SwitchOf.Keys).Order(StringComparer.Ordinal),
                tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

            var variants = new List<PromptPair>();
            foreach (var (id, mouse, browser) in new[] { ("off", false, false), ("browser", false, true), ("on", true, true) })
            {
                foreach (var memory in new[] { AgentMemory.Answer, AgentMemory.Chat, AgentMemory.Session })
                {
                    var settings = AppSettings.CreateDefault();
                    settings.ComputerControl = mouse;
                    settings.AgentBrowser = browser;
                    settings.AgentMemory = memory;
                    var engine = new ChatEngine(client, options, () => settings, registry)
                    {
                        Time = new FixedTime()
                    };
                    settings.TechAiPrompt = "";
                    var baseline = engine.CurrentSystemPrompt(new ChatSession());
                    settings.TechAiPrompt = candidateTech;
                    var candidate = engine.CurrentSystemPrompt(new ChatSession());

                    // Сравнение должно менять только техпромпт: обязательные приписки и дата совпадают.
                    Assert.Equal(
                        baseline.Replace(baselineTech, "<TECH>", StringComparison.Ordinal),
                        candidate.Replace(candidateTech, "<TECH>", StringComparison.Ordinal));
                    Assert.Contains(mouse ? ChatEngine.ComputerRules : ChatEngine.ComputerOffRules, candidate, StringComparison.Ordinal);
                    Assert.Contains(browser ? BrowserPrompt.ChatRules : BrowserPrompt.ChatOffRules(mouse), candidate, StringComparison.Ordinal);
                    variants.Add(new PromptPair(
                        id + "-" + memory.ToString().ToLowerInvariant(), baseline, candidate));
                }
            }

            var export = new
            {
                schemaVersion = 1,
                date = "2026-11-08",
                baselineTech,
                candidateTech,
                variants,
                tools = registry.GetDefinitions().Append(Refusals.Definition).ToArray()
            };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(export, JsonOptions));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record PromptPair(string Id, string Baseline, string Candidate);

    private sealed class FixedTime : TimeProvider
    {
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
            "PromptEvaluationUTC+07", TimeSpan.FromHours(7), "UTC+07", "UTC+07");

        public override DateTimeOffset GetUtcNow() => new(2026, 11, 8, 5, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Prompt export must not access the network.");
    }
}
