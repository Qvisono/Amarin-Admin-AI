using System.Text.Json;

namespace Amarin.Core;

/// <summary>Решение по дописанному сообщению: трогать ли работающих агентов.</summary>
/// <param name="AgentIds">Кого именно. Пусто при <see cref="AgentInterruptKind.None"/>.</param>
/// <param name="Complexity">Уровень для пересадки: <c>fast</c>, <c>lite</c> или <c>heavy</c>.</param>
/// <param name="Note">Одна строка для человека — она и показывается в раздумьях.</param>
/// <param name="Message">Что передать агенту дословно. Только для <c>Tell</c>.</param>
internal sealed record FollowUpDecision(
    AgentInterruptKind Kind,
    IReadOnlyList<string> AgentIds,
    string Complexity,
    string Note,
    string Message = "")
{
    public static FollowUpDecision None { get; } = new(AgentInterruptKind.None, [], "", "");
}

/// <summary>
/// Читает сообщение, дописанное человеком во время работы, и решает, что делать с агентами,
/// которые в этот момент работают.
/// </summary>
/// <remarks>
/// Отдельный короткий запрос, а не ход чата: пока агент выполняется внутри вызова инструмента,
/// основная модель занята ожиданием и физически не может ни ответить, ни вызвать что-либо ещё.
/// Просьба «быстрее» или «отмени» доходила до неё только после того, как агент отработал.
/// <para>
/// Запрос идёт мимо <see cref="ChatEngine"/>: ни системного промпта, ни инструментов, ни истории
/// — один вопрос и один ответ, как в <see cref="JournalExplainer"/>. Модель берётся быстрая
/// (<see cref="AppSettings.AgentFastModelId"/>): решение нужно за секунды и стоить почти ничего.
/// </para>
/// </remarks>
internal static class FollowUpDirector
{
    /// <summary>Задание агента в подсказке цитируется до этого — дальше оно только шумит.</summary>
    private const int MaxQuotedPrompt = 300;

    /// <summary>Сам вопрос. Открыт для тестов: важно, что в нём перечислены живые агенты.</summary>
    internal static string BuildPrompt(string userText, IReadOnlyList<RunningAgent> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var text =
            "Пользователь дописал сообщение, пока на его компьютере работают запущенные агенты.\n" +
            "Реши, что сделать с ними прямо сейчас. Ответь ОДНИМ объектом JSON и ничем больше:\n" +
            "{\"action\":\"none\",\"agents\":[],\"complexity\":\"\",\"message\":\"\"," +
            "\"note\":\"\"}\n\n" +
            "action:\n" +
            "  none - сообщение вообще не про эту работу: отдельный вопрос, болтовня, " +
            "новая задача на потом.\n" +
            "  tell - уточнение, поправка или важная новая вводная по тому, чем агент занят " +
            "прямо сейчас: не тот диск, не та папка, ещё одно условие, дополнительный адрес.\n" +
            "  stop - просит прекратить: отмени, стоп, не надо, хватит, я передумал.\n" +
            "  switch - просит сменить исполнителя: быстрее (complexity fast), тщательнее или " +
            "умнее (complexity heavy).\n" +
            "agents: идентификаторы из списка ниже. Пусто при none, иначе хотя бы один. " +
            "Для tell - только тот агент, к чьей задаче это относится.\n" +
            "complexity: только для switch, ровно fast, lite или heavy.\n" +
            "message: только для tell. Сама вводная для агента, одной строкой и по делу: он не " +
            "видит чат и знает лишь своё задание, поэтому пиши так, чтобы было понятно без " +
            "переписки.\n" +
            "note: одно короткое предложение на языке сообщения о том, что ты делаешь. " +
            "Без вступлений.\n" +
            "Сомневаешься - none. Ошибочная остановка стоит человеку всей уже сделанной работы.\n\n" +
            "Работают:\n";

        foreach (var agent in agents)
        {
            var prompt = agent.Prompt.Length <= MaxQuotedPrompt
                ? agent.Prompt
                : agent.Prompt[..MaxQuotedPrompt] + "…";
            text += $"{agent.Id} [{agent.Complexity}, {agent.ModelId}]: {prompt}\n";
        }

        return text + "\nСообщение пользователя:\n" + userText.Trim();
    }

    /// <summary>
    /// Разбор ответа. Всё непонятное — это <see cref="FollowUpDecision.None"/>: неверно понятое
    /// «стоп» убивает работу, которую уже сделали, а неверно понятое «ничего» всего лишь ждёт.
    /// </summary>
    /// <param name="userText">Сообщение человека — им подменяется вводная, которую модель не написала.</param>
    internal static FollowUpDecision Parse(
        string answer,
        IReadOnlyList<RunningAgent> agents,
        string userText = "")
    {
        ArgumentNullException.ThrowIfNull(agents);

        JsonElement root;
        try
        {
            // Тот же разбор, что и у аргументов инструментов: модель так же кладёт JSON в
            // ```json, дописывает пояснение после скобки и склеивает два объекта подряд.
            root = ToolArguments.Parse(answer);
        }
        catch (JsonException)
        {
            return FollowUpDecision.None;
        }

        var kind = ReadString(root, "action").ToLowerInvariant() switch
        {
            "stop" => AgentInterruptKind.Stop,
            "switch" => AgentInterruptKind.Switch,
            "tell" => AgentInterruptKind.Tell,
            _ => AgentInterruptKind.None
        };

        if (kind == AgentInterruptKind.None)
        {
            return FollowUpDecision.None;
        }

        var known = agents.Select(agent => agent.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        if (root.TryGetProperty("agents", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var id = (item.ValueKind == JsonValueKind.String ? item.GetString() : null)?.Trim() ?? "";
                if (known.Contains(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(agents.First(agent =>
                        string.Equals(agent.Id, id, StringComparison.OrdinalIgnoreCase)).Id);
                }
            }
        }

        // «Останови» без перечисления — это про всё, что работает: человек видит один индикатор,
        // а не четыре слота. Уточнение без адресата — тоже всем: лучше услышат все, чем никто.
        if (ids.Count == 0)
        {
            ids.AddRange(agents.Select(agent => agent.Id));
        }

        if (ids.Count == 0)
        {
            return FollowUpDecision.None;
        }

        var complexity = ReadString(root, "complexity").ToLowerInvariant();
        if (kind == AgentInterruptKind.Switch && complexity is not "fast" and not "lite" and not "heavy")
        {
            // Пересадка без уровня — это просьба поторопиться: только она и приходит без слов.
            complexity = "fast";
        }

        // Вводная, которую модель не переписала, — это ровно то, что человек и написал.
        var message = ReadString(root, "message").Trim();
        if (kind == AgentInterruptKind.Tell && message.Length == 0)
        {
            message = userText.Trim();
        }

        if (kind == AgentInterruptKind.Tell && message.Length == 0)
        {
            return FollowUpDecision.None;
        }

        return new FollowUpDecision(kind, ids, complexity, ReadString(root, "note").Trim(), message);
    }

    /// <summary>Спрашивает модель. Любой сбой — «ничего не делать»: работа агентов дороже.</summary>
    public static async Task<FollowUpDecision> DecideAsync(
        VeniceClient client,
        string modelId,
        string userText,
        IReadOnlyList<RunningAgent> agents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(agents);

        if (agents.Count == 0 || string.IsNullOrWhiteSpace(userText))
        {
            return FollowUpDecision.None;
        }

        try
        {
            var response = await client.CreateChatCompletionAsync(
                    modelId,
                    [
                        new ChatMessage
                        {
                            Role = "user",
                            Content = ChatContent.Text(BuildPrompt(userText, agents))
                        }
                    ],
                    tools: null,
                    toolChoice: null,
                    new VeniceParameters(),
                    cancellationToken,
                    ReasoningChoice.Disabled)
                .ConfigureAwait(false);

            var answer = ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "";
            return Parse(answer, agents, userText);
        }
        catch (OperationCanceledException)
        {
            return FollowUpDecision.None;
        }
        catch (Exception)
        {
            // Помощник, а не часть ответа: сеть легла — агенты просто доработают сами.
            return FollowUpDecision.None;
        }
    }

    private static string ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
