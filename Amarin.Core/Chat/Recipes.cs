using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>Параметр рецепта: имя из шаблона <c>{{имя}}</c> и значение по умолчанию.</summary>
public sealed record RecipeParameter(string Name, string Default = "");

/// <summary>
/// Рецепт — сохранённый вызов инструмента, который человек повторяет без модели: тот же
/// инструмент, те же аргументы, в которых часть значений — параметры.
/// </summary>
public sealed class Recipe
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string Tool { get; set; } = "";

    /// <summary>Аргументы инструмента — JSON-объект, в строках которого стоят <c>{{имя}}</c>.</summary>
    public string Arguments { get; set; } = "{}";

    public List<RecipeParameter> Parameters { get; set; } = [];

    public DateTime Created { get; set; }

    public DateTime? LastRun { get; set; }
}

/// <summary>
/// Шаблон аргументов: какие в нём параметры и как подставить значения.
/// </summary>
/// <remarks>
/// Подстановка идёт по разобранному JSON, внутрь строковых значений, а не заменой текста:
/// кавычка или обратный слэш в значении (путь Windows) иначе сломали бы JSON, а то и дописали
/// бы в объект чужое поле. Что получилось в итоге — всё равно проверяет шлюз, как у вызова
/// от модели: скрипт с подставленным путём он разбирает уже целиком.
/// </remarks>
internal static partial class RecipeTemplate
{
    /// <summary>Имена параметров в порядке первого появления.</summary>
    public static IReadOnlyList<string> Placeholders(string? arguments) =>
        string.IsNullOrEmpty(arguments)
            ? []
            : Placeholder().Matches(arguments).Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Годится ли шаблон: JSON-объект.</summary>
    public static bool IsValid(string? arguments) => Parse(arguments) is not null;

    /// <summary>
    /// Аргументы с подставленными значениями. Нет значения — берётся значение по умолчанию.
    /// Null — шаблон не JSON-объект.
    /// </summary>
    public static JsonElement? Fill(string? arguments, IReadOnlyDictionary<string, string> values)
    {
        if (Parse(arguments) is not { } root)
        {
            return null;
        }

        var filled = Walk(root, values);
        return JsonSerializer.SerializeToElement(filled);
    }

    /// <summary>Параметры шаблона со значениями по умолчанию, сохранёнными прежде.</summary>
    public static List<RecipeParameter> Reconcile(string? arguments, IEnumerable<RecipeParameter>? known)
    {
        var defaults = (known ?? []).GroupBy(parameter => parameter.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Default, StringComparer.Ordinal);
        return Placeholders(arguments)
            .Select(name => new RecipeParameter(name, defaults.GetValueOrDefault(name, "")))
            .ToList();
    }

    private static JsonObject? Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(arguments) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Walk(JsonNode? node, IReadOnlyDictionary<string, string> values) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(pair =>
            new KeyValuePair<string, JsonNode?>(pair.Key, Walk(pair.Value, values)))),
        JsonArray array => new JsonArray(array.Select(item => Walk(item, values)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) =>
            JsonValue.Create(Placeholder().Replace(text, match =>
                values.TryGetValue(match.Groups[1].Value, out var given) ? given : match.Value)),
        _ => node?.DeepClone()
    };

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]{0,39})\s*\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>Какие инструменты можно сохранить рецептом.</summary>
internal static class RecipeRules
{
    /// <summary>
    /// Инструменты, которым без модели делать нечего или которые тратят деньги ключа: рецепт
    /// повторяет действие на этом ПК, а не разговор.
    /// </summary>
    public static readonly string[] Excluded =
    [
        "web_search", "scrape_url", "generate_image", "init_agent", ReadInstructionTool.ToolName, AgentPlans.SubmitTool
    ];

    public static bool IsRunnable(string? tool) =>
        !string.IsNullOrWhiteSpace(tool) && !Excluded.Contains(tool.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Рецепт из сделанного вызова: служебные поля модели сняты, значения — как были.</summary>
    public static Recipe FromCall(string tool, string? argumentsJson, string name)
    {
        var arguments = "{}";
        try
        {
            var parsed = ToolArguments.Parse(argumentsJson ?? "{}");
            if (parsed.ValueKind == JsonValueKind.Object && JsonNode.Parse(parsed.GetRawText()) is JsonObject node)
            {
                foreach (var field in ModelOnlyFields)
                {
                    node.Remove(field);
                }

                arguments = node.ToJsonString(Indented);
            }
        }
        catch (JsonException)
        {
            // Негодные аргументы — пустой объект: человек поправит их в редакторе.
        }

        return new Recipe { Name = name, Tool = tool.Trim(), Arguments = arguments, Created = DateTime.Now };
    }

    /// <summary>
    /// Пояснение, которое <see cref="ToolRegistry"/> добавляет в схему каждого инструмента: оно про
    /// тот разговор, и в окне подтверждения рецепта стояло бы чужим доводом.
    /// </summary>
    internal static readonly string[] ModelOnlyFields = ["explanation"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}

/// <summary>
/// Рецепты профиля: по файлу <c>recipes/&lt;id&gt;.json</c> на рецепт.
/// </summary>
/// <remarks>
/// Файл на рецепт, а не общий список: правка одного не переписывает остальные, а битый файл
/// теряет только себя. Смену профиля библиотека переживает через <see cref="UseRoot"/> — ссылку
/// на неё держит страница настроек.
/// </remarks>
internal sealed class RecipeLibrary
{
    internal const string FolderName = "recipes";

    private readonly Lock _gate = new();
    private string _folder;

    public RecipeLibrary(string root) => _folder = Path.Combine(root, FolderName);

    public string Folder
    {
        get
        {
            lock (_gate)
            {
                return _folder;
            }
        }
    }

    public void UseRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        lock (_gate)
        {
            _folder = Path.Combine(root, FolderName);
        }
    }

    /// <summary>Все рецепты по имени. Битые файлы пропускаются. Никогда не бросает.</summary>
    public IReadOnlyList<Recipe> All()
    {
        var folder = Folder;
        var list = new List<Recipe>();
        try
        {
            if (!Directory.Exists(folder))
            {
                return list;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (Read(file) is { } recipe)
                {
                    list.Add(recipe);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Папка пропала или закрыта — показываем, что успели прочесть.
        }

        return list.OrderBy(recipe => recipe.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Recipe? Find(string? id) =>
        IsUsableId(id) ? Read(PathFor(id)) : null;

    /// <summary>Записывает рецепт; без годного идентификатора заводит новый. Null — диск отказал.</summary>
    public Recipe? Save(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (!IsUsableId(recipe.Id))
        {
            recipe.Id = Guid.NewGuid().ToString("N")[..12];
        }

        if (recipe.Created == default)
        {
            recipe.Created = DateTime.Now;
        }

        recipe.Name = recipe.Name.Trim();
        recipe.Parameters = RecipeTemplate.Reconcile(recipe.Arguments, recipe.Parameters);
        try
        {
            lock (_gate)
            {
                AppDataFile.WriteAtomic(PathFor(recipe.Id), JsonSerializer.Serialize(recipe, AppJson.Options));
            }

            return recipe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Delete(string? id)
    {
        if (!IsUsableId(id))
        {
            return false;
        }

        try
        {
            File.Delete(PathFor(id));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Идентификатор становится именем файла, поэтому только буквы, цифры, «-» и «_»: иначе
    /// «..\\settings» из подложенного файла указал бы мимо папки.
    /// </summary>
    internal static bool IsUsableId([NotNullWhen(true)] string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private string PathFor(string id) => Path.Combine(Folder, id + ".json");

    private static Recipe? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), AppJson.Options);
            if (recipe is null || string.IsNullOrWhiteSpace(recipe.Tool))
            {
                return null;
            }

            // Имя файла — источник истины: подложенный файл с чужим id внутри не перепишет соседа.
            recipe.Id = Path.GetFileNameWithoutExtension(path);
            recipe.Parameters = RecipeTemplate.Reconcile(recipe.Arguments, recipe.Parameters);
            return recipe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>Чем кончился запуск рецепта.</summary>
internal sealed record RecipeRunOutcome(ToolResult Result, bool Ran, ApprovalSource Approval);

/// <summary>
/// Запуск рецепта без модели — через тот же шлюз, что у чата и агента: режим доступа,
/// жёсткие запреты, вопрос человеку, снимок для отката и строка аудита.
/// </summary>
/// <remarks>
/// SynGuard здесь не спрашивается: он сверяет вызовы модели с тем, о чём её просили, а рецепт
/// запускает сам человек, нажатием, и сам же его составил. Всё остальное — как у модели.
/// </remarks>
internal sealed class RecipeRunner(
    Func<ToolRegistry> tools,
    ConfirmationQueue confirmations,
    Func<AppSettings> settings,
    Func<AuditLog?> audit)
{
    /// <summary>Как снимается снимок перед записью. Подменяется в тестах.</summary>
    internal Func<SessionUndoTracker> Undo { get; init; } = () => new SessionUndoTracker();

    public async Task<RecipeRunOutcome> RunAsync(
        Recipe recipe,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (!RecipeRules.IsRunnable(recipe.Tool))
        {
            return new RecipeRunOutcome(ToolResult.Fail(Loc.Format("S.Recipe.NotRunnable", recipe.Tool)), false,
                ApprovalSource.NotRequired);
        }

        var merged = recipe.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Default, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            merged[name] = value;
        }

        if (RecipeTemplate.Fill(recipe.Arguments, merged) is not { } arguments)
        {
            return new RecipeRunOutcome(ToolResult.Fail(Loc.Get("S.Recipe.BadArguments")), false, ApprovalSource.NotRequired);
        }

        var label = Loc.Format("S.Recipe.Origin", recipe.Name);
        var callId = "recipe_" + Guid.NewGuid().ToString("N")[..8];
        var check = ToolGate.Check(recipe.Tool, arguments, settings());
        var decision = await ToolGate.DecideAsync(
                check,
                (info, token) => confirmations.ConfirmDetailedAsync(label, info, null, token),
                guardApproved: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (!decision.Allowed)
        {
            var refused = ToolResult.Fail(decision.Refusal ?? ToolGate.DeniedReply);
            Audit(AuditOutcome.Refused, refused);
            return new RecipeRunOutcome(refused, false, decision.Approval);
        }

        SessionUndoTracker? undo = null;
        if (decision.NeedsSnapshot)
        {
            undo = Undo();
            undo.BeginRequest(label);
            await Task.Run(() => undo.EnsureSnapshotBeforeMutation(check.ToolName, decision.Arguments), cancellationToken)
                .ConfigureAwait(false);
        }

        ToolResult result;
        try
        {
            result = await tools().ExecuteAsync(check.ToolName, decision.Arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Audit(AuditOutcome.Cancelled, null);
            throw;
        }

        if (undo is not null && result.Success)
        {
            undo.RecordMutation(check.ToolName, decision.Arguments);
            undo.CompleteRequest();
        }

        Audit(result.Success ? AuditOutcome.Ok : AuditOutcome.Failed, result);
        return new RecipeRunOutcome(result, true, decision.Approval);

        void Audit(AuditOutcome outcome, ToolResult? output) =>
            audit()?.Record(
                new AuditOrigin(null, null, label),
                callId,
                check.ToolName,
                decision?.Arguments.GetRawText() ?? arguments.GetRawText(),
                decision?.Effect ?? check.Effect,
                outcome,
                decision?.Approval ?? ApprovalSource.NotRequired,
                AuditGuard.Off,
                output?.Output);
    }
}
