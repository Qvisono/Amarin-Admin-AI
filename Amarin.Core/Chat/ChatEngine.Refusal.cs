namespace Amarin.Core;

internal sealed partial class ChatEngine
{
    /// <summary>
    /// Ступень после отказа: тяжёлая модель чата, затем обход. <c>null</c> — сильнее модели нет.
    /// </summary>
    /// <remarks>
    /// Место на лестнице узнаётся по самой модели: отказала модель обхода — выше некуда, тяжёлая —
    /// осталась обход, любая другая (лёгкая, выбранная руками, запасная из цепочки) — обе, так
    /// решил человек. Ступень с той же моделью пропускается: та же модель откажет так же.
    /// </remarks>
    internal (ModelSlot Slot, string ModelId)? NextAfterRefusal(string refusedModelId)
    {
        var settings = _settings();
        var heavy = FirstNonEmpty(settings.HeavyModelId, LiteModelId());
        var bypass = FirstNonEmpty(settings.BypassModelId, heavy);

        (ModelSlot Slot, string ModelId)[] ladder = Same(refusedModelId, bypass)
            ? []
            : Same(refusedModelId, heavy)
                ? [(ModelSlot.Bypass, bypass)]
                : [(ModelSlot.Heavy, heavy), (ModelSlot.Bypass, bypass)];

        foreach (var step in ladder)
        {
            if (!Same(step.ModelId, refusedModelId))
            {
                return step;
            }
        }

        return null;

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Отдаёт ход ступени выше после <c>i_refuse</c>. <c>false</c> — выше никого, и отказ окончательный.
    /// </summary>
    /// <remarks>
    /// Отказной ответ выбрасывается целиком: в историю модели он не попадает (вызывающий ещё не
    /// записал его), а пузырь очищается — человек видит только ответ той модели, что взялась.
    /// Ключ и размышление — слота ступени, тем же путём, каким «Авто» ставит их после выбора.
    /// Деньги отказавшей попытки уже в счёте хода.
    /// </remarks>
    private bool ClimbAfterRefusal(ChatDisplayMessage assistant, IChatTurnObserver observer, VeniceTurnContext turn)
    {
        if (NextAfterRefusal(turn.ModelId) is not { } next)
        {
            return false;
        }

        var settings = _settings();
        turn.ModelId = next.ModelId;
        turn.Credential = KeyFor(SlotBinding(next.Slot, next.ModelId));
        turn.Reasoning = ((next.Slot == ModelSlot.Bypass ? settings.BypassReasoning : settings.HeavyReasoning)
                          ?? new ReasoningSettings()).ToChoice();

        assistant.Text = "";
        assistant.ResolvedModelId = next.ModelId;
        observer.OnAssistantText(assistant);
        return true;
    }

    /// <summary>
    /// Ответ окончательного отказа: тот же запрос, но вместо его текста и вызовов — «Я, модель,
    /// отказываюсь…». В истории модели остаётся обычный текст, без вызова, который ждал бы ответа.
    /// </summary>
    private static StreamedChatCompletion AsFinalRefusal(StreamedChatCompletion streamed, string modelId, string reasons) =>
        new()
        {
            Text = Refusals.FinalText(modelId, reasons, streamed.Text),
            ThinkingElapsed = streamed.ThinkingElapsed,
            FinishReason = streamed.FinishReason,
            Cost = streamed.Cost,
            PromptTokens = streamed.PromptTokens,
            CompletionTokens = streamed.CompletionTokens,
            TotalTokens = streamed.TotalTokens,
            Model = streamed.Model
        };
}
