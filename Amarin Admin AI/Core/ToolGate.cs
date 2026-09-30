using System.Text.Json;
using System.Text.Json.Nodes;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>Что шлюз узнал о вызове до того, как кого-либо спрашивать.</summary>
/// <param name="Arguments">
/// Аргументы, с которыми вызов исполнится: служебные поля от модели сняты, а у молчаливой
/// записи нового файла стоит <see cref="SafeZone.CreateNewFlag"/>.
/// </param>
/// <param name="Refusal">Отказ без вопроса — текст для модели. Null — вызов не запрещён.</param>
/// <param name="Question">Что показать человеку. Null — спрашивать не нужно.</param>
internal sealed record GateCheck(
    string ToolName,
    JsonElement Arguments,
    ToolEffect Effect,
    string? Refusal,
    DangerousActionInfo? Question,
    bool NeedsSnapshot);

/// <summary>Итог шлюза для одного вызова.</summary>
internal sealed record GateDecision(
    bool Allowed,
    string? Refusal,
    ApprovalSource Approval,
    JsonElement Arguments,
    ToolEffect Effect,
    bool NeedsSnapshot)
{
    public static GateDecision Refuse(string refusal, ApprovalSource source, JsonElement arguments, ToolEffect effect) =>
        new(false, refusal, source, arguments, effect, NeedsSnapshot: false);
}

/// <summary>
/// Одна точка решения для инструментов чата и агента: выключен ли инструмент, пускает ли режим,
/// нет ли жёсткого запрета, надо ли спросить человека и снимать ли снимок.
/// </summary>
/// <remarks>
/// <para>
/// Прежде инструменты чата не проходили ничего: <c>write_file</c> из чата переписывал любой файл
/// без вопроса, мимо режима подтверждений и мимо SynGuard, — вся защита жила в агенте.
/// </para>
/// <para>
/// Решение разделено на две части. <see cref="Check"/> ничего не ждёт и никого не спрашивает —
/// его зовут для всего раунда сразу, до первого вопроса. <see cref="DecideAsync"/> спрашивает
/// тем способом, который дал вызывающий: агент — через свой интерфейс, чат — через очередь
/// подтверждений. Шлюз не бросает: любой исход — решение по этому вызову, и пара
/// «вызов — ответ» в истории модели не рвётся.
/// </para>
/// </remarks>
internal static class ToolGate
{
    /// <summary>Отказ человека. Тот же текст, что был у агента, — на нём стоят тесты и модели.</summary>
    public const string DeniedReply = "Действие отменено пользователем.";

    private static readonly AsyncLocal<bool> ReadOnlyForced = new();

    /// <summary>
    /// Режим «только чтение» для всего, что выполнится в этом асинхронном потоке, — какой бы
    /// режим ни стоял в настройках. Так идут прогоны по расписанию: человека рядом нет, и
    /// спросить его о записи некому.
    /// </summary>
    public static IDisposable ForceReadOnly()
    {
        var previous = ReadOnlyForced.Value;
        ReadOnlyForced.Value = true;
        return new ReadOnlyRestore(previous);
    }

    /// <summary>Действует ли сейчас «только чтение» — из настроек или из <see cref="ForceReadOnly"/>.</summary>
    public static bool IsReadOnly(AppSettings settings) =>
        settings.ApprovalMode == ApprovalMode.ReadOnly || ReadOnlyForced.Value;

    private sealed class ReadOnlyRestore(bool previous) : IDisposable
    {
        public void Dispose() => ReadOnlyForced.Value = previous;
    }

    public static GateCheck Check(string? toolName, JsonElement arguments, AppSettings? settings)
    {
        settings ??= new AppSettings();
        var tool = (toolName ?? "").Trim();
        var args = WithoutInternalFields(arguments);
        var effect = ToolEffects.Classify(tool, args);

        if (IsDisabled(settings, tool))
        {
            return Refused(Loc.Format("S.Gate.Disabled", tool));
        }

        // Секреты, ядро Windows, загрузки мимо белого списка, последний администратор — запрет
        // до всякого вопроса, в том числе в скрипте, который только читает.
        if (tool.Equals("run_powershell", StringComparison.OrdinalIgnoreCase) &&
            PowerShellAnalysis.Analyze(args).Refusal is { } scriptRefusal)
        {
            return Refused(scriptRefusal);
        }

        if (effect == ToolEffect.Write && IsReadOnly(settings))
        {
            return Refused(ReadOnlyRefusal(tool, args, byScope: settings.ApprovalMode != ApprovalMode.ReadOnly));
        }

        if (tool.Equals("local_users", StringComparison.OrdinalIgnoreCase) &&
            LocalUsersSafety.TryGetHardBlockReason(args, out var localUsersBlock))
        {
            return Refused(localUsersBlock);
        }

        if (effect == ToolEffect.Write && ProgramDataTarget(tool, args) is { } target)
        {
            return Refused(SensitivePaths.ProgramDataRefusal(target));
        }

        var needsSnapshot = DangerousActionGuard.RequiresUndoSnapshot(tool, args);
        if (effect == ToolEffect.Read)
        {
            return new GateCheck(tool, args, effect, null, null, needsSnapshot);
        }

        var silentNewFile = settings.ApprovalMode != ApprovalMode.AskAll && QuietNewFile(tool, args);
        var ask = settings.ApprovalMode == ApprovalMode.AskAll ||
                  (!silentNewFile && RequiresConfirmation(tool, args));

        if (silentNewFile)
        {
            args = WithCreateNew(args);
        }

        return new GateCheck(
            tool,
            args,
            effect,
            null,
            ask ? DangerousActionGuard.DescribeDetailed(tool, args) : null,
            needsSnapshot);

        GateCheck Refused(string reason) => new(tool, args, effect, reason, null, false);
    }

    /// <summary>Спрашивает, если нужно, и выносит решение. Отмена хода — исключение, как и прежде.</summary>
    /// <param name="guardApproved">
    /// Человек уже разрешил этот вызов в вопросе SynGuard: второй, более слабый вопрос про то же
    /// самое не задаётся.
    /// </param>
    public static async Task<GateDecision> DecideAsync(
        GateCheck check,
        Func<DangerousActionInfo, CancellationToken, Task<ConfirmationAnswer>> ask,
        bool guardApproved,
        CancellationToken cancellationToken)
    {
        if (check.Refusal is { } refusal)
        {
            return GateDecision.Refuse(refusal, ApprovalSource.NotRequired, check.Arguments, check.Effect);
        }

        if (guardApproved)
        {
            return new GateDecision(true, null, ApprovalSource.SynGuardHuman, check.Arguments, check.Effect,
                check.NeedsSnapshot);
        }

        if (check.Question is not { } question)
        {
            return new GateDecision(true, null, ApprovalSource.NotRequired, check.Arguments, check.Effect,
                check.NeedsSnapshot);
        }

        var answer = await ask(question, cancellationToken).ConfigureAwait(false);
        return answer.Approved
            ? new GateDecision(true, null, answer.Source, check.Arguments, check.Effect, check.NeedsSnapshot)
            : GateDecision.Refuse(DeniedReply, answer.Source, check.Arguments, check.Effect);
    }

    /// <summary>Описания инструментов без выключенных человеком.</summary>
    public static List<ToolDefinition> WithoutDisabled(List<ToolDefinition> definitions, AppSettings? settings)
    {
        if (settings?.DisabledTools is not { Count: > 0 } disabled)
        {
            return definitions;
        }

        return definitions
            .Where(definition => !disabled.Contains(definition.Function.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public static bool IsDisabled(AppSettings settings, string toolName) =>
        settings.DisabledTools is { Count: > 0 } disabled &&
        disabled.Contains(toolName.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string ReadOnlyRefusal(string tool, JsonElement args, bool byScope)
    {
        var action = DangerousActionGuard.ActionOf(args);
        // Режим этого прогона, а не настройка: совет «смените режим в настройках» тут неверен.
        return Loc.Format(byScope ? "S.Gate.ReadOnlyRun" : "S.Gate.ReadOnly", action.Length == 0 ? tool : $"{tool} ({action})");
    }

    /// <summary>
    /// Нужен ли вопрос в обычном режиме. Сверх прежних правил: копирование и перенос тоже
    /// спрашиваются, если кладут файл вне «Загрузок» и «Рабочего стола» или поверх
    /// существующего, — раньше они молча перезаписывали цель.
    /// </summary>
    private static bool RequiresConfirmation(string tool, JsonElement args)
    {
        if (tool.Equals("filesystem", StringComparison.OrdinalIgnoreCase) &&
            DangerousActionGuard.ActionOf(args) is "copy" or "move")
        {
            return true;
        }

        return DangerousActionGuard.RequiresConfirmation(tool, args);
    }

    /// <summary>
    /// Запись без вопроса: новый файл (или копия, или перенос в новое место) внутри «Загрузок» или
    /// «Рабочего стола». Что файла нет, проверит сама запись, — шлюз лишь ставит ей это условие.
    /// </summary>
    private static bool QuietNewFile(string tool, JsonElement args)
    {
        var action = DangerousActionGuard.ActionOf(args);
        var target = tool.ToLowerInvariant() switch
        {
            "write_file" => StringArg(args, "path"),
            "filesystem" when action == "write" => StringArg(args, "path"),
            "filesystem" when action is "copy" or "move" => StringArg(args, "destination"),
            _ => null
        };

        // Цель уже есть — это перезапись, о ней спрашивают. Без этой проверки повтор вызова
        // после отказа «файл уже существует» снова шёл бы молча — и снова упирался бы в отказ.
        if (target is null || File.Exists(target) || Directory.Exists(target))
        {
            return false;
        }

        // Перенос уносит исходный файл: без вопроса — только если и исходный в зоне.
        if (tool.Equals("filesystem", StringComparison.OrdinalIgnoreCase) && action == "move" &&
            !SafeZone.Contains(StringArg(args, "path")))
        {
            return false;
        }

        return SafeZone.Contains(target);
    }

    /// <summary>Куда пишет вызов, если это папка данных самой программы. Иначе null.</summary>
    private static string? ProgramDataTarget(string tool, JsonElement args)
    {
        var action = DangerousActionGuard.ActionOf(args);
        string?[] targets = tool.ToLowerInvariant() switch
        {
            "write_file" => [StringArg(args, "path")],
            "filesystem" when action is "write" or "mkdir" => [StringArg(args, "path")],
            "filesystem" when action == "copy" => [StringArg(args, "destination")],
            "filesystem" when action == "move" => [StringArg(args, "path"), StringArg(args, "destination")],
            "download_file" => [StringArg(args, "destination")],
            _ => []
        };

        return targets.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && SensitivePaths.IsProgramData(path));
    }

    private static JsonElement WithoutInternalFields(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(SafeZone.CreateNewFlag, out _))
        {
            return arguments;
        }

        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node.Remove(SafeZone.CreateNewFlag);
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement WithCreateNew(JsonElement arguments)
    {
        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node[SafeZone.CreateNewFlag] = true;
        return JsonSerializer.SerializeToElement(node);
    }

    private static string? StringArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
