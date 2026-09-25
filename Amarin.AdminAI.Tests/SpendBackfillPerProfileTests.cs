using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Разовый перенос цен из переписок в журнал трат.
/// </summary>
/// <remarks>
/// Отметка о переносе стояла у ключа, а переписки принадлежат профилю: заведя второй ключ,
/// человек получал на его графике всю историю чатов, оплаченную первым, — два ключа показывали
/// одну и ту же сумму до цента. Какой ключ платил за переписку, в ней не записано, поэтому
/// переносить её вообще можно только один раз на профиль.
/// </remarks>
public sealed class SpendBackfillPerProfileTests : IDisposable
{
    private const string KeyOne = "vk-first-0123456789";
    private const string KeyTwo = "sk-or-v1-9876543210";

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string Root()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-backfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        _roots.Add(root);
        return root;
    }

    private static ChatSession Paid(DateTime when, decimal usd, string model = "grok-4-6")
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

    private static decimal Total(SpendLedger ledger, string secret) =>
        SpendPeriods.Build(ledger.Read(secret), SpendPeriod.All, DateTime.Now, null).TotalUsd;

    private AppServices Build(string root)
    {
        var options = new AgentOptions
        {
            ApiKey = KeyOne,
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6"
        };

        var http = new HttpClient { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var download = new HttpClient { BaseAddress = new Uri("https://example.invalid/") };
        var venice = new VeniceClient(http, options);
        var settingsStore = new AppSettingsStore(root);
        var settings = settingsStore.Load();
        var keyStore = new ApiKeyStore(root);
        keyStore.Load();

        return new AppServices
        {
            Options = options,
            SettingsStore = settingsStore,
            Settings = settings,
            ChatStore = new ChatStore(root),
            Prompts = new PromptLibrary(root),
            Instructions = new InstructionLibrary(root),
            KeyStore = keyStore,
            Ledger = new SpendLedger(root),
            Keys = new ApiKeyProvider(),
            EnvironmentKey = "",
            Profiles = new ProfileStore(),
            ProfileRegistry = new ProfileRegistry(),
            Http = http,
            DownloadHttp = download,
            Venice = venice,
            Models = new VeniceModelListCache(venice),
            Balances = new BalanceBook(),
            Chat = new ChatEngine(venice, options, () => settings, new ToolRegistry([])),
            Titles = new ChatTitleGenerator(http, options, () => settings),
            Summaries = new ChatSummaryGenerator(http, options, () => settings),
            Confirmations = new ConfirmationQueue(() => settings)
        };
    }

    /// <summary>
    /// Тот самый случай из отчёта: завели второй ключ — и его график повторил траты первого.
    /// </summary>
    [Fact]
    public void A_second_key_does_not_inherit_the_first_ones_history()
    {
        var root = Root();
        using var services = Build(root);

        services.KeyStore.Add("Венис", KeyOne);
        services.ChatStore.Save(Paid(DateTime.Now.Date.AddDays(-1).AddHours(12), 0.4m));
        services.ChatStore.Flush();

        services.BackfillSpendLedger();
        Assert.Equal(0.4m, Total(services.Ledger, KeyOne));

        // Человек добавил ключ другого провайдера и зашёл на страницу трат.
        services.KeyStore.Add("Роутер", KeyTwo, LlmProvider.OpenRouter);
        services.ApplyActiveKey();
        services.BackfillSpendLedger();

        Assert.Equal(0m, Total(services.Ledger, KeyTwo));
        Assert.Equal(0.4m, Total(services.Ledger, KeyOne));
    }

    /// <summary>Отметка переживает перезапуск: иначе перенос повторялся бы каждое утро.</summary>
    [Fact]
    public void The_mark_survives_a_restart()
    {
        var root = Root();
        using (var first = Build(root))
        {
            first.ChatStore.Save(Paid(DateTime.Now, 0.2m));
            first.ChatStore.Flush();
            first.BackfillSpendLedger();
        }

        using var second = Build(root);
        Assert.NotNull(second.Settings.SpendBackfilledAt);

        second.BackfillSpendLedger();
        second.Ledger.Flush();

        Assert.Equal(0.2m, Total(second.Ledger, ""));
    }

    /// <summary>
    /// Починка тем, кто уже успел получить лишний импорт. Вычитается ровно он: живые траты,
    /// записанные после переноса, остаются на месте.
    /// </summary>
    [Fact]
    public void The_repair_takes_back_the_duplicate_and_leaves_live_spending()
    {
        var root = Root();
        var ledger = new SpendLedger(root);
        var yesterday = DateTime.Now.Date.AddDays(-1).AddHours(12);
        var sessions = new[] { Paid(yesterday, 0.4m) };

        // Как было до починки: перенос достался обоим ключам.
        ledger.Backfill(KeyOne, sessions);
        Thread.Sleep(10);
        ledger.Backfill(KeyTwo, sessions);

        // А этим вторым ключом человек уже успел заплатить по-настоящему.
        ledger.Record(KeyTwo, new VeniceCost { Usd = 0.05m, HasData = true }, "openrouter:openai/gpt-5");
        ledger.Flush();

        Assert.Equal(0.45m, Total(ledger, KeyTwo));

        Assert.Equal(1, ledger.RepairDuplicateBackfills(sessions));

        Assert.Equal(0.05m, Total(ledger, KeyTwo));
        Assert.Equal(0.4m, Total(ledger, KeyOne));
    }

    /// <summary>
    /// Переписки, написанные уже после лишнего импорта, в него не попадали — вычитать их
    /// значило бы отнять у человека настоящие деньги.
    /// </summary>
    [Fact]
    public void Chats_written_after_the_import_are_not_taken_back()
    {
        var root = Root();
        var ledger = new SpendLedger(root);
        var older = new[] { Paid(DateTime.Now.Date.AddDays(-2).AddHours(12), 0.4m) };

        ledger.Backfill(KeyOne, older);
        Thread.Sleep(10);
        ledger.Backfill(KeyTwo, older);
        ledger.Flush();

        // Эта переписка появилась после обоих переносов, но в файлах чатов лежит рядом.
        var afterwards = Paid(DateTime.Now, 0.3m);
        ledger.Record(KeyTwo, new VeniceCost { Usd = 0.3m, HasData = true }, "grok-4-6");
        ledger.Flush();

        ledger.RepairDuplicateBackfills([.. older, afterwards]);

        Assert.Equal(0.3m, Total(ledger, KeyTwo));
    }

    /// <summary>Один ключ с переносом — чинить нечего, и трогать его нельзя.</summary>
    [Fact]
    public void A_single_import_is_left_alone()
    {
        var root = Root();
        var ledger = new SpendLedger(root);
        var sessions = new[] { Paid(DateTime.Now.Date.AddDays(-1).AddHours(12), 0.4m) };

        ledger.Backfill(KeyOne, sessions);
        ledger.Flush();

        Assert.Equal(0, ledger.RepairDuplicateBackfills(sessions));
        Assert.Equal(0.4m, Total(ledger, KeyOne));
    }

    /// <summary>
    /// Починка не уводит корзины в минус: отрицательная трата читалась бы как «модель
    /// заплатила человеку». Переписка, удалённая после импорта, оставит немного лишнего —
    /// это безопасная сторона ошибки.
    /// </summary>
    [Fact]
    public void The_repair_never_goes_below_zero()
    {
        var root = Root();
        var ledger = new SpendLedger(root);
        var yesterday = DateTime.Now.Date.AddDays(-1).AddHours(12);

        ledger.Backfill(KeyOne, [Paid(yesterday, 0.1m)]);
        Thread.Sleep(10);
        ledger.Backfill(KeyTwo, [Paid(yesterday, 0.1m)]);
        ledger.Flush();

        // Переписок стало больше, чем было в импорте, — вычитать нужно всё равно не больше,
        // чем там лежит.
        ledger.RepairDuplicateBackfills([Paid(yesterday, 0.1m), Paid(yesterday, 9m)]);

        Assert.Equal(0m, Total(ledger, KeyTwo));
    }
}
