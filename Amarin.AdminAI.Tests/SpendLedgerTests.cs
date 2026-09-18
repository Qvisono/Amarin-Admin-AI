using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Собственный журнал трат программы.
/// </summary>
/// <remarks>
/// Venice ведёт свой, куда более полный, но отдаёт его только админ-ключу: на живом ключе
/// <c>billing/usage-history</c> отвечает <c>401 Admin API key required</c>, хотя документация
/// обещает обратное. Отсюда и этот журнал, и всё, что здесь проверяется.
/// </remarks>
public sealed class SpendLedgerTests : IDisposable
{
    private const string Key = "vk-ledger-key-0123456789";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-ledger-" + Guid.NewGuid().ToString("N"));

    public SpendLedgerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static VeniceCost Usd(decimal amount) => new() { Usd = amount, HasData = true };

    [Fact]
    public void A_charge_lands_in_todays_bucket()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record(Key, Usd(0.25m), "grok-4-6");

        var day = Assert.Single(ledger.Read(Key).Days);
        Assert.Equal(DateTime.Now.Date, day.Date);
        Assert.Equal(0.25m, day.Usd);
    }

    [Fact]
    public void Charges_for_one_model_add_up()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record(Key, Usd(0.1m), "grok-4-6");
        ledger.Record(Key, Usd(0.2m), "grok-4-6");
        ledger.Record(Key, Usd(0.4m), "web-search-request");

        var day = Assert.Single(ledger.Read(Key).Days);
        Assert.Equal(0.7m, day.Usd);
        Assert.Equal(2, day.Skus.Count);
        Assert.Equal(0.3m, day.Skus.Single(sku => sku.Sku == "grok-4-6").Usd);
        Assert.Equal(2, day.Skus.Single(sku => sku.Sku == "grok-4-6").Requests);
    }

    [Fact]
    public void A_free_turn_is_not_written_down()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record(Key, new VeniceCost { HasData = true }, "grok-4-6");

        Assert.Empty(ledger.Read(Key).Days);
    }

    [Fact]
    public void The_journal_survives_a_restart()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record(Key, Usd(0.25m), "grok-4-6");
        ledger.Flush();

        Assert.Equal(0.25m, Assert.Single(new SpendLedger(_root).Read(Key).Days).Usd);
    }

    /// <summary>Журнал принадлежит ключу: у другого он свой.</summary>
    [Fact]
    public void Two_keys_keep_separate_journals()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record(Key, Usd(0.25m), "grok-4-6");

        Assert.Empty(ledger.Read("vk-another-key-9876543210").Days);
    }

    [Fact]
    public void The_file_is_not_named_after_the_key()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record(Key, Usd(0.25m), "grok-4-6");
        ledger.Flush();

        var files = Directory.GetFiles(Path.Combine(_root, "usage"));
        Assert.All(files, file => Assert.DoesNotContain("ledger-key", file, StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── перенос из переписок ─────────────────────────

    private static ChatSession SessionWith(DateTime when, decimal usd, string model = "grok-4-6")
    {
        var session = new ChatSession { Id = "s" + Guid.NewGuid().ToString("N") };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", CreatedAt = when, Text = "?" });
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "assistant",
            CreatedAt = when,
            ResolvedModelId = model,
            Cost = new VeniceCost { Usd = usd, HasData = true }
        });
        return session;
    }

    /// <summary>
    /// Без переноса у обновившегося человека график был бы пуст, хотя цена каждого ответа
    /// давно лежит в его же файлах чатов.
    /// </summary>
    [Fact]
    public void Prices_already_saved_in_chats_move_into_the_journal()
    {
        var ledger = new SpendLedger(_root);
        var yesterday = DateTime.Now.Date.AddDays(-1).AddHours(12);

        Assert.True(ledger.Backfill(Key, [SessionWith(yesterday, 0.3m), SessionWith(yesterday, 0.2m)]));

        var day = Assert.Single(ledger.Read(Key).Days);
        Assert.Equal(yesterday.Date, day.Date);
        Assert.Equal(0.5m, day.Usd);
    }

    /// <summary>
    /// Второй перенос удвоил бы те же деньги. Отметка в журнале закрывает эту дверь навсегда —
    /// в том числе для следующего запуска программы.
    /// </summary>
    [Fact]
    public void The_move_happens_exactly_once()
    {
        var ledger = new SpendLedger(_root);
        var sessions = new[] { SessionWith(DateTime.Now.Date.AddHours(9), 0.3m) };

        Assert.True(ledger.Backfill(Key, sessions));
        Assert.False(ledger.Backfill(Key, sessions));
        Assert.Equal(0.3m, Assert.Single(ledger.Read(Key).Days).Usd);

        ledger.Flush();
        Assert.False(new SpendLedger(_root).Backfill(Key, sessions));
        Assert.Equal(0.3m, Assert.Single(new SpendLedger(_root).Read(Key).Days).Usd);
    }

    [Fact]
    public void Free_and_user_messages_are_skipped()
    {
        var ledger = new SpendLedger(_root);
        var session = new ChatSession { Id = "s1" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", CreatedAt = DateTime.Now, Text = "?" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", CreatedAt = DateTime.Now });

        ledger.Backfill(Key, [session]);

        Assert.Empty(ledger.Read(Key).Days);
    }

    /// <summary>Живой учёт после переноса пишет только то, что платится сейчас.</summary>
    [Fact]
    public void Live_charges_after_the_move_are_not_double_counted()
    {
        var ledger = new SpendLedger(_root);
        var today = DateTime.Now.Date.AddHours(9);
        ledger.Backfill(Key, [SessionWith(today, 0.3m)]);
        ledger.Record(Key, Usd(0.2m), "grok-4-6");

        Assert.Equal(0.5m, Assert.Single(ledger.Read(Key).Days).Usd);
    }

    // ───────────────────────── удаление переписок ─────────────────────────
    //
    // График показывает, сколько ушло с ключа, а не сколько лежит на диске. Удалённый чат денег
    // не возвращает, и убирать его траты из журнала значило бы врать о потраченном — тем более
    // что удаляют как раз старые переписки, по которым и смотрят «Всё время».

    /// <summary>Журнал трат и переписки — разные папки, и удаление ходит только по своей.</summary>
    [Fact]
    public void Deleting_a_chat_leaves_the_journal_alone()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record(Key, Usd(0.25m), "grok-4-6");
        ledger.Flush();

        var chats = new ChatStore(_root);
        var session = SessionWith(DateTime.Now, 0.25m);
        chats.Save(session);
        chats.Flush();

        Assert.True(chats.Delete(session.Id));
        Assert.Null(chats.TryLoad(session.Id));

        // Через новый журнал, а не через живой: важно, что деньги остались на диске, а не только
        // в памяти того объекта, который их записал.
        Assert.Equal(0.25m, Assert.Single(new SpendLedger(_root).Read(Key).Days).Usd);
    }

    /// <summary>«Удалить все чаты» — та же дорога, и журнала она тоже не касается.</summary>
    [Fact]
    public void Wiping_every_chat_leaves_the_journal_alone()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record(Key, Usd(0.1m), "grok-4-6");
        ledger.Record(Key, Usd(0.4m), VeniceSku.ChatSummary);
        ledger.Flush();

        var chats = new ChatStore(_root);
        chats.Save(SessionWith(DateTime.Now, 0.1m));
        chats.Save(SessionWith(DateTime.Now, 0.4m));
        chats.Flush();
        chats.DeleteAll();

        Assert.Empty(chats.List());

        var report = SpendPeriods.Build(
            new SpendLedger(_root).Read(Key), SpendPeriod.All, DateTime.Now, null);

        Assert.Equal(0.5m, report.TotalUsd);
        Assert.Equal(2, report.Models.Count);
    }

    /// <summary>
    /// Перенесённые из переписок деньги переживают удаление той переписки, из которой пришли.
    /// </summary>
    /// <remarks>
    /// Перенос одноразовый — отметка <c>BackfilledAt</c> закрывает дверь навсегда, — поэтому
    /// в журнале лежит копия, а не ссылка. Повторный заход после удаления ничего не пересчитывает
    /// и обнулить её не может.
    /// </remarks>
    [Fact]
    public void Money_moved_from_a_chat_outlives_that_chat()
    {
        var ledger = new SpendLedger(_root);
        var chats = new ChatStore(_root);
        var yesterday = DateTime.Now.Date.AddDays(-1).AddHours(12);
        var session = SessionWith(yesterday, 0.3m);
        chats.Save(session);
        chats.Flush();

        Assert.True(ledger.Backfill(Key, [session]));
        ledger.Flush();

        chats.Delete(session.Id);

        // Второй заход на страницу «Key & Info» после удаления: переносить уже нечего, и деньги
        // обязаны остаться теми же.
        var reopened = new SpendLedger(_root);
        Assert.False(reopened.Backfill(Key, chats.List().Select(item => chats.TryLoad(item.Id)!)));
        Assert.Equal(0.3m, Assert.Single(reopened.Read(Key).Days).Usd);
    }
}

/// <summary>Отказ Venice в журнале — не поломка, а повод взять свой.</summary>
public sealed class SpendAdminKeyFallbackTests : IDisposable
{
    private const string Key = "vk-fallback-key-0123456789";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-fallback-" + Guid.NewGuid().ToString("N"));

    public SpendAdminKeyFallbackTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Ровно то, чем Venice отвечает обычному ключу — снято с живого API.</summary>
    private sealed class AdminOnly : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"error":"Admin API key required"}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private (SpendService Service, SpendLedger Ledger, AdminOnly Handler) Build()
    {
        var handler = new AdminOnly();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var venice = new VeniceClient(http, new AgentOptions { ApiKey = Key });
        var ledger = new SpendLedger(_root);
        return (new SpendService(venice, new SpendHistoryStore(_root), ledger), ledger, handler);
    }

    [Fact]
    public async Task A_refused_journal_falls_back_to_our_own()
    {
        var (service, ledger, _) = Build();
        ledger.Record(Key, new VeniceCost { Usd = 0.4m, HasData = true }, "grok-4-6");

        var report = await service.GetReportAsync(Key, SpendPeriod.Week, force: true);

        Assert.Equal(SpendStatus.Local, report.Status);
        Assert.Equal(0.4m, report.TotalUsd);
    }

    /// <summary>
    /// Ответ по одному и тому же ключу не изменится до перезапуска — биться в закрытую дверь
    /// при каждой смене отрезка незачем.
    /// </summary>
    [Fact]
    public async Task The_closed_door_is_knocked_on_once()
    {
        var (service, _, handler) = Build();

        await service.GetReportAsync(Key, SpendPeriod.Week, force: true);
        await service.GetReportAsync(Key, SpendPeriod.Month, force: true);
        await service.GetReportAsync(Key, SpendPeriod.Year, force: true);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task The_refusal_is_told_apart_from_a_network_failure()
    {
        var (service, _, _) = Build();

        var report = await service.GetReportAsync(Key, SpendPeriod.Week, force: true);

        Assert.NotEqual(SpendStatus.Failed, report.Status);
        Assert.NotEqual(SpendStatus.Stale, report.Status);
    }
}
