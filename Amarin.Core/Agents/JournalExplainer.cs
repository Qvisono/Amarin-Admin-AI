namespace Amarin.Core;

/// <summary>
/// Объясняет запись журнала простыми словами — моделью без всего лишнего.
/// </summary>
/// <remarks>
/// Намеренно не через <see cref="ChatEngine"/>: тот всегда ставит техпромпт и даёт модели три
/// десятка инструментов, а вопрос здесь — «что сделал этот вызов», и модель с редактором реестра
/// в руках норовит ответить, сходив посмотреть. Ни системного промпта, ни инструментов, ни истории —
/// один вопрос, один ответ.
/// </remarks>
internal static class JournalExplainer
{
    /// <summary>
    /// Аргументы и вывод вставляются целиком до этого предела; дальше ответ из объяснения
    /// превращается в пересказ журнала.
    /// </summary>
    private const int MaxQuoted = 2_000;

    /// <summary>Вопрос с процитированной записью. Открыт для тестов.</summary>
    internal static string BuildPrompt(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var status = entry.Status switch
        {
            ToolCallStatus.Failed => "завершился ошибкой",
            ToolCallStatus.Done => entry.Success ? "выполнен успешно" : "завершился ошибкой",
            _ => "не завершился"
        };

        var text =
            "Объясни человеку, что сделала с его компьютером вот эта запись журнала.\n" +
            "Структурно, кратко и по делу: что это за инструмент, что именно он изменил или " +
            "прочитал, чем это грозит и на что стоит обратить внимание. Без вступлений и без " +
            "пересказа задания. По-русски.\n\n" +
            $"Инструмент: {entry.ToolName}\n" +
            $"Статус: {status}\n";

        if (!string.IsNullOrWhiteSpace(entry.AgentName))
        {
            text += $"Выполнил: {entry.AgentName}\n";
        }

        if (!string.IsNullOrWhiteSpace(entry.ArgumentsJson))
        {
            text += "\nАргументы:\n" + Quote(entry.ArgumentsJson);
        }

        var result = string.IsNullOrWhiteSpace(entry.ResultText) ? entry.ResultPreview : entry.ResultText;
        if (!string.IsNullOrWhiteSpace(result))
        {
            text += "\n\nРезультат:\n" + Quote(result);
        }

        return text;
    }

    /// <summary>
    /// Спрашивает модель. Возвращает её ответ или фразу, по которой человек может действовать:
    /// неудавшееся объяснение здесь — обычный исход, а не авария.
    /// </summary>
    public static async Task<string> ExplainAsync(
        VeniceClient client,
        string modelId,
        JournalEntry entry,
        CancellationToken cancellationToken = default,
        ApiCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var charge = VeniceClient.ChargeAs(VeniceSku.Explain);
        try
        {
            var response = await client.CreateChatCompletionAsync(
                    modelId,
                    [new ChatMessage { Role = "user", Content = ChatContent.Text(BuildPrompt(entry)) }],
                    tools: null,
                    toolChoice: null,
                    new VeniceParameters(),
                    cancellationToken,
                    ReasoningChoice.Disabled,
                    credential)
                .ConfigureAwait(false);

            var answer = ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "";
            return string.IsNullOrWhiteSpace(answer) ? Loc.Get("S.Journal.AskEmpty") : answer.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Ловим всё намеренно: зовут нас из async void обработчика, и любое вылетевшее
            // исключение уронило бы окно, а «объяснение не получилось» — обычный исход вопроса к
            // удалённой модели.
            return Loc.Get("S.Journal.AskFailed");
        }
    }

    private static string Quote(string text) =>
        text.Length <= MaxQuoted ? text : text[..MaxQuoted] + "…";
}
