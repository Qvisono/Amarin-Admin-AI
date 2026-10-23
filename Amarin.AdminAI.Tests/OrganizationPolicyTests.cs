using System.Text.Json;
using System.Xml.Linq;
using Amarin.Core;
using Microsoft.Win32;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Политики организации (1.37.0): чтение из реестра, применение в шлюзе, у мыши, у провайдеров и
/// моделей, и шаблон ADMX, который пишет ровно то, что программа читает.
/// </summary>
[Collection(OrganizationPolicyCollection.Name)]
public sealed class OrganizationPolicyTests : IDisposable
{
    private readonly OrganizationPolicy _previous = OrganizationPolicy.Current;

    public void Dispose() => OrganizationPolicy.Current = _previous;

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Policy_is_read_from_values_and_admx_style_lists()
    {
        var path = @"Software\AmarinAdminAITests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue(OrganizationPolicy.ReadOnlyValue, 1, RegistryValueKind.DWord);
                key.SetValue(OrganizationPolicy.DisableComputerControlValue, 0, RegistryValueKind.DWord);
                key.SetValue(OrganizationPolicy.WindowsEventLogValue, 1, RegistryValueKind.DWord);
                using (var tools = key.CreateSubKey(OrganizationPolicy.DisabledToolsKey))
                {
                    tools.SetValue("1", "run_powershell");
                    tools.SetValue("2", " registry ");
                }

                using (var providers = key.CreateSubKey(OrganizationPolicy.AllowedProvidersKey))
                {
                    providers.SetValue("1", new[] { "anthropic", "custom" }, RegistryValueKind.MultiString);
                }

                using var models = key.CreateSubKey(OrganizationPolicy.AllowedModelsKey);
                models.SetValue("1", "anthropic:claude-*");
            }

            var policy = OrganizationPolicy.Read(Registry.CurrentUser, path);

            Assert.True(policy.ReadOnly);
            Assert.False(policy.DisableComputerControl);
            Assert.True(policy.WindowsEventLog);
            Assert.Equal(["registry", "run_powershell"], policy.DisabledTools.Order());
            Assert.Equal(["anthropic", "custom"], policy.AllowedProviders.Order());
            Assert.Equal(["anthropic:claude-*"], policy.AllowedModels);
            Assert.True(policy.Any);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void A_missing_key_means_no_policy()
    {
        var policy = OrganizationPolicy.Read(Registry.CurrentUser, @"Software\AmarinAdminAITests\" + Guid.NewGuid().ToString("N"));

        Assert.Same(OrganizationPolicy.None, policy);
        Assert.False(policy.Any);
    }

    [Fact]
    public void Providers_and_models_are_matched_by_list_prefix_and_the_custom_word()
    {
        var policy = new OrganizationPolicy
        {
            AllowedProviders = ["anthropic", "openRouter"],
            AllowedModels = ["anthropic:claude-*", "openrouter:openai/gpt-5"]
        };

        Assert.True(policy.AllowsProvider(LlmProvider.Anthropic));
        Assert.True(policy.AllowsProvider(LlmProvider.OpenRouter));
        Assert.False(policy.AllowsProvider(LlmProvider.Venice));
        Assert.False(policy.AllowsProvider(LlmProvider.Custom("a1b2c3")));
        Assert.True(policy.AllowsModel("anthropic:claude-sonnet-5"));
        Assert.True(policy.AllowsModel("OpenRouter:OpenAI/GPT-5"));
        Assert.False(policy.AllowsModel("openrouter:openai/gpt-5-mini"));
        Assert.False(policy.AllowsModel("grok-4-6"));

        var custom = new OrganizationPolicy { AllowedProviders = [OrganizationPolicy.CustomServers] };
        Assert.True(custom.AllowsProvider(LlmProvider.Custom("a1b2c3")));
        Assert.False(custom.AllowsProvider(LlmProvider.Anthropic));

        Assert.True(OrganizationPolicy.None.AllowsModel("grok-4-6"));
        Assert.True(OrganizationPolicy.None.AllowsProvider(LlmProvider.Custom("a1b2c3")));
    }

    [Fact]
    public void Forced_read_only_refuses_every_write_whatever_the_settings_and_the_run_exception()
    {
        OrganizationPolicy.Current = new OrganizationPolicy { ReadOnly = true };
        var settings = new AppSettings { ApprovalMode = ApprovalMode.AlwaysApprove };

        var write = ToolGate.Check("registry", Args(new { action = "write", path = @"HKCU\Software\X" }), settings);
        var read = ToolGate.Check("registry", Args(new { action = "read", path = @"HKCU\Software\X" }), settings);

        Assert.NotNull(write.Refusal);
        Assert.Contains(Loc.Format("S.Gate.ReadOnlyPolicy", "registry (write)"), write.Refusal, StringComparison.Ordinal);
        Assert.Null(read.Refusal);
        Assert.True(ToolGate.IsReadOnly(settings));

        // Исключение прогона расписания (мышь) политика не знает.
        using (ToolGate.ForceReadOnly("computer"))
        {
            Assert.True(ToolGate.IsReadOnlyFor(settings, "computer"));
        }
    }

    [Fact]
    public void A_tool_turned_off_by_the_organization_is_hidden_and_refused_with_its_own_reason()
    {
        OrganizationPolicy.Current = new OrganizationPolicy { DisabledTools = ["registry"] };
        var settings = new AppSettings();

        var check = ToolGate.Check("registry", Args(new { action = "read", path = @"HKCU\Software\X" }), settings);
        Assert.Equal(Loc.Format("S.Gate.DisabledPolicy", "registry"), check.Refusal);
        Assert.True(ToolGate.IsDisabled(settings, "registry"));

        var definitions = new List<ToolDefinition> { Definition("registry"), Definition("read_file") };
        Assert.Equal(["read_file"], ToolGate.WithoutDisabled(definitions, settings).Select(item => item.Function.Name));

        // Выключенный самим человеком — прежний текст.
        OrganizationPolicy.Current = OrganizationPolicy.None;
        var own = new AppSettings { DisabledTools = ["registry"] };
        Assert.Equal(Loc.Format("S.Gate.Disabled", "registry"),
            ToolGate.Check("registry", Args(new { action = "read" }), own).Refusal);
    }

    [Fact]
    public void The_mouse_is_allowed_only_with_the_switch_and_without_the_ban()
    {
        var on = new AppSettings { ComputerControl = true };
        Assert.True(ComputerHub.Allowed(on));
        Assert.False(ComputerHub.Allowed(new AppSettings()));

        OrganizationPolicy.Current = new OrganizationPolicy { DisableComputerControl = true };
        Assert.False(ComputerHub.Allowed(on));
    }

    [Fact]
    public async Task A_blocked_provider_gets_no_request_at_all()
    {
        OrganizationPolicy.Current = new OrganizationPolicy { AllowedProviders = ["anthropic"] };
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6" };
        var client = new VeniceClient(http, options);

        var error = await Assert.ThrowsAsync<VeniceApiException>(() => client.ListTextModelsAsync());

        Assert.Equal(Loc.Format("S.Policy.ProviderBlocked", "Venice"), error.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_blocked_model_gets_no_request_at_all()
    {
        OrganizationPolicy.Current = new OrganizationPolicy { AllowedModels = ["zai-org-glm-5"] };
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6" };
        var client = new VeniceClient(http, options);

        var error = await Assert.ThrowsAsync<VeniceApiException>(() => client.CreateChatCompletionAsync(
            "grok-4-6", [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement("hi") }], null, null, new VeniceParameters()));

        Assert.Contains(VeniceModelCatalog.GetDisplayName("grok-4-6"), error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Model_pickers_show_only_allowed_models()
    {
        IReadOnlyList<VeniceModelInfo> models =
        [
            new() { Id = "grok-4-6" },
            new() { Id = "anthropic:claude-sonnet-5" },
            new() { Id = "openrouter:openai/gpt-5" }
        ];

        Assert.Same(models, VeniceModelCatalog.ForSlot(models, ModelSlot.Chat));

        OrganizationPolicy.Current = new OrganizationPolicy { AllowedModels = ["anthropic:*"] };
        Assert.Equal(["anthropic:claude-sonnet-5"], VeniceModelCatalog.ForSlot(models, ModelSlot.Chat).Select(model => model.Id));

        OrganizationPolicy.Current = new OrganizationPolicy { AllowedProviders = ["venice", "openRouter"] };
        Assert.Equal(["grok-4-6", "openrouter:openai/gpt-5"], VeniceModelCatalog.ForSlot(models, ModelSlot.Chat).Select(model => model.Id));
    }

    [Fact]
    public void Blocked_providers_are_not_offered()
    {
        OrganizationPolicy.Current = new OrganizationPolicy { AllowedProviders = ["openRouter"] };
        var keys = new ApiKeyProvider();

        Assert.Equal([LlmProvider.OpenRouter], keys.Providers.Select(spec => spec.Provider));
    }

    [Fact]
    public void Chat_is_told_the_mouse_is_banned_by_the_organization_not_to_find_the_switch()
    {
        OrganizationPolicy.Current = new OrganizationPolicy { DisableComputerControl = true };
        var settings = new AppSettings { ComputerControl = true };
        var engine = new ChatEngine(
            new VeniceClient(new HttpClient(new CountingHandler()), new AgentOptions { ApiKey = "test" }),
            new AgentOptions { ApiKey = "test" }, () => settings, new Amarin.Tools.ToolRegistry([]));

        var prompt = engine.CurrentSystemPrompt(new ChatSession { Id = "s" });

        Assert.Contains(ChatEngine.ComputerPolicyRules, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(ChatEngine.ComputerRules, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(ChatEngine.ComputerOffRules, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_admx_template_writes_exactly_what_the_app_reads()
    {
        XNamespace ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";
        var folder = Path.Combine(SourceTree.RepositoryRoot, "policies");
        var admx = XDocument.Load(Path.Combine(folder, "AmarinAdminAI.admx"));

        var policies = admx.Descendants(ns + "policy").ToList();
        Assert.All(policies, policy =>
        {
            Assert.Equal(OrganizationPolicy.KeyPath, (string?)policy.Attribute("key"));
            Assert.Equal("Machine", (string?)policy.Attribute("class"));
        });

        var values = policies.Select(policy => (string?)policy.Attribute("valueName")).OfType<string>().Order();
        Assert.Equal(
            new[]
            {
                OrganizationPolicy.ReadOnlyValue, OrganizationPolicy.DisableComputerControlValue,
                OrganizationPolicy.WindowsEventLogValue
            }.Order(),
            values);

        var lists = admx.Descendants(ns + "list").Select(list => (string?)list.Attribute("key")).Order();
        Assert.Equal(
            new[]
            {
                OrganizationPolicy.DisabledToolsKey, OrganizationPolicy.AllowedProvidersKey,
                OrganizationPolicy.AllowedModelsKey
            }.Select(name => OrganizationPolicy.KeyPath + "\\" + name).Order(),
            lists);
        Assert.All(admx.Descendants(ns + "list"), list => Assert.Equal("", (string?)list.Attribute("valuePrefix")));

        // Каждая строка и каждое представление шаблона есть в обоих языках.
        var strings = admx.Descendants().Attributes()
            .Select(attribute => attribute.Value)
            .Where(value => value.StartsWith("$(string.", StringComparison.Ordinal))
            .Select(value => value["$(string.".Length..^1])
            .Distinct()
            .ToList();
        var presentations = admx.Descendants().Attributes()
            .Select(attribute => attribute.Value)
            .Where(value => value.StartsWith("$(presentation.", StringComparison.Ordinal))
            .Select(value => value["$(presentation.".Length..^1])
            .ToList();
        foreach (var language in new[] { "en-US", "ru-RU" })
        {
            var adml = XDocument.Load(Path.Combine(folder, language, "AmarinAdminAI.adml"));
            var ids = adml.Descendants(ns + "string").Select(item => (string?)item.Attribute("id")).ToHashSet();
            Assert.All(strings, id => Assert.Contains(id, ids));
            var shown = adml.Descendants(ns + "presentation").Select(item => (string?)item.Attribute("id")).ToHashSet();
            Assert.All(presentations, id => Assert.Contains(id, shown));
            var boxes = adml.Descendants(ns + "listBox").Select(item => (string?)item.Attribute("refId")).ToHashSet();
            Assert.All(admx.Descendants(ns + "list"), list => Assert.Contains((string?)list.Attribute("id"), boxes));
        }
    }

    private static ToolDefinition Definition(string name) => new()
    {
        Function = new FunctionDefinition { Name = name, Description = "", Parameters = Args(new { }) }
    };

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OrganizationPolicyCollection
{
    public const string Name = "OrganizationPolicy";
}
