using System.Diagnostics;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

internal sealed partial class ChatEngine
{
    /// <summary>
    /// Слушает композер, пока идёт раунд инструментов, и решает судьбу работающих агентов.
    /// </summary>
    /// <remarks>
    /// Граница раунда — единственное место, где дописанное сообщение можно положить в
    /// стенограмму, но не единственное, где оно нужно: агент живёт внутри вызова инструмента и
    /// к границе уже отработает. Просьба «стоп» или «быстрее», дождавшаяся границы, опаздывает
    /// ровно на всю работу, которую просили не делать. Поэтому строка снимается с очереди сразу
    /// (в контекст она попадёт всё равно — <see cref="DrainQueued"/> получит её списком), а
    /// решение принимает отдельный короткий запрос: основная модель в этот момент занята
    /// ожиданием инструментов и ответить не может.
    /// <para>
    /// Ни одна ошибка отсюда не должна ронять ход: это помощник, а не часть ответа.
    /// </para>
    /// </remarks>
    private async Task WatchFollowUpsAsync(
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        ToolRound round,
        List<string> taken,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(FollowUpPoll, cancellationToken).ConfigureAwait(false);

                var fresh = new List<string>();
                while (observer.TryTakeQueuedMessage(out var text))
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        fresh.Add(text.Trim());
                    }
                }

                if (fresh.Count == 0)
                {
                    continue;
                }

                taken.AddRange(fresh);

                // Человеку видно, что его услышали, ещё до того, как модель что-то скажет.
                round.FollowUpNote = Loc.Get("S.Tools.FollowUpSeen");
                observer.OnToolsChanged(assistant);

                var running = _agents?.ListFor(session.Id) ?? [];
                if (running.Count == 0)
                {
                    continue;
                }

                var decide = FollowUpDecider ?? DecideOnFollowUpAsync;
                var decision = await decide(
                        string.Join(Environment.NewLine, fresh), running, cancellationToken)
                    .ConfigureAwait(false);

                if (decision.Kind == AgentInterruptKind.None)
                {
                    continue;
                }

                foreach (var id in decision.AgentIds)
                {
                    var agent = running.FirstOrDefault(item =>
                        string.Equals(item.Id, id, StringComparison.Ordinal));
                    if (agent is null)
                    {
                        continue;
                    }

                    // Вводная не прерывает: агент дорабатывает шаг и читает её на границе раунда.
                    // Прервать его ради уточнения значило бы выбросить то, что он уже нашёл.
                    if (decision.Kind == AgentInterruptKind.Tell)
                    {
                        agent.Tell(decision.Message);
                    }
                    else
                    {
                        agent.Request(new AgentInterrupt(decision.Kind, decision.Complexity));
                    }
                }

                round.FollowUpNote = string.IsNullOrWhiteSpace(decision.Note)
                    ? Loc.Get(decision.Kind switch
                    {
                        AgentInterruptKind.Stop => "S.Tools.AgentStopped",
                        AgentInterruptKind.Tell => "S.Tools.AgentTold",
                        _ => "S.Tools.AgentSwitched"
                    })
                    : decision.Note;
                observer.OnToolsChanged(assistant);
            }
        }
        catch (OperationCanceledException)
        {
            // Раунд закончился или ход отменили — обычный конец наблюдения.
        }
        catch (Exception)
        {
            // Сбой помощника не имеет права стать сбоем ответа.
        }
    }

    /// <summary>Быстрая модель из настроек: решение нужно за секунды и почти даром.</summary>
    private async Task<FollowUpDecision> DecideOnFollowUpAsync(
        string text,
        IReadOnlyList<RunningAgent> agents,
        CancellationToken cancellationToken)
    {
        var settings = _settings();
        var modelId = string.IsNullOrWhiteSpace(settings.AgentFastModelId)
            ? AgentHost.ForcedAgentModelId
            : settings.AgentFastModelId.Trim();

        // Свой HttpClient по той же причине, что и у агента: у служебного запроса свой короткий
        // таймаут, а у клиента — свой счёт потраченного, который не должен смешиваться с ходом.
        using var http = HttpClients.Create(HttpClients.ServiceTimeout);
        return await FollowUpDirector
            .DecideAsync(
                new VeniceClient(http, _options),
                modelId,
                text,
                agents,
                cancellationToken,
                KeyFor(SlotBinding(ModelSlot.AgentFast, modelId)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Закрывает ответ в точке, где в контекст вплетено дописанное.
    /// </summary>
    /// <remarks>
    /// Руками, а не через <see cref="FinishAssistant"/>: тот дописал бы в историю второе сообщение
    /// ассистента, а сообщение этого раунда с вызовами инструментов уже там, и дубль после ответов
    /// инструментов API отвергает. Текст выбрасывается: сказанное моделью в этом раунде уже
    /// сохранено репликой раунда, а дважды это читалось бы как заикание.
    /// </remarks>
    private static void CloseAssistantForFollowUp(
        ChatSession session,
        ChatDisplayMessage assistant,
        Stopwatch clock,
        VeniceTurnContext turn,
        IChatTurnObserver observer)
    {
        assistant.Text = "";
        assistant.Duration = turn.ElapsedBefore + clock.Elapsed;
        assistant.ResolvedModelId = turn.ModelId;
        assistant.Status = AssistantStatus.Complete;
        Settle(session, assistant, turn);
        session.UpdatedAt = DateTime.Now;
        observer.OnAssistantContinued(assistant);
    }
}
