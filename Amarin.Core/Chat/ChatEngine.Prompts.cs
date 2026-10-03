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
        var system = BuildSystemPrompt(currentModelId, instructions, session.Profile);

        // Сжатый чат (D10): старая часть уходит сводкой в системном промпте, а не отдельным
        // сообщением — два сообщения user подряд или выдуманный ответ ассистента принимают
        // не все провайдеры.
        IReadOnlyList<ChatMessage> history;
        lock (session.Gate)
        {
            if (ContextCompaction.IsActive(session) && session.CompactSummary is { } summary)
            {
                system = (system + "\n\n" + ContextCompaction.PromptBlock(summary)).Trim();
            }

            history = ContextCompaction.Tail(session);
        }

        if (!string.IsNullOrWhiteSpace(system))
        {
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = ChatContent.Text(system)
            });
        }

        messages.AddRange(ChatMessageCloner.CloneAll(history));
        return messages;
    }

    /// <summary>
    /// Системный промпт следующего запроса. Открыт для кольца контекста: это настоящая часть окна,
    /// а собирать его заново в интерфейсе значило бы раздвоить логику.
    /// </summary>
    /// <param name="session">Чат, чей промпт мерить: у него может быть свой профиль (D11).</param>
    internal string CurrentSystemPrompt(ChatSession? session = null) =>
        BuildSystemPrompt(currentModelId: null, ActiveInstructions(session), session?.Profile);

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
    private string BuildSystemPrompt(string? currentModelId, IReadOnlyList<Instruction> instructions, ChatProfile? profile = null)
    {
        // Только чат: основной промпт + TechAiPrompt. У агента — TechAgentPrompt / BaseSystemPrompt.
        var settings = _settings();
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
        parts.Add(FormulaRules);

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

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }
}
