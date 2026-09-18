using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Пометки списаний: под какой статьёй служебные запросы попадают в журнал трат.
/// </summary>
/// <remarks>
/// Сводки, заголовки, маршрутизатор, защита, перевод интерфейса и разъяснения платятся токенами
/// обычной модели. Без пометки их деньги сливаются в разбивке со строкой этой модели, и на вопрос
/// «сколько ушло на сводки» ответить нечем — а глазами это не поймать: сумма-то верна.
/// </remarks>
public sealed class SpendChargeLabelTests
{
    private static readonly List<VeniceModelInfo> Catalogue =
    [
        new() { Id = "grok-4-6" },
        new() { Id = "claude-sonnet-5" }
    ];

    [Theory]
    [InlineData(VeniceSku.ChatSummary, "Сводка чата")]
    [InlineData(VeniceSku.ChatSearch, "Поиск по чатам")]
    [InlineData(VeniceSku.ChatTitle, "Заголовок чата")]
    [InlineData(VeniceSku.Router, "Маршрутизатор")]
    [InlineData(VeniceSku.Guard, "Защита")]
    [InlineData(VeniceSku.Translate, "Перевод интерфейса")]
    [InlineData(VeniceSku.Explain, "Разъяснения")]
    [InlineData(VeniceSku.FollowUp, "Уточнения на ходу")]
    [InlineData(VeniceSku.Infographic, "Инфографика")]
    [InlineData(VeniceSku.WebSearch, "Поиск в интернете")]
    [InlineData(VeniceSku.Scrape, "Чтение страниц")]
    public void Every_service_charge_gets_its_own_row(string sku, string title)
    {
        var identity = VeniceSku.Resolve(sku, Catalogue);

        Assert.Null(identity.ModelId);
        Assert.Equal(title, VeniceSku.Title(identity));
    }

    /// <summary>
    /// Ради этого наши пометки и разбираются точным совпадением: статья <c>search</c> из таблицы
    /// Venice ищется вхождением подстроки и увела бы поиск по чатам в строку поиска в интернете.
    /// </summary>
    [Fact]
    public void Chat_search_is_not_swallowed_by_web_search()
    {
        var chats = VeniceSku.Resolve(VeniceSku.ChatSearch, Catalogue);
        var web = VeniceSku.Resolve(VeniceSku.WebSearch, Catalogue);

        Assert.NotEqual(VeniceSku.GroupKey(web), VeniceSku.GroupKey(chats));
    }

    /// <summary>Статьи Venice разбираются как раньше — свой словарь их не перехватывает.</summary>
    [Fact]
    public void Venices_own_skus_are_untouched()
    {
        Assert.Equal("grok-4-6", VeniceSku.Resolve("grok-4-6-llm-output-mtoken", Catalogue).ModelId);
        Assert.Equal(
            "Поиск в интернете",
            VeniceSku.Title(VeniceSku.Resolve("websearch-mtoken", Catalogue)));
    }

    // ───────────────────────── живой клиент ─────────────────────────

    private static VeniceClient Client(HttpMessageHandler handler, out List<string> skus)
    {
        var captured = new List<string>();
        skus = captured;
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        return new VeniceClient(http, new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            SpendSink = (_, _, sku) => captured.Add(sku)
        });
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(script(request));
    }

    /// <summary>
    /// Область восстанавливает прежнюю пометку, а не обнуляет её.
    /// </summary>
    /// <remarks>
    /// Поиск в сети умеет случиться внутри служебного запроса, и раньше он в <c>finally</c> писал
    /// в пометку <c>null</c>. С вложенными областями это значило бы, что всё, за что заплатят
    /// после поиска, уедет под именем модели — а увидеть это можно только в журнале трат, задним
    /// числом и не разобравшись почему.
    /// </remarks>
    [Fact]
    public async Task A_nested_charge_label_restores_the_outer_one()
    {
        using var handler = new ScriptedHandler(_ => Json(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"готово\"}}]," +
            "\"cost\":{\"usd\":0.001,\"diem\":0}}"));
        var client = Client(handler, out var skus);

        using (VeniceClient.ChargeAs(VeniceSku.ChatSummary))
        {
            await client.SearchWebAsync("что-нибудь");
            await client.CreateChatCompletionAsync(
                "grok-4-6",
                [new ChatMessage { Role = "user", Content = ChatContent.Text("сводка") }],
                tools: null,
                toolChoice: null,
                new VeniceParameters());
        }

        Assert.Equal([VeniceSku.WebSearch, VeniceSku.ChatSummary], skus);
    }

    /// <summary>Без пометки списание по-прежнему идёт на модель хода.</summary>
    [Fact]
    public async Task An_unlabelled_charge_still_goes_to_the_model()
    {
        using var handler = new ScriptedHandler(_ => Json(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"готово\"}}]," +
            "\"cost\":{\"usd\":0.001,\"diem\":0}}"));
        var client = Client(handler, out var skus);

        await client.CreateChatCompletionAsync(
            "grok-4-6",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("привет") }],
            tools: null,
            toolChoice: null,
            new VeniceParameters());

        Assert.Equal("grok-4-6", Assert.Single(skus));
    }

    /// <summary>
    /// Картинка помечается той моделью, которой её и рисовали.
    /// </summary>
    /// <remarks>
    /// Раньше в пометку шёл параметр метода, а не вычисленная из него модель. Из чата параметр
    /// приходит пустым, и в журнал ложилось голое <c>-image</c>: строка «Картинки» собиралась
    /// верно, а сырой sku в её подсказке не говорил ничего.
    /// </remarks>
    [Fact]
    public async Task A_picture_is_charged_to_the_model_that_drew_it()
    {
        using var handler = new ScriptedHandler(_ => Json(
            "{\"images\":[\"aGVsbG8=\"],\"cost\":{\"usd\":0.04,\"diem\":0}}"));
        var client = Client(handler, out var skus);

        await client.GenerateImageAsync("кот", model: null);

        Assert.Equal(VeniceClient.DefaultImageModel + "-image", Assert.Single(skus));
    }
}
