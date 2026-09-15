using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// За придуманный заголовок чата человек платит, а в программе этих денег не было видно нигде.
/// </summary>
/// <remarks>
/// Заголовок сочиняется брошенной задачей и может закончиться и раньше первого ответа, и позже
/// того, как тот уже дописан и сохранён. Оба порядка обязаны кончиться одинаково — это и есть
/// то, что здесь проверяется, потому что глазами такую гонку не поймать.
/// </remarks>
public sealed class ChatTitleCostTests
{
    private static VeniceCost Usd(decimal amount) => new() { Usd = amount, HasData = true };

    private static ChatSession WithAnswer(out ChatDisplayMessage answer)
    {
        answer = new ChatDisplayMessage { Role = "assistant", Id = "a1", Text = "готово" };
        var session = new ChatSession { Id = "s1" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "привет" });
        session.Messages.Add(answer);
        return session;
    }

    [Fact]
    public void A_title_priced_before_the_answer_exists_lands_on_it_when_it_finishes()
    {
        var session = new ChatSession { Id = "s1" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "привет" });

        Assert.False(ChatTitleCost.Book(session, Usd(0.0004m)));

        var answer = new ChatDisplayMessage { Role = "assistant", Id = "a1" };
        session.Messages.Add(answer);
        ChatTitleCost.Attach(session, answer);
        ChatEngine.ApplyCosts(answer, Usd(0.02m));

        Assert.Equal(0.0004m, answer.TitleCost?.Usd);
        Assert.Equal(0.0204m, answer.Cost?.Usd);
        Assert.Equal(0.02m, answer.ModelCost?.Usd);
    }

    [Fact]
    public void A_title_priced_after_the_answer_is_done_still_reaches_the_bill()
    {
        var session = WithAnswer(out var answer);
        ChatTitleCost.Attach(session, answer);
        ChatEngine.ApplyCosts(answer, Usd(0.02m));

        Assert.True(ChatTitleCost.Book(session, Usd(0.0004m)));

        Assert.Equal(0.0204m, answer.Cost?.Usd);

        // Этих денег в счёте хода не было, поэтому строку «Модель» они не трогают.
        Assert.Equal(0.02m, answer.ModelCost?.Usd);
    }

    [Fact]
    public void The_title_is_billed_once()
    {
        var session = WithAnswer(out var answer);

        Assert.False(ChatTitleCost.Book(session, Usd(0.0004m)));
        ChatTitleCost.Attach(session, answer);
        ChatEngine.ApplyCosts(answer, Usd(0.02m));

        Assert.False(ChatTitleCost.Book(session, Usd(0.0004m)));
        ChatTitleCost.Attach(session, answer);
        ChatEngine.ApplyCosts(answer, Usd(0.02m));

        Assert.Equal(0.0204m, answer.Cost?.Usd);
    }

    [Fact]
    public void Only_the_first_answer_carries_the_title()
    {
        var session = WithAnswer(out var first);
        var second = new ChatDisplayMessage { Role = "assistant", Id = "a2" };
        session.Messages.Add(second);

        ChatTitleCost.Book(session, Usd(0.0004m));
        ChatTitleCost.Attach(session, second);

        Assert.Null(second.TitleCost);
        Assert.Equal(0.0004m, first.TitleCost?.Usd);
    }

    [Fact]
    public void A_regenerated_first_answer_picks_the_title_price_up_again()
    {
        // Ради этого цена и остаётся на самом чате: «Перегенерировать» выбрасывает сообщение,
        // на котором она была записана, и строка иначе пропала бы навсегда.
        var session = WithAnswer(out var answer);
        ChatTitleCost.Book(session, Usd(0.0004m));

        session.Messages.Remove(answer);
        var again = new ChatDisplayMessage { Role = "assistant", Id = "a2" };
        session.Messages.Add(again);
        ChatTitleCost.Attach(session, again);

        Assert.Equal(0.0004m, again.TitleCost?.Usd);
    }

    [Fact]
    public void A_title_without_a_reported_price_books_nothing()
    {
        // Отсутствующая цифра — это не «бесплатно», это «неизвестно»: строка «$0» соврала бы.
        var session = WithAnswer(out var answer);

        Assert.False(ChatTitleCost.Book(session, null));
        Assert.False(ChatTitleCost.Book(session, new VeniceCost()));

        Assert.Null(session.TitleCost);
        Assert.Null(answer.TitleCost);
    }

    [Fact]
    public async Task The_title_request_is_kept_out_of_the_turn()
    {
        var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Про диск D\"}}]," +
                "\"cost\":{\"usd\":0.0004,\"diem\":0}}",
                Encoding.UTF8,
                "application/json")
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6"
        };
        var titles = new ChatTitleGenerator(http, options, () => new AppSettings());

        var turn = new VeniceTurnContext { RequestedModelId = "grok-4-6", ModelId = "grok-4-6" };
        using var scope = VeniceTurnScope.Push(turn);

        var draft = await titles.GenerateAsync("что на диске D");

        Assert.Equal(0.0004m, draft.Cost?.Usd);

        // Счёт разговора трогать нельзя: иначе цена заголовка попала бы и сюда, и в свою строку.
        Assert.False(turn.Total.HasData);
    }

    private sealed class ScriptedHandler(Func<HttpResponseMessage> script) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(script());
    }
}
