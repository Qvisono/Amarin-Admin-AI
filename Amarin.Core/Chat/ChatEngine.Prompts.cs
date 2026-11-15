using System.Diagnostics;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

internal sealed partial class ChatEngine
{
    private List<ChatMessage> BuildApiMessages(
        ChatSession session,
        string? currentModelId,
        IReadOnlyList<Instruction> instructions)
    {
        var messages = new List<ChatMessage>();
        var turn = VeniceTurnScope.Current;
        var access = turn?.Access ?? session.Access(_settings());
        if (turn is not null)
        {
            turn.Access = access;
        }

        var system = BuildSystemPrompt(currentModelId, instructions, session.Profile, access);

        // Сжатый чат (D10): старая часть уходит сводкой в системном промпте, а не отдельным
        // сообщением — два сообщения user подряд или выдуманный ответ ассистента принимают
        // не все провайдеры.
        IReadOnlyList<ChatMessage> history;
        int reach;
        lock (session.Gate)
        {
            if (ContextCompaction.IsActive(session) && session.CompactSummary is { } summary)
            {
                system = (system + "\n\n" + ContextCompaction.PromptBlock(summary, session.CompactedFrom > 0)).Trim();
            }

            history = ContextCompaction.Tail(session);
            reach = HygieneReach(session, history);
        }

        if (!string.IsNullOrWhiteSpace(system))
        {
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = ChatContent.Text(system)
            });
        }

        var lead = messages.Count;
        messages.AddRange(ChatMessageCloner.CloneAll(history));

        // Устаревшие версии файлов и прочитанные давно документы — заглушками: только в копии для
        // запроса, хранимая история и отпечаток сжатия не меняются.
        ContextHygiene.Apply(messages, lead + reach);
        return messages;
    }

    /// <summary>
    /// Сколько сообщений истории чистить: до границы или, если её пора сдвинуть, всё.
    /// </summary>
    /// <remarks>
    /// Граница отсчитывается от начала <see cref="ChatSession.ApiMessages"/>, а история может быть
    /// хвостом после сжатия. Сжатие и удаление сообщений и так меняют начало запроса, поэтому граница
    /// вне хвоста просто ставится заново.
    /// </remarks>
    private int HygieneReach(ChatSession session, IReadOnlyList<ChatMessage> history)
    {
        var reach = (session.HygieneUpTo ?? -1) - (session.ApiMessages.Count - history.Count);
        if (session.HygieneUpTo is not null && reach >= 0 && reach <= history.Count)
        {
            var held = ContextCompaction.HumanTurns(history.Skip(reach).ToList()).Count;
            if (!ContextHygiene.MayMoveBoundary(SinceLastAnswer(session), held))
            {
                return reach;
            }
        }

        session.HygieneUpTo = session.ApiMessages.Count;
        return history.Count;
    }

    /// <summary>
    /// Сколько прошло с конца последнего законченного ответа. Кэш отсчитывается от начала последнего
    /// запроса, то есть истекает раньше: граница сдвинется на ход позже, но живой кэш не собьёт.
    /// </summary>
    private TimeSpan? SinceLastAnswer(ChatSession session) =>
        session.Messages.LastOrDefault(message => message.Role == "assistant" && message.Status == AssistantStatus.Complete) is { } answer
            ? Time.GetLocalNow().DateTime - (answer.CreatedAt + answer.Duration)
            : null;

    /// <summary>
    /// Системный промпт следующего запроса. Открыт для кольца контекста: это настоящая часть окна,
    /// а собирать его заново в интерфейсе значило бы раздвоить логику.
    /// </summary>
    /// <param name="session">Чат, чей промпт мерить: у него может быть свой профиль (D11).</param>
    internal string CurrentSystemPrompt(ChatSession? session = null)
    {
        var settings = _settings();
        return BuildSystemPrompt(
            currentModelId: null,
            ActiveInstructions(session),
            session?.Profile,
            session?.Access(settings) ?? ChatSessionAccess.For(null, settings));
    }

    /// <param name="currentModelId">
    /// Модель, на которой идёт ход, — она попадает в блок MODELS. Явным аргументом, а не через
    /// <see cref="VeniceTurnScope"/>: тот ambient и с несколькими одновременными ходами протёк
    /// бы в чужой запрос, а аргумент протечь не может. Null у кольца контекста, которое
    /// собирает промпт вне хода.
    /// </param>
    /// <param name="instructions">Снимок инструкций хода — см. <see cref="ActiveInstructions"/>.</param>
    /// <param name="profile">
    /// Профиль чата (D11): его промпт встаёт на место общего «основного». Технический промпт,
    /// правила формул и блоки ниже остаются — без них чат перестал бы уметь то, что умеет.
    /// </param>
    private string BuildSystemPrompt(
        string? currentModelId,
        IReadOnlyList<Instruction> instructions,
        ChatProfile? profile = null,
        ChatSessionAccess? access = null)
    {
        // Только чат: основной промпт + TechAiPrompt. У агента — TechAgentPrompt / BaseSystemPrompt.
        var settings = _settings();
        var effectiveAccess = access ?? ChatSessionAccess.For(null, settings);
        var main = profile?.Prompt is { } own && !string.IsNullOrWhiteSpace(own) ? own.Trim() : settings.MainPrompt?.Trim() ?? "";
        var tech = settings.TechAiPrompt?.Trim() ?? "";
        if (tech.Length == 0)
        {
            tech = DefaultTechPrompt;
        }

        // Блок дописывается здесь, а не живёт внутри DefaultTechPrompt, по двум причинам:
        // идентификаторы моделей — настройки, а тот текст константа; и половина пользы — для
        // тех, кто однажды сохранил свой технический промпт и носит замороженную копию, куда
        // правка константы не дойдёт никогда.
        var models = ModelBriefing.ForChat(currentModelId, _venice.ResolveModelInfo);

        var parts = new List<string>();
        if (main.Length > 0)
        {
            parts.Add(main);
        }

        parts.Add(tech);
        parts.Add(TaskRules);
        parts.Add(AgentLifetimeRules(settings.AgentMemory));
        parts.Add(FormulaRules);
        parts.Add(FileRules);
        parts.Add(DeferredRules);
        parts.Add(effectiveAccess.CanControlComputer ? ComputerRules
            : OrganizationPolicy.Current.DisableComputerControl ? ComputerPolicyRules
            : ComputerOffRules);

        // Браузер агента (1.37.0) — блоком здесь, а не в описании init_agent: то собирается один раз.
        // Выключен — чат называет переключатель, а не обещает браузер (1.37.0-beta.8). На удалённой
        // машине браузера у агента нет вовсе, и говорить о нём нечего.
        if (ExecutionTarget.Current is null)
        {
            parts.Add(effectiveAccess.CanUseAgentBrowser
                ? BrowserPrompt.ChatRules
                : BrowserPrompt.ChatOffRules(effectiveAccess.CanControlComputer));
        }

        // Команды «/» (D9) — из того же списка, что и подсказка у поля: модель может назвать
        // нужную, а сохранённые техпромпты не отстают, потому что строка дописывается здесь.
        parts.Add(ChatCommands.PromptBlock());

        // Перед блоком моделей, а не после: тот меняется с моделью хода (под «Авто» — хоть каждый
        // ход), а оглавление инструкций — только когда их правят. Неизменная голова промпта
        // провайдеры кэшируют сами.
        var index = InstructionBriefing.ForChat(instructions);
        if (index.Length > 0)
        {
            parts.Add(index);
        }

        if (models.Length > 0)
        {
            parts.Add(models);
        }

        if (RemoteBriefing.For(ExecutionTarget.Current) is { Length: > 0 } remote)
        {
            parts.Add(remote);
        }

        parts.Add(DateLine(Time.GetLocalNow()));
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    /// <summary>
    /// Сегодняшняя дата — последней строкой промпта.
    /// </summary>
    /// <remarks>
    /// Без неё модель не знает, какой сегодня день, и «напомни в пятницу» превращала в случайную
    /// дату. Только дата, без времени: строка меняется раз в сутки, и закэшированное провайдером
    /// начало промпта ломается не чаще. Точное «сейчас» называет ответ инструмента, которому оно
    /// нужно (<c>deferred_task</c>). Последней — по той же причине: всё выше остаётся неизменным.
    /// </remarks>
    internal static string DateLine(DateTimeOffset now) =>
        "Local date: " + now.ToString("yyyy-MM-dd, dddd", System.Globalization.CultureInfo.InvariantCulture) +
        " (UTC" + (now.Offset < TimeSpan.Zero ? "-" : "+") + now.Offset.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture) + ").";
}
