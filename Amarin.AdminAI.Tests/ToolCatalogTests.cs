using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Страница «Безопасность» перечисляет инструменты списком. Этот тест не даёт списку отстать от
/// того, что агент получает на деле: инструмент, которого нет в каталоге, нельзя было бы выключить.
/// </summary>
public sealed class ToolCatalogTests
{
    [Fact]
    public void Every_agent_tool_can_be_switched_off_on_the_security_page()
    {
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6" };
        using var http = new HttpClient { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        using var download = new HttpClient();
        var registry = AgentTools.Create(new VeniceClient(http, options), download, new DownloadOptions());

        var listed = ToolCatalog.All.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(registry.All, tool => Assert.Contains(tool.Name, listed));
    }

    [Fact]
    public void Every_listed_tool_has_a_label_in_both_languages_and_is_listed_once()
    {
        var names = ToolCatalog.All.ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, name => Assert.True(
            StringsRu.Values.ContainsKey(ToolCatalog.LabelKey(name)),
            $"нет подписи {ToolCatalog.LabelKey(name)}"));
    }
}
