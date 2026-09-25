using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// График трат не зависит от того, живы ли переписки.
/// </summary>
/// <remarks>
/// График отвечает на вопрос «сколько ушло с ключа», а не «сколько лежит на диске»: удалённый чат
/// денег не возвращает. Живой учёт от переписок и не зависит — <c>VeniceClient.AddCost</c> пишет
/// в <c>usage/</c> в момент списания. Зависимость была ровно одна: разовый перенос цен из старых
/// переписок делала страница «Key &amp; Info» по первому заходу, и чат, удалённый раньше него,
/// уносил свои деньги навсегда. Теперь перенос идёт с запуска программы, и проверяется здесь
/// именно это — числами, потому что заметить пропажу можно только по чужому графику.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class SpendSurvivesChatDeletionTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly List<string> _roots = [];

    public SpendSurvivesChatDeletionTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Временная папка останется — не повод валить прогон.
            }
        }
    }

    private static ChatSession Paid(DateTime when, decimal usd)
    {
        var session = new ChatSession { Id = "s" + Guid.NewGuid().ToString("N") };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", CreatedAt = when, Text = "?" });
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "assistant",
            CreatedAt = when,
            ResolvedModelId = "grok-4-6",
            Cost = new VeniceCost { Usd = usd, HasData = true }
        });
        return session;
    }

    private AppServices Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-spend-del-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        _roots.Add(root);

        var options = new AgentOptions
        {
            ApiKey = "vk-deletion-probe-0123456789",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6"
        };

        var http = new HttpClient { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var download = new HttpClient { BaseAddress = new Uri("https://example.invalid/") };
        var venice = new VeniceClient(http, options);
        var settingsStore = new AppSettingsStore(root);
        var settings = settingsStore.Load();

        return new AppServices
        {
            Options = options,
            SettingsStore = settingsStore,
            Settings = settings,
            ChatStore = new ChatStore(root),
            Prompts = new PromptLibrary(root),
            Instructions = new InstructionLibrary(root),
            KeyStore = new ApiKeyStore(root),
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

    private static decimal AllTime(AppServices services) =>
        SpendPeriods.Build(
            services.Ledger.Read(services.KeyStore.ActiveSecret()),
            SpendPeriod.All,
            DateTime.Now,
            null).TotalUsd;

    /// <summary>
    /// Перенесённые деньги остаются на графике после того, как все переписки стёрты.
    /// </summary>
    [Fact]
    public void Wiping_every_chat_does_not_touch_the_chart()
    {
        using var services = Build();
        var yesterday = DateTime.Now.Date.AddDays(-1).AddHours(12);
        services.ChatStore.Save(Paid(yesterday, 0.3m));
        services.ChatStore.Save(Paid(DateTime.Now, 0.2m));
        services.ChatStore.Flush();

        services.BackfillSpendLedger();
        Assert.Equal(0.5m, AllTime(services));

        services.ChatStore.DeleteAll();

        Assert.Empty(services.ChatStore.List());
        Assert.Equal(0.5m, AllTime(services));
    }

    /// <summary>
    /// Живые списания к перепискам вообще не привязаны: журнал пишется в момент списания.
    /// </summary>
    [Fact]
    public void Live_charges_never_depended_on_chats_at_all()
    {
        using var services = Build();
        var secret = services.KeyStore.ActiveSecret();
        services.Ledger.Record(secret, new VeniceCost { Usd = 0.75m, HasData = true }, "grok-4-6");
        services.Ledger.Flush();

        services.ChatStore.Save(Paid(DateTime.Now, 0.75m));
        services.ChatStore.Flush();
        services.ChatStore.DeleteAll();

        Assert.Equal(0.75m, AllTime(services));
    }

    /// <summary>
    /// Перенос запускается с окном, а не по заходу на страницу трат.
    /// </summary>
    /// <remarks>
    /// Это и была та единственная зависимость от переписок: пока на «Key &amp; Info» не зашли,
    /// цены старых ответов жили только в файлах чатов, и удалённый до первого захода чат уносил
    /// их с собой. Ждём с потолком — перенос уходит в фоновый поток, и привязываться к тому,
    /// успел ли он к следующей строке, нельзя.
    /// </remarks>
    [Fact]
    public void Attaching_the_services_starts_the_move_by_itself()
    {
        var services = Build();
        try
        {
            var yesterday = DateTime.Now.Date.AddDays(-1).AddHours(12);
            services.ChatStore.Save(Paid(yesterday, 0.4m));
            services.ChatStore.Flush();

            var window = _wpf.Ui.Invoke(() =>
            {
                var created = new MainWindow();
                created.AttachServices(services);
                return created;
            });

            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (AllTime(services) == 0m && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(25);
                }

                Assert.Equal(0.4m, AllTime(services));
            }
            finally
            {
                _wpf.Ui.Invoke(() =>
                {
                    window.Close();
                    return true;
                });
            }
        }
        finally
        {
            services.Dispose();
        }
    }
}
