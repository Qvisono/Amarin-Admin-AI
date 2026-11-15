using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Отчёт агента уходит чату целиком: обрезка длинного вывода касается прочих инструментов, но не
/// <c>init_agent</c>, <c>message_agent</c> и <c>agent_refused</c>. Обрезанный отчёт чат
/// переспрашивал через <c>message_agent</c>, и это стоило лишнего прогона агента.
/// </summary>
/// <remarks>
/// Проверяются оба пути, которыми текст попадает модели: подготовка вызова для API и запрос,
/// уже лежащий в сессии (там обрезка живёт своим правилом, по имени сообщения).
/// </remarks>
public sealed class AgentReportLimitTests
{
    private static string LongText() => new('r', ChatToolPreview.ApiLimit * 3);

    [Theory]
    [InlineData("init_agent")]
    [InlineData("message_agent")]
    [InlineData("agent_refused")]
    public void An_agent_report_reaches_the_chat_model_whole(string tool)
    {
        var text = LongText();
        var result = ToolResult.Ok(text);

        Assert.Equal(text, ChatToolPreview.FormatCallForApi(tool, result));
        Assert.False(ChatToolPreview.IsCallTruncatedForApi(tool, result));
    }

    [Fact]
    public void Other_tool_output_is_still_clipped_for_the_model()
    {
        var text = LongText();
        var result = ToolResult.Ok(text);

        var forApi = ChatToolPreview.FormatCallForApi("read_file", result);

        // Потолок остался: иначе список папки на десять тысяч строк уезжал бы в каждый запрос.
        Assert.True(forApi.Length < text.Length);
        Assert.StartsWith(text[..ChatToolPreview.ApiLimit], forApi, StringComparison.Ordinal);
        Assert.True(ChatToolPreview.IsCallTruncatedForApi("read_file", result));
    }

    [Fact]
    public void A_failed_agent_call_keeps_its_whole_error()
    {
        var text = LongText();
        var result = ToolResult.Fail(text);

        var forApi = ChatToolPreview.FormatCallForApi("init_agent", result);

        // Отказ агента читается моделью как ошибка, но причина в нём не теряется.
        Assert.StartsWith("ERROR: ", forApi, StringComparison.Ordinal);
        Assert.EndsWith(text, forApi, StringComparison.Ordinal);
    }

    [Fact]
    public void The_request_keeps_a_stored_agent_report_whole_and_clips_other_tools()
    {
        var text = LongText();
        var messages = new List<ChatMessage>
        {
            new() { Role = "tool", ToolCallId = "a", Name = "init_agent", Content = ChatContent.Text(text) },
            new() { Role = "tool", ToolCallId = "b", Name = "read_file", Content = ChatContent.Text(text) }
        };

        var prepared = ApiContextLimiter.Prepare(messages);

        Assert.Equal(text, ChatContent.ReadText(prepared[0].Content)!);
        Assert.True(ChatContent.ReadText(prepared[1].Content)!.Length < text.Length);
    }
}
