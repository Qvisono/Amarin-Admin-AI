using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Рецепты (C7): шаблон с параметрами, файлы профиля и запуск через шлюз. Главное обещание —
/// рецепт не обходит защиту: режим «только чтение» и вопрос работают как у модели.
/// </summary>
public sealed class RecipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-recipes-" + Guid.NewGuid().ToString("N"));

    public RecipeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Dictionary<string, string> Values(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

    // ───────────────────────── шаблон ─────────────────────────

    [Fact]
    public void Placeholders_are_listed_once_in_order_of_appearance()
    {
        var names = RecipeTemplate.Placeholders("""{"path":"{{folder}}\\{{ name }}.txt","content":"{{folder}}"}""");

        Assert.Equal(["folder", "name"], names);
    }

    [Fact]
    public void A_value_with_quotes_and_backslashes_stays_inside_its_string()
    {
        // Замена текстом дописала бы в объект чужое поле: "a\", \"recurse\": true, \"x\": \"".
        var filled = RecipeTemplate.Fill(
            """{"path":"C:\\Temp\\{{name}}"}""",
            Values(("name", "a\", \"recurse\": true, \"x\": \"b\\c")))!.Value;

        Assert.Equal("C:\\Temp\\a\", \"recurse\": true, \"x\": \"b\\c", filled.GetProperty("path").GetString());
        Assert.False(filled.TryGetProperty("recurse", out _));
    }

    [Fact]
    public void Placeholders_are_filled_inside_arrays_and_nested_objects_and_numbers_stay_numbers()
    {
        var filled = RecipeTemplate.Fill(
            """{"items":["{{a}}", {"x":"{{a}}-{{b}}"}], "count": 3, "flag": true}""",
            Values(("a", "1"), ("b", "2")))!.Value;

        Assert.Equal("1", filled.GetProperty("items")[0].GetString());
        Assert.Equal("1-2", filled.GetProperty("items")[1].GetProperty("x").GetString());
        Assert.Equal(3, filled.GetProperty("count").GetInt32());
        Assert.True(filled.GetProperty("flag").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("{broken")]
    [InlineData("\"text\"")]
    public void Only_a_json_object_is_a_template(string arguments)
    {
        Assert.False(RecipeTemplate.IsValid(arguments));
        Assert.Null(RecipeTemplate.Fill(arguments, Values()));
    }

    [Fact]
    public void Defaults_follow_the_placeholders_and_keep_what_was_typed()
    {
        var parameters = RecipeTemplate.Reconcile(
            """{"a":"{{keep}} {{added}}"}""",
            [new RecipeParameter("keep", "old"), new RecipeParameter("gone", "x")]);

        Assert.Equal([new RecipeParameter("keep", "old"), new RecipeParameter("added", "")], parameters);
    }

    // ───────────────────────── правила ─────────────────────────

    [Theory]
    [InlineData("web_search", false)]
    [InlineData("init_agent", false)]
    [InlineData("read_instruction", false)]
    [InlineData("windows_service", true)]
    [InlineData("run_powershell", true)]
    [InlineData("", false)]
    public void Tools_that_need_a_model_are_not_recipes(string tool, bool runnable) =>
        Assert.Equal(runnable, RecipeRules.IsRunnable(tool));

    [Fact]
    public void A_recipe_from_the_journal_drops_the_models_explanation()
    {
        var recipe = RecipeRules.FromCall("windows_service",
            """{"action":"restart","service_name":"Spooler","explanation":"печать зависла"}""", "Перезапуск");

        using var doc = JsonDocument.Parse(recipe.Arguments);
        Assert.False(doc.RootElement.TryGetProperty("explanation", out _));
        Assert.Equal("Spooler", doc.RootElement.GetProperty("service_name").GetString());
        Assert.Equal("windows_service", recipe.Tool);
    }

    [Fact]
    public void A_call_with_unreadable_arguments_becomes_an_empty_recipe_to_fix() =>
        Assert.Equal("{}", RecipeRules.FromCall("registry", "not json at all", "x").Arguments);

    // ───────────────────────── библиотека ─────────────────────────

    [Fact]
    public void Recipes_are_saved_one_file_each_and_read_back_by_name()
    {
        var library = new RecipeLibrary(_root);

        var second = library.Save(new Recipe { Name = "Б", Tool = "registry", Arguments = """{"path":"{{key}}"}""" })!;
        library.Save(new Recipe { Name = "А", Tool = "windows_service", Arguments = "{}" });

        Assert.True(File.Exists(Path.Combine(_root, RecipeLibrary.FolderName, second.Id + ".json")));
        Assert.Equal(["А", "Б"], library.All().Select(recipe => recipe.Name));
        Assert.Equal("key", Assert.Single(library.Find(second.Id)!.Parameters).Name);
    }

    [Fact]
    public void A_broken_or_foreign_file_does_not_hide_the_others()
    {
        var library = new RecipeLibrary(_root);
        var good = library.Save(new Recipe { Name = "ok", Tool = "registry", Arguments = "{}" })!;
        var folder = Path.Combine(_root, RecipeLibrary.FolderName);
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(folder, "copy.json"), $$"""{"id":"{{good.Id}}","name":"copy","tool":"registry","arguments":"{}"}""");

        var all = library.All();

        Assert.Equal(2, all.Count);
        // Идентификатор берётся из имени файла: подложенный файл не выдаёт себя за соседа.
        Assert.Contains(all, recipe => recipe.Id == "copy");
    }

    [Theory]
    [InlineData("..\\settings")]
    [InlineData("../x")]
    [InlineData("a b")]
    [InlineData("")]
    public void An_id_is_never_a_path(string id)
    {
        var library = new RecipeLibrary(_root);

        Assert.Null(library.Find(id));
        Assert.False(library.Delete(id));
        Assert.False(RecipeLibrary.IsUsableId(id));
    }

    [Fact]
    public void Switching_profiles_moves_the_library()
    {
        var library = new RecipeLibrary(_root);
        library.Save(new Recipe { Name = "first", Tool = "registry", Arguments = "{}" });
        var other = Path.Combine(_root, "profiles", "p2");

        library.UseRoot(other);

        Assert.Empty(library.All());
    }

    [Fact]
    public void Recipes_travel_with_settings_and_go_with_the_profile()
    {
        Assert.Equal(DataUsage.SettingsKey, DataUsage.ClassifyAppFile("recipes/abc.json"));
        Assert.Equal(DataCategory.Settings, DataBundle.CategoryOf("recipes/abc.json"));
        Assert.Contains(RecipeLibrary.FolderName, ProfileDataWiper.DefaultProfileFolders);
    }

    // ───────────────────────── запуск ─────────────────────────

    private static RecipeRunner Runner(ApprovalMode mode, List<JsonElement> ran, AuditLog? audit = null)
    {
        var settings = new AppSettings { ApprovalMode = mode };
        return new RecipeRunner(
            () => new ToolRegistry([new CapturingTool("write_file", ran)]),
            new ConfirmationQueue(() => settings),
            () => settings,
            () => audit);
    }

    private static Recipe WriteRecipe() => new()
    {
        Name = "Заметка",
        Tool = "write_file",
        Arguments = """{"path":"C:\\Temp\\{{name}}.txt","content":"x"}""",
        Parameters = [new RecipeParameter("name", "note")]
    };

    [Fact]
    public async Task A_run_fills_the_defaults_and_what_was_typed()
    {
        var ran = new List<JsonElement>();

        var outcome = await Runner(ApprovalMode.AlwaysApprove, ran).RunAsync(WriteRecipe(), Values(), CancellationToken.None);
        await Runner(ApprovalMode.AlwaysApprove, ran).RunAsync(WriteRecipe(), Values(("name", "other")), CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(ApprovalSource.Auto, outcome.Approval);
        Assert.Equal(["C:\\Temp\\note.txt", "C:\\Temp\\other.txt"], ran.Select(args => args.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task Read_only_mode_refuses_a_recipe_that_writes()
    {
        var ran = new List<JsonElement>();

        var outcome = await Runner(ApprovalMode.ReadOnly, ran).RunAsync(WriteRecipe(), Values(), CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.False(outcome.Result.Success);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task A_recipe_of_a_model_tool_does_not_run()
    {
        var ran = new List<JsonElement>();

        var outcome = await Runner(ApprovalMode.AlwaysApprove, ran)
            .RunAsync(new Recipe { Name = "x", Tool = "web_search", Arguments = "{}" }, Values(), CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task A_run_is_written_to_the_audit_log_as_the_recipes()
    {
        var audit = new AuditLog(_root);

        await Runner(ApprovalMode.AlwaysApprove, [], audit).RunAsync(WriteRecipe(), Values(), CancellationToken.None);

        var entry = Assert.Single(audit.ReadAll());
        Assert.Equal("write_file", entry.Tool);
        Assert.Equal(Loc.Format("S.Recipe.Origin", "Заметка"), entry.Agent);
    }

    // ───────────────────────── страница без окна ─────────────────────────

    [Fact]
    public void A_draft_needs_a_name_a_known_runnable_tool_and_an_object()
    {
        Assert.NotNull(SettingsAutomationPage.Validate("", "registry", "{}"));
        Assert.NotNull(SettingsAutomationPage.Validate("x", "no_such_tool", "{}"));
        Assert.NotNull(SettingsAutomationPage.Validate("x", "web_search", "{}"));
        Assert.NotNull(SettingsAutomationPage.Validate("x", "registry", "[]"));
        Assert.Null(SettingsAutomationPage.Validate("x", "REGISTRY", """{"action":"read"}"""));
    }

    [Fact]
    public void Search_looks_at_name_description_and_tool()
    {
        Recipe[] recipes =
        [
            new() { Id = "a", Name = "Очистка", Description = "временные файлы", Tool = "disk_space" },
            new() { Id = "b", Name = "Печать", Description = "", Tool = "windows_service" }
        ];

        Assert.Equal(["a"], SettingsAutomationPage.Filter(recipes, "врем").Select(row => row.Id));
        Assert.Equal(["b"], SettingsAutomationPage.Filter(recipes, "SERVICE").Select(row => row.Id));
        Assert.Equal(2, SettingsAutomationPage.Filter(recipes, "  ").Count);
    }

    [Fact]
    public void The_agent_gets_the_filled_call_and_the_card_shows_the_action()
    {
        var recipe = new Recipe { Name = "Спулер", Tool = "windows_service", Arguments = """{"action":"restart","service_name":"{{svc}}"}""" };

        var prompt = SettingsAutomationPage.AgentPrompt(recipe, RecipeTemplate.Fill(recipe.Arguments, Values(("svc", "Spooler"))));

        Assert.Contains("\"service_name\": \"Spooler\"", prompt, StringComparison.Ordinal);
        Assert.Equal("windows_service · restart", RecipeRow.TechnicalLine(recipe));
        Assert.EndsWith("…", SettingsAutomationPage.ClipOutput(new string('x', SettingsAutomationPage.OutputLimit + 10)), StringComparison.Ordinal);
    }

    private sealed class CapturingTool(string name, List<JsonElement> ran) : ITool
    {
        public string Name => name;

        public string Description => "stub";

        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            lock (ran)
            {
                ran.Add(arguments.Clone());
            }

            return Task.FromResult(ToolResult.Ok("готово"));
        }
    }
}

/// <summary>Страница «Автоматизация» на живом WPF.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class RecipePageUiTests
{
    private readonly WpfFixture _wpf;

    public RecipePageUiTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public async Task Saved_recipes_appear_as_cards_and_the_editor_lists_their_parameters()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-recipe-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await _wpf.Ui.Invoke(async () =>
            {
                var services = UiServices.Build(root, "k", new HttpClientHandler());
                var saved = services.Recipes.Save(new Recipe
                {
                    Name = "Спулер",
                    Tool = "windows_service",
                    Arguments = """{"action":"restart","service_name":"{{svc}}"}"""
                })!;

                var page = new SettingsAutomationPage { Width = 520, Height = 500 };
                page.Attach(services);
                page.Load();
                for (var i = 0; i < 50 && ((ItemsControl)page.FindName("RecipeItems")).Items.Count == 0; i++)
                {
                    await Task.Delay(20);
                }

                var cards = ((ItemsControl)page.FindName("RecipeItems")).Items.Count;
                page.Edit(saved.Id);
                var defaults = ((ItemsControl)page.FindName("DefaultsList")).Items.Count;
                var editor = ((FrameworkElement)page.FindName("EditorPane")).Visibility;
                return (cards, defaults, editor);
            });

            Assert.Equal(1, result.cards);
            Assert.Equal(1, result.defaults);
            Assert.Equal(Visibility.Visible, result.editor);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
