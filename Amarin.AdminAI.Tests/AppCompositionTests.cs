using Amarin.Composition;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Связи полного графа служб — того, что собирает программа, — без окна и на временной папке.
/// </summary>
/// <remarks>
/// До 1.30.0 граф собирался посреди <c>Program.RunWpf</c> и не проверялся ничем: копия настроек
/// без делегата лимитов или книги остатков платила бы мимо них молча. Статику процесса
/// (<see cref="AppComposition.ApplyProcessWide"/>) тест не трогает — её ставит только запуск.
/// </remarks>
public sealed class AppCompositionTests : IDisposable
{
    private const string EnvironmentKey = "vk-composition-probe-0123456789";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-compose-" + Guid.NewGuid().ToString("N"));

    public AppCompositionTests() => Directory.CreateDirectory(Path.Combine(_root, "chats"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка — уберёт система.
        }
    }

    [Fact]
    public void Every_copy_of_the_options_sees_the_program_wide_ledger_book_gate_and_audit()
    {
        using var services = Build([]);
        var options = services.Options;

        Assert.Same(services.SpendGuard, options.SpendGate?.Target);
        Assert.Same(services.Balances, options.BalanceSink?.Target);
        Assert.NotNull(services.Audit);
        Assert.Same(services.Audit, options.Audit);
        Assert.Same(services.Keys, options.Keys);

        // Точка учёта пишет в общий журнал и тут же сверяет трату с лимитами: предупреждение
        // обязано прозвучать на том запросе, что подвёл к порогу.
        services.Settings.SpendLimits = new SpendLimits { DayUsd = 1m, WarnPercent = 80 };
        var warned = new List<SpendBreach>();
        services.SpendGuard.Warned = warned.Add;
        options.SpendSink!("sink-key", new VeniceCost { Usd = 0.9m, HasData = true }, "chat");

        Assert.Equal(0.9m, services.Ledger.Totals("sink-key", DateTime.Now).KeyDay);
        Assert.Single(warned);
    }

    [Fact]
    public void The_profile_folder_holds_chats_and_the_environment_key_is_known_and_scrubbed()
    {
        var secrets = new SecretRegistry();
        using var services = Build([], secrets);

        Assert.Equal(_root, services.Profile.DataRoot);
        Assert.Equal(EnvironmentKey, services.Keys.Current);
        Assert.Contains(EnvironmentKey, secrets.Current);

        services.ChatStore.Save(new ChatSession
        {
            Id = "c1",
            Title = "Чат",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            Messages = [new ChatDisplayMessage { Role = "user", Id = "u1", CreatedAt = DateTime.Now, Text = "привет" }]
        });
        services.ChatStore.Flush();
        Assert.Contains(new ChatStore(_root).List(), entry => entry.Id == "c1");
    }

    [Fact]
    public void The_download_allowlist_is_seeded_once_into_the_profile_settings()
    {
        using var services = Build([]);

        Assert.NotNull(services.Settings.DownloadAllowedDomains);
        Assert.Equal(
            new DownloadOptions().AllowedDomains,
            new AppSettingsStore(_root).Load().DownloadAllowedDomains);
    }

    [Fact]
    public void A_model_from_the_command_line_becomes_the_default_and_is_remembered()
    {
        using var services = Build(["--model", "glm-5"]);

        Assert.Equal("glm-5", services.Options.Model);
        Assert.Equal("glm-5", new AppSettingsStore(_root).Load().ChatModelId);
    }

    [Fact]
    public async Task Limits_read_the_settings_of_the_profile_open_now()
    {
        // Лимиты, включённые в настройках в памяти, действуют сразу — без записи на диск.
        using var services = Build([]);
        services.Settings.SpendLimits = new SpendLimits { TurnUsd = 0.01m };
        var meter = new SpendMeter();
        meter.Add(new VeniceCost { Usd = 0.02m, HasData = true });

        using (SpendScope.Push(meter))
        {
            // Спросить некого (окна нет) — значит отказ.
            var refused = await Assert.ThrowsAsync<SpendLimitException>(
                () => services.SpendGuard.CheckAsync(new ApiCredential(LlmProvider.Venice, EnvironmentKey), CancellationToken.None));
            Assert.Equal(SpendLimitKind.Turn, refused.Breach.Kind);
        }
    }

    private AppServices Build(string[] args, SecretRegistry? secrets = null)
    {
        var settingsStore = new AppSettingsStore(_root);
        var profile = new StartupProfile(
            new ProfileStore(),
            new ProfileRegistry(),
            _root,
            settingsStore,
            settingsStore.Load());

        return AppComposition.Build(
            new AppConfiguration(
                EnvironmentKey,
                "",
                "https://api.venice.ai/api/v1",
                "grok-4-6",
                30,
                "off",
                EnableWebCitations: true,
                EnableXSearch: null,
                new DownloadOptions()),
            profile,
            StartupArgs.Parse(args),
            wiped: null,
            secrets ?? new SecretRegistry());
    }
}
