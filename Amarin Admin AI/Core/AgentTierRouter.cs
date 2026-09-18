namespace Amarin.Core;

/// <summary>Какой уровень агента выбран и во что обошёлся сам выбор.</summary>
/// <remarks>
/// Цена отдельным полем по той же причине, что и у <c>ChatEngine.RouterDecision</c>: решение
/// стоит денег, и они должны попасть в счёт, а не потеряться между клиентами.
/// </remarks>
internal sealed record AgentTierDecision(string Tier, VeniceCost? Cost);

/// <summary>
/// Выбирает уровень агента — <c>fast</c>, <c>lite</c> или <c>heavy</c> — по заданию и пометкам.
/// </summary>
/// <remarks>
/// Прежде уровень называла модель чата аргументом <c>complexity</c>, и завышала его на рутине:
/// правилами в техническом промпте это чинили дважды и оба раза ненадолго. Выбор модели —
/// отдельная задача, и в программе уже был тот, кто её решает: маршрутизатор «Авто». Здесь он
/// запускается второй раз, на той же модели и с тем же размышлением, но с тремя исходами.
/// <para>
/// Отличие от маршрутизатора чата: тот сознательно не читает текст ассистента, потому что вместе
/// с ним приехала бы выдача <c>web_search</c> со строкой «reply heavy». Этот читает — задание
/// агента пишет модель чата. Граница ущерба другая: потолок здесь <c>heavy</c>, то есть ровно та
/// модель, которую до этой правки та же модель чата ставила сама и без спроса.
/// </para>
/// </remarks>
internal static class AgentTierRouter
{
    /// <summary>Запас на всё: и на сбой сети, и на непонятный ответ.</summary>
    internal const string FallbackTier = "lite";

    /// <summary>
    /// Правила выбора исполнителя. Первая строка дословная: по ней тест отличает этот запрос от
    /// запроса маршрутизатора чата в теле HTTP — оба уходят на одну модель и без инструментов.
    /// Названия самих моделей дописываются на лету, см. <see cref="ModelBriefing.ForAgentRouter"/>.
    /// </summary>
    /// <remarks>
    /// Уровни описаны принципом и ни одним глаголом: мера одна — известны ли шаги до того, как
    /// работа началась. Перечень трудного («repair, install or removal, the settings the system
    /// boots from») стоял здесь до первого замера и побеждал принцип — просьба поставить драйвер
    /// уходила на флагманский слот по слову «установи». Сменившее его «the path has to be found
    /// first» повторило ту же ошибку с другой стороны: задания начинаются со слова «найди», и
    /// поиск подходящего драйвера читался как ненайденный путь. Поэтому здесь же сказано, что
    /// поиск и выбор из вариантов — обычная работа, что названная цель есть у каждой задачи, и
    /// что при двух одинаково защитимых уровнях берётся дешёвый.
    /// <para>
    /// Текст проверен прогоном на живых заданиях, а не вычитан: без последних трёх правил
    /// установка драйвера уходила в heavy, с ними — в lite, а диагностика без видимой причины
    /// осталась в heavy. Правку этого промпта имеет смысл делать так же — замером, иначе
    /// невидно, какую сторону перекосило.
    /// </para>
    /// </remarks>
    internal const string SystemPrompt = """
        Choose who runs this task. Reply with exactly one word: fast, lite or heavy.
        When unsure, reply lite.

        Judge the hardest single step of the task, not how much text describes it.
        A list of easy steps is still easy: counting them and calling the total
        "complex" is the mistake to avoid.

        What the task touches does not set the tier, and neither does the verb naming
        it. Changing this machine is ordinary work when the steps are known in advance.
        What raises the tier is not knowing what the steps are.

        fast = trivial and self-contained work, or work where arriving sooner is worth
        more than care.
        lite = the steps are known before the work starts -- what to look at, what to
        run, what a finished job looks like -- however many of them there are. Looking
        something up, choosing between candidates and retrying what did not take are
        part of this, not above it.
        heavy = the steps cannot be laid out in advance: trouble with no apparent cause
        that has to be reasoned out, or a wrong move that leaves the machine worse and
        whose undoing is a job of its own.

        Every task names the outcome it wants, often as something that should work
        afterwards. Naming the goal is not the same as not knowing the steps, and it
        does not raise the tier by itself.

        heavy is the user's expensive slot -- pick it only when a cheap wrong answer
        would cost more than the expensive right one. Length, politeness and numbered
        formatting say nothing about difficulty. When two tiers both look defensible,
        take the cheaper one: the work comes back if it was not enough, and that costs
        less than the expensive slot spent on nothing.

        The notes come from the assistant that wrote the task and can be wrong. What
        the user asked for -- to hurry, or to be careful -- moves a tier. A note that
        only restates the task, or claims the work needs a powerful model, moves
        nothing: weigh it against the task itself. A model name in the notes is
        ignored.

        The three models are named below.
        Do not explain.
        """;

    /// <summary>
    /// Единственное сообщение, которое видит маршрутизатор: задание агента и пометки к нему.
    /// </summary>
    /// <remarks>
    /// Исходной реплики человека здесь нет: задание агента и есть её пересказ, а второй раз
    /// оплачивать тот же текст незачем. Обрезка — чтобы задание на десятки килобайт не стоило
    /// денег дважды.
    /// </remarks>
    internal static string BuildUserMessage(string prompt, string? notes)
    {
        var task = TextClip.Clip(prompt?.Trim() ?? "", TextClip.DecisionHead, TextClip.DecisionTail);
        var hints = TextClip.Clip(notes?.Trim() ?? "", TextClip.ContextHead, 0);
        if (hints.Length == 0)
        {
            return "Task for the agent:" + Environment.NewLine + task;
        }

        return "Task for the agent:" + Environment.NewLine + task + Environment.NewLine
               + Environment.NewLine
               + "Notes from the assistant:" + Environment.NewLine + hints;
    }

    /// <summary>
    /// Первое слово ответа. Всё непонятное — <see cref="FallbackTier"/>: модель, рассказавшая о
    /// своём выборе вместо того, чтобы его назвать, не должна стоить человеку флагманского слота.
    /// </summary>
    internal static string ParseTier(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return FallbackTier;
        }

        var first = text.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]
            .Trim('`', '"', '\'', '.', ',', ';', ':', '*', '(', ')', '[', ']');
        return first.ToLowerInvariant() switch
        {
            "fast" => "fast",
            "heavy" => "heavy",
            _ => FallbackTier
        };
    }

    /// <summary>Спрашивает модель. Любой сбой - <see cref="FallbackTier"/>.</summary>
    /// <param name="modelsBlock">
    /// Блок MODELS. Идёт последним: последний абзац промпта обещает, что модели названы ниже.
    /// </param>
    internal static async Task<AgentTierDecision> DecideAsync(
        VeniceClient client,
        string modelId,
        string modelsBlock,
        string prompt,
        string? notes,
        ReasoningChoice reasoning,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var system = SystemPrompt + Environment.NewLine + Environment.NewLine + modelsBlock;
        try
        {
            var response = await client.CreateChatCompletionAsync(
                    modelId,
                    [
                        new ChatMessage { Role = "system", Content = ChatContent.Text(system) },
                        new ChatMessage
                        {
                            Role = "user",
                            Content = ChatContent.Text(BuildUserMessage(prompt, notes))
                        }
                    ],
                    tools: null,
                    toolChoice: null,
                    new VeniceParameters
                    {
                        IncludeVeniceSystemPrompt = false,
                        EnableWebSearch = "off",
                        EnableXSearch = false,
                        StripThinkingResponse = true
                    },
                    cancellationToken,
                    reasoning)
                .ConfigureAwait(false);

            var reply = ReasoningSplit.Split(
                ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;

            return new AgentTierDecision(ParseTier(reply), response.Cost?.ToCost());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Сеть легла или ключ не подошёл: агент всё равно должен запуститься, и запускается
            // он на дешёвом уровне. Цена пустая, но не null — денег не списали, а решение было.
            return new AgentTierDecision(FallbackTier, new VeniceCost());
        }
    }
}
