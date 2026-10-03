using System.Diagnostics;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

internal sealed partial class ChatEngine
{
    private static VeniceCost SumCosts(VeniceCost chatCost, ChatDisplayMessage assistant)
    {
        var total = chatCost;
        foreach (var round in assistant.ToolRounds)
        {
            foreach (var call in round.Calls)
            {
                if (call.NestedAgent?.Cost is { HasData: true } agentCost)
                {
                    total = total.Add(agentCost);
                }
            }
        }

        // Заголовок считался в стороне от хода, в chatCost его нет: прибавляем, а не вычитаем.
        if (assistant.TitleCost is { HasData: true } title)
        {
            total = total.Add(title);
        }

        // Защитник - тоже в стороне: у него свой клиент, да ещё и внутри агента, от которого ход
        // закрыт VeniceTurnScope.Suppress(). В chatCost его денег нет ни при каком раскладе.
        if (assistant.GuardCost is { HasData: true } guard)
        {
            total = total.Add(guard);
        }

        return total;
    }

    /// <summary>
    /// Записывает счёт хода и рядом — разбивку, которую читает подсказка цены.
    /// </summary>
    /// <remarks>
    /// <paramref name="chatCost"/> — траты общего <see cref="VeniceClient"/>: разговор и инструменты,
    /// которые платят через него (рисование, чтение страниц). Те же списания
    /// <c>AgentRunScope.Charge</c> пишет и на строки инструментов, так что за их вычетом остаётся
    /// ровно цена самой модели. Вложенные агенты работают на своём клиенте — их цена прибавляется,
    /// а не вычитается.
    /// </remarks>
    internal static void ApplyCosts(ChatDisplayMessage assistant, VeniceCost chatCost)
    {
        var tools = VeniceCost.Zero;
        foreach (var round in assistant.ToolRounds)
        {
            foreach (var call in round.Calls)
            {
                // Вложенный агент платит из своего клиента, в chatCost его нет - вычитать
                // его отсюда значило бы увести строку «Модель» в минус.
                if (call.NestedAgent is null && call.Cost is { HasData: true } cost)
                {
                    tools = tools.Add(cost);
                }
            }
        }

        // Маршрутизатор платит тем же клиентом, что и разговор, поэтому его деньги уже внутри
        // chatCost. Без этого вычитания строка «Модель» показывала бы ещё и выбор модели, а
        // сумма строк перестала бы сходиться с «Итого» под ними.
        var router = assistant.RouterCost is { HasData: true } routed ? routed : VeniceCost.Zero;

        assistant.Cost = SumCosts(chatCost, assistant);
        assistant.ModelCost = chatCost.Subtract(tools).Subtract(router);
    }

    /// <summary>
    /// Закрывает счёт сообщения: переносит на него то, что относится ко всему ходу
    /// (маршрутизатор) и ко всему чату (заголовок), и только потом складывает.
    /// </summary>
    /// <remarks>
    /// <see cref="ApplyCosts"/> обязан оставаться пересчётом, а не прибавлением: по одному и
    /// тому же сообщению можно пройти второй раз - например, отмена после того, как ответ уже
    /// закрыт ради дописанного сообщения. Правка, делающая его инкрементальным, молча удвоит
    /// счёт и здесь, и в <see cref="ChatTitleCost"/>.
    /// </remarks>
    private static void Settle(ChatSession session, ChatDisplayMessage assistant, VeniceTurnContext turn)
    {
        // ??= а не =: на втором проходе переписывать нечего.
        assistant.RouterCost ??= turn.RouterCost;
        ChatTitleCost.Attach(session, assistant);
        ApplyCosts(assistant, turn.Total.HasData ? turn.Total : assistant.Cost ?? VeniceCost.Zero);
    }
}
