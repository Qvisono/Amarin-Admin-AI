namespace Amarin.Core;

/// <summary>
/// Explains one journal entry in plain language, using a model with nothing attached to it.
/// </summary>
/// <remarks>
/// Deliberately not routed through <see cref="ChatEngine"/>. That one always prepends the tech
/// prompt and hands the model thirty-odd tools, which is exactly wrong here: the question is
/// "what did this call do", and a model holding a registry editor is liable to answer it by going
/// and looking. No system prompt, no tools, no chat history — one question, one answer.
/// </remarks>
internal static class JournalExplainer
{
    /// <summary>
    /// Arguments and output are pasted in whole up to this; past it the answer stops being an
    /// explanation and starts being a re-reading of the log.
    /// </summary>
    private const int MaxQuoted = 2_000;

    /// <summary>The question, with the entry quoted into it. Exposed for tests.</summary>
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
    /// Asks the model. Returns its answer, or a sentence a person can act on — a failed
    /// explanation is an ordinary outcome here, not a crash.
    /// </summary>
    public static async Task<string> ExplainAsync(
        VeniceClient client,
        string modelId,
        JournalEntry entry,
        CancellationToken cancellationToken = default)
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
                    ReasoningChoice.Disabled)
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
            // Caught wholesale on purpose: this is called from an async void handler, so anything
            // that escapes takes the window with it — and "объяснение не получилось" is an ordinary
            // outcome of asking a remote model, not a crash.
            return Loc.Get("S.Journal.AskFailed");
        }
    }

    private static string Quote(string text) =>
        text.Length <= MaxQuoted ? text : text[..MaxQuoted] + "…";
}
