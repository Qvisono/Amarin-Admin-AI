using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class ChatShareCodecTests
{
    private static ChatSession BuildSession()
    {
        var session = new ChatSession
        {
            Id = "original",
            Title = "Диагностика диска",
            CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
            UpdatedAt = new DateTime(2026, 1, 2, 4, 0, 0, DateTimeKind.Local),
            SelectedModelId = "grok-4-6"
        };

        for (var i = 1; i <= 3; i++)
        {
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "user",
                Id = $"u{i}",
                Text = $"вопрос {i}",
                CreatedAt = DateTime.Now
            });
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "assistant",
                Id = $"a{i}",
                Text = $"ответ {i}",
                RequestedModelId = "grok-4-6",
                ResolvedModelId = "grok-4-6",
                Status = AssistantStatus.Complete,
                Cost = new VeniceCost { Usd = 0.001m, HasData = true },
                CreatedAt = DateTime.Now
            });
            session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text($"вопрос {i}") });
            session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text($"ответ {i}") });
        }

        return session;
    }

    [Fact]
    public void Whole_chat_roundtrips()
    {
        var session = BuildSession();

        var decoded = ChatShareCodec.TryDecode(ChatShareCodec.Encode(session));

        Assert.NotNull(decoded);
        Assert.Equal(6, decoded!.Messages.Count);
        Assert.Equal(6, decoded.ApiMessages.Count);
        Assert.Equal("grok-4-6", decoded.SelectedModelId);
        Assert.Equal("ответ 3", decoded.Messages[^1].Text);
        Assert.Equal("grok-4-6", decoded.Messages[^1].ResolvedModelId);
        Assert.Equal(AssistantStatus.Complete, decoded.Messages[^1].Status);
    }

    [Fact]
    public void Shared_chat_gets_a_new_id_so_it_cannot_overwrite_a_local_one()
    {
        var session = BuildSession();

        var decoded = ChatShareCodec.TryDecode(ChatShareCodec.Encode(session));

        Assert.NotNull(decoded);
        Assert.NotEqual("original", decoded!.Id);
        Assert.Contains("(общий)", decoded.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Sharing_up_to_a_message_cuts_both_histories()
    {
        var session = BuildSession();

        var decoded = ChatShareCodec.TryDecode(ChatShareCodec.Encode(session, "a2"));

        Assert.NotNull(decoded);
        Assert.Equal(4, decoded!.Messages.Count);
        Assert.Equal("ответ 2", decoded.Messages[^1].Text);
        // Two user turns kept, so the API history must stop before the third question.
        Assert.Equal(4, decoded.ApiMessages.Count);
        Assert.DoesNotContain(
            decoded.ApiMessages,
            m => m.Content?.GetString()?.Contains("вопрос 3", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Images_travel_with_the_shared_chat()
    {
        var session = BuildSession();
        session.Messages[0].Images = [new ImageAttachment("iVBORw0KGgo=", "image/png", "shot.png")];

        var decoded = ChatShareCodec.TryDecode(ChatShareCodec.Encode(session));

        Assert.NotNull(decoded);
        var image = Assert.Single(decoded!.Messages[0].Images);
        Assert.Equal("iVBORw0KGgo=", image.Base64);
        Assert.Equal("shot.png", image.Label);
    }

    [Fact]
    public void Code_survives_line_breaks_introduced_by_a_paste()
    {
        var code = ChatShareCodec.Encode(BuildSession());
        var mangled = code[..20] + "\r\n" + code[20..40] + "\n  " + code[40..];

        Assert.NotNull(ChatShareCodec.TryDecode(mangled));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("обычный поисковый запрос")]
    [InlineData("AMRN1:")]
    [InlineData("AMRN1:не-base64!!!")]
    [InlineData("AMRN1:aGVsbG8")]     // valid base64, not gzip
    public void Garbage_is_rejected(string? code)
    {
        Assert.Null(ChatShareCodec.TryDecode(code));
    }

    private const string LongPayload = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGH";

    [Theory]
    [InlineData("AMRN1:" + LongPayload, true)]
    [InlineData("amrn1:" + LongPayload, true)]
    [InlineData("  AMRN1:" + LongPayload + "  ", true)]
    [InlineData("AMRN1:", false)]
    [InlineData("AMRN1:abcdef", false)]   // too short to be a real chat — user still typing
    [InlineData("диск", false)]
    [InlineData(null, false)]
    public void Prefix_detection_is_cheap_and_case_insensitive(string? text, bool expected)
    {
        Assert.Equal(expected, ChatShareCodec.LooksLikeShareCode(text));
    }

    [Fact]
    public void Typing_the_prefix_by_hand_is_not_treated_as_a_code()
    {
        // Otherwise the sidebar would pop "код повреждён" on every keystroke.
        const string typed = "AMRN1:";
        for (var i = 1; i <= typed.Length; i++)
        {
            Assert.False(ChatShareCodec.LooksLikeShareCode(typed[..i]));
        }
    }

    [Fact]
    public void A_real_code_always_clears_the_length_floor()
    {
        var minimal = new ChatSession { Id = "s", Title = "т" };
        minimal.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "?" });

        Assert.True(ChatShareCodec.LooksLikeShareCode(ChatShareCodec.Encode(minimal)));
    }

    [Fact]
    public void Json_export_is_readable_and_imports_back()
    {
        var session = BuildSession();

        var json = ChatShareCodec.ExportJson(session, "a2");

        // "Голый файл" — no compression, no encoding: the model id must be visible in the text.
        Assert.Contains("grok-4-6", json, StringComparison.Ordinal);
        Assert.Contains("вопрос 1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("вопрос 3", json, StringComparison.Ordinal);

        var imported = ChatShareCodec.TryImportJson(json);
        Assert.NotNull(imported);
        Assert.Equal(4, imported!.Messages.Count);
        Assert.Equal("grok-4-6", imported.SelectedModelId);
    }

    [Fact]
    public void Json_import_rejects_unrelated_files()
    {
        Assert.Null(ChatShareCodec.TryImportJson("{\"hello\":\"world\"}"));
        Assert.Null(ChatShareCodec.TryImportJson("not json at all"));
        Assert.Null(ChatShareCodec.TryImportJson(""));
    }

    [Fact]
    public void Encoding_actually_compresses()
    {
        var session = BuildSession();
        // Repetitive text is the realistic case and must not come back bigger.
        session.Messages[1].Text = string.Concat(Enumerable.Repeat("одинаковый текст ответа. ", 400));

        var code = ChatShareCodec.Encode(session);
        var raw = ChatShareCodec.ExportJson(session);

        Assert.True(code.Length < raw.Length / 4, $"code {code.Length} vs raw {raw.Length}");
    }
}
