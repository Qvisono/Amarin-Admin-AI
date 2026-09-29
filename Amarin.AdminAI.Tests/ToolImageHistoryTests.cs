using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Картинки инструментов лежат в истории модели с ролью <c>user</c>. Разрез истории считал их
/// ходами человека и при перегенерации второго ответа терял итог первого и сам второй вопрос —
/// модель получала историю без вопроса, на который отвечала.
/// </summary>
public sealed class ToolImageHistoryTests
{
    /// <summary>U1 → A1 со скриншотом → U2 → A2.</summary>
    private static ChatSession ScreenshotThenQuestion()
    {
        var session = new ChatSession { Id = "s" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "что на экране" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a1", Text = "рабочий стол" });
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u2", Text = "а теперь?" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a2", Text = "всё так же" });

        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("что на экране") });
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "assistant",
            ToolCalls =
            [
                new ToolCall { Id = "c1", Function = new FunctionCall { Name = "capture_screenshot", Arguments = "{}" } }
            ]
        });
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "tool",
            ToolCallId = "c1",
            Name = "capture_screenshot",
            Content = ChatContent.Text("ok")
        });
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "user",
            Content = ChatContent.ToolImages(
                "capture_screenshot",
                "Вставь это изображение в ответ.",
                [new ImageAttachment("AAAA", "image/png")])
        });
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("рабочий стол") });
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("а теперь?") });
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("всё так же") });
        return session;
    }

    private static List<string> Texts(ChatSession session) =>
        session.ApiMessages.Select(item => ChatContent.ReadText(item.Content) ?? item.Role).ToList();

    [Fact]
    public void A_tool_picture_is_not_a_turn_of_the_person()
    {
        var picture = new ChatMessage
        {
            Role = "user",
            Content = ChatContent.ToolImages("capture_screenshot", "x", [new ImageAttachment("AAAA", "image/png")])
        };

        Assert.True(ChatContent.IsToolImage(picture));
        Assert.False(ChatContent.IsTurnStart(picture));
    }

    [Fact]
    public void A_person_quoting_the_same_words_is_still_a_turn()
    {
        // Без вложений сообщение человека уходит строкой, а не массивом частей, — поэтому одной
        // совпавшей строки для «картинки инструмента» мало.
        var typed = new ChatMessage { Role = "user", Content = ChatContent.Text(ChatContent.ToolImagePrefix + "x") };

        Assert.False(ChatContent.IsToolImage(typed));
        Assert.True(ChatContent.IsTurnStart(typed));
    }

    [Fact]
    public void Regenerating_the_second_answer_keeps_the_first_answer_and_the_second_question()
    {
        var session = ScreenshotThenQuestion();

        Assert.True(ChatSessionEdit.TruncateFromMessage(session, "a2"));

        var texts = Texts(session);
        Assert.Equal("рабочий стол", texts[^2]);
        Assert.Equal("а теперь?", texts[^1]);
        Assert.Equal(6, session.ApiMessages.Count);
    }

    [Fact]
    public void Deleting_the_second_turn_cuts_exactly_that_turn()
    {
        var session = ScreenshotThenQuestion();

        Assert.True(ChatSessionEdit.DeleteTurn(session, "a2"));

        Assert.Equal(["u1", "a1"], session.Messages.Select(m => m.Id));
        Assert.Equal(5, session.ApiMessages.Count);
        Assert.Equal("рабочий стол", Texts(session)[^1]);
    }

    [Fact]
    public void Editing_the_second_question_rewrites_that_question_and_not_the_picture()
    {
        var session = ScreenshotThenQuestion();

        Assert.True(ChatSessionEdit.ReplaceUserText(session, "u2", "покажи снова"));

        Assert.True(ChatContent.IsToolImage(session.ApiMessages[3]));
        Assert.Equal("покажи снова", Texts(session)[^1]);
        Assert.Equal("рабочий стол", Texts(session)[^2]);
    }

    [Fact]
    public void The_cut_before_an_answer_ends_right_after_its_question()
    {
        var session = ScreenshotThenQuestion();

        Assert.Equal(6, ChatSessionEdit.ApiCut(session, 3));
        Assert.Equal(5, ChatSessionEdit.ApiCut(session, 2));
        Assert.Equal(1, ChatSessionEdit.ApiCut(session, 1));
        Assert.Equal(0, ChatSessionEdit.ApiCut(session, 0));
        Assert.Equal(7, ChatSessionEdit.ApiCut(session, 4));
    }

    [Fact]
    public void Sharing_up_to_the_first_answer_keeps_its_picture_and_final_words()
    {
        var session = ScreenshotThenQuestion();

        var json = ChatShareCodec.ExportJson(session, "a1");
        var shared = ChatShareCodec.TryImportJson(json)!;

        Assert.Equal(["u1", "a1"], shared.Messages.Select(m => m.Id));
        Assert.Equal(5, shared.ApiMessages.Count);
        Assert.Equal("рабочий стол", ChatContent.ReadText(shared.ApiMessages[^1].Content));
    }
}
