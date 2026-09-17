using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Маршрутизатор и модель чата не знали, какие модели стоят в настройках, и потому не могли
/// оценить избыточность. Блок с моделями уезжает в каждый запрос, поэтому проверяется и то,
/// что он говорит, и то, что он остаётся коротким и не падает без каталога.
/// </summary>
public sealed class ModelBriefingTests
{
    private static VeniceModelInfo Model(
        string id,
        string name,
        int context,
        bool reasoning = true,
        bool vision = false,
        bool code = false,
        string? description = null) => new()
    {
        Id = id,
        ContextLength = context,
        ModelSpec = new VeniceModelSpec
        {
            Name = name,
            Description = description,
            AvailableContextTokens = context,
            Capabilities = new VeniceModelCapabilities
            {
                SupportsFunctionCalling = true,
                SupportsReasoning = reasoning,
                SupportsVision = vision,
                OptimizedForCode = code
            }
        }
    };

    private static Func<string, VeniceModelInfo?> Catalogue(params VeniceModelInfo[] models) =>
        id => models.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Briefing_names_the_model_and_what_it_can_do()
    {
        var resolve = Catalogue(
            Model("openai-gpt-56-luna", "GPT-5.6 Luna", 131_072, code: true),
            Model("grok-4-6", "Grok 4.6", 2_000_000, vision: true, code: true));

        var block = ModelBriefing.ForRouter("openai-gpt-56-luna", "grok-4-6", resolve);

        Assert.Contains("lite -> GPT-5.6 Luna", block, StringComparison.Ordinal);
        Assert.Contains("heavy -> Grok 4.6", block, StringComparison.Ordinal);
        Assert.Contains("2M context", block, StringComparison.Ordinal);
        Assert.Contains("131k context", block, StringComparison.Ordinal);
        Assert.Contains("reasoning, vision, code", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Briefing_uses_capabilities_and_not_the_venice_description()
    {
        var blurb = new string('x', 500);
        var resolve = Catalogue(Model("grok-4-6", "Grok 4.6", 2_000_000, description: blurb));

        var block = ModelBriefing.ForRouter("grok-4-6", "grok-4-6", resolve);

        Assert.DoesNotContain(blurb, block, StringComparison.Ordinal);
    }

    [Fact]
    public void Briefing_degrades_to_bare_names_before_the_catalogue_loads()
    {
        // Каталог подтягивается лениво: до первого ответа /models разрешителя нет вовсе, а на
        // модель без reasoning его и потом не будет — FilterAgentic такие в кеш не кладёт.
        foreach (var resolve in new Func<string, VeniceModelInfo?>?[] { null, _ => null })
        {
            var block = ModelBriefing.ForRouter("openai-gpt-56-luna", "grok-4-6", resolve);

            Assert.Contains("lite -> GPT-5.6 Luna", block, StringComparison.Ordinal);
            Assert.Contains("heavy -> Grok 4.6", block, StringComparison.Ordinal);
            Assert.DoesNotContain("context", block, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_agent_router_sees_all_three_tiers_it_chooses_between()
    {
        var resolve = Catalogue(
            Model("deepseek-v4-flash-0731-fast", "DeepSeek V4 Flash", 131_072),
            Model("openai-gpt-56-luna", "GPT-5.6 Luna", 131_072, code: true),
            Model("grok-4-6", "Grok 4.6", 2_000_000, vision: true, code: true));

        var block = ModelBriefing.ForAgentRouter(
            "deepseek-v4-flash-0731-fast", "openai-gpt-56-luna", "grok-4-6", resolve);

        Assert.Contains("fast -> DeepSeek V4 Flash", block, StringComparison.Ordinal);
        Assert.Contains("lite -> GPT-5.6 Luna", block, StringComparison.Ordinal);
        Assert.Contains("heavy -> Grok 4.6", block, StringComparison.Ordinal);
        Assert.Contains("2M context", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_router_is_told_when_two_tiers_lead_to_one_model()
    {
        var same = ModelBriefing.ForAgentRouter(
            "deepseek-v4-flash-0731-fast", "grok-4-6", "grok-4-6", resolve: null);
        Assert.Contains("changes nothing", same, StringComparison.Ordinal);

        var apart = ModelBriefing.ForAgentRouter(
            "deepseek-v4-flash-0731-fast", "openai-gpt-56-luna", "grok-4-6", resolve: null);
        Assert.DoesNotContain("changes nothing", apart, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chat_is_told_its_own_model_and_nobody_elses()
    {
        // Уровень агента выбирает маршрутизатор, и три чужие модели в промпте чата только
        // возвращали модель к решению, которое больше не её.
        var block = ModelBriefing.ForChat("grok-4-6", resolve: null);

        Assert.Contains("you -> Grok 4.6", block, StringComparison.Ordinal);
        Assert.DoesNotContain("agent", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("heavy", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_chat_block_is_empty_when_the_model_is_not_chosen_yet()
    {
        // Так блок собирается для кольца контекста: оно считает промпт вне хода, и модели
        // «Авто» ещё не выбрала. Придумывать её ради оценки хуже, чем недосчитать строку.
        foreach (var self in new[] { null, "", "auto" })
        {
            Assert.Equal("", ModelBriefing.ForChat(self, resolve: null));
        }
    }

    [Fact]
    public void Briefing_stays_short_enough_to_ship_on_every_request()
    {
        var resolve = Catalogue(
            Model("deepseek-v4-flash-0731-fast", "DeepSeek V4 Flash", 131_072),
            Model("openai-gpt-56-luna", "GPT-5.6 Luna", 131_072, code: true),
            Model("grok-4-6", "Grok 4.6", 2_000_000, vision: true, code: true));

        var chat = ModelBriefing.ForChat("grok-4-6", resolve);
        var router = ModelBriefing.ForRouter("openai-gpt-56-luna", "grok-4-6", resolve);
        var agents = ModelBriefing.ForAgentRouter(
            "deepseek-v4-flash-0731-fast", "openai-gpt-56-luna", "grok-4-6", resolve);

        Assert.True(chat.Length < 200, $"chat briefing grew to {chat.Length}");
        Assert.True(router.Length < 200, $"router briefing grew to {router.Length}");
        Assert.True(agents.Length < 300, $"agent briefing grew to {agents.Length}");
    }
}
