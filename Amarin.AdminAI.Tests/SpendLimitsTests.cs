using System.Net;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Лимиты трат (E1): правила, итоги журнала, вопрос посреди хода и отказ без человека.</summary>
public sealed class SpendLimitsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-limits-" + Guid.NewGuid().ToString("N"));

    public SpendLimitsTests() => Directory.CreateDirectory(_root);

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

    [Theory]
    [InlineData("5", "5")]
    [InlineData("$2.5", "2.5")]
    [InlineData("1,25", "1.25")]
    [InlineData(" 0.456 ", "0.46")]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("-3", null)]
    [InlineData("five", null)]
    public void A_limit_field_reads_dollars_and_empty_zero_or_junk_is_no_limit(string text, string? expected)
    {
        Assert.Equal(
            expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            SpendRules.ParseUsd(text));
    }

    [Fact]
    public void Only_limits_that_are_set_are_checked_and_the_key_comes_first()
    {
        var limits = new SpendLimits
        {
            MonthUsd = 20m,
            Keys = { ["fp"] = new KeySpendLimit { DayUsd = 1m } }
        };

        var breaches = SpendRules.Evaluate(limits, "fp", new SpendTotals(1.2m, 3m, 2m, 10m));

        Assert.Equal([SpendLimitKind.KeyDay, SpendLimitKind.ProfileMonth], breaches.Select(breach => breach.Kind));
        Assert.True(breaches[0].Reached);
        Assert.False(breaches[1].Reached);
        Assert.DoesNotContain(SpendRules.Evaluate(limits, "other", default), breach => breach.Kind == SpendLimitKind.KeyDay);
    }

    [Fact]
    public void A_warning_is_due_past_the_share_and_before_the_limit()
    {
        Assert.True(SpendRules.ShouldWarn(new SpendBreach(SpendLimitKind.ProfileDay, 10m, 8m), 80));
        Assert.False(SpendRules.ShouldWarn(new SpendBreach(SpendLimitKind.ProfileDay, 10m, 7.99m), 80));
        Assert.False(SpendRules.ShouldWarn(new SpendBreach(SpendLimitKind.ProfileDay, 10m, 10m), 80));
    }

    [Fact]
    public void Totals_count_today_and_this_month_for_the_key_and_for_every_key_of_the_profile()
    {
        var ledger = new SpendLedger(_root);
        ledger.Record("key-a", new VeniceCost { Usd = 1.5m, HasData = true }, "chat");
        ledger.Record("key-b", new VeniceCost { Usd = 0.5m, HasData = true }, "chat");
        ledger.Flush();

        // Свежий журнал того же профиля: чужие ключи он обязан дочитать с диска сам.
        var fresh = new SpendLedger(_root);
        var totals = fresh.Totals("key-a", DateTime.Now);

        Assert.Equal(1.5m, totals.KeyDay);
        Assert.Equal(1.5m, totals.KeyMonth);
        Assert.Equal(2m, totals.ProfileDay);
        Assert.Equal(2m, totals.ProfileMonth);

        // Первое число следующего месяца: сегодняшние траты — уже прошлый месяц.
        var today = DateTime.Now.Date;
        var nextMonth = new DateTime(today.Year, today.Month, 1).AddMonths(1);
        Assert.Equal(default, fresh.Totals("key-a", nextMonth));
    }

    private (SpendGuard Guard, SpendLedger Ledger, AppSettings Settings) Guard(SpendLimits limits)
    {
        var ledger = new SpendLedger(_root);
        var settings = new AppSettings { SpendLimits = limits };
        return (new SpendGuard(() => settings, ledger), ledger, settings);
    }

    private static readonly ApiCredential Key = new(LlmProvider.Venice, "key-a");

    [Fact]
    public async Task Outside_a_turn_a_reached_limit_refuses_without_asking()
    {
        var (guard, ledger, _) = Guard(new SpendLimits { DayUsd = 1m });
        ledger.Record(Key.Secret, new VeniceCost { Usd = 1m, HasData = true }, "chat");
        var asked = 0;
        guard.Ask = (_, _) =>
        {
            asked++;
            return Task.FromResult(true);
        };

        var refusal = await Assert.ThrowsAsync<SpendLimitException>(() => guard.CheckAsync(Key, CancellationToken.None));

        Assert.Equal(SpendLimitKind.ProfileDay, refusal.Breach.Kind);
        Assert.Equal(0, asked);
        Assert.IsAssignableFrom<VeniceApiException>(refusal);
    }

    [Fact]
    public async Task A_scheduled_run_is_refused_without_asking()
    {
        var (guard, ledger, _) = Guard(new SpendLimits { DayUsd = 1m });
        ledger.Record(Key.Secret, new VeniceCost { Usd = 2m, HasData = true }, "chat");
        guard.Ask = (_, _) => throw new InvalidOperationException("никого нет рядом");

        using (SpendScope.Push(new SpendMeter { Unattended = true }))
        {
            await Assert.ThrowsAsync<SpendLimitException>(() => guard.CheckAsync(Key, CancellationToken.None));
        }
    }

    [Fact]
    public async Task In_a_turn_the_person_is_asked_once_even_by_parallel_requests_and_yes_lets_the_turn_through()
    {
        var (guard, ledger, _) = Guard(new SpendLimits { DayUsd = 1m });
        ledger.Record(Key.Secret, new VeniceCost { Usd = 1m, HasData = true }, "chat");
        var asked = 0;
        var answer = new TaskCompletionSource<bool>();
        guard.Ask = (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return answer.Task;
        };

        var meter = new SpendMeter();
        using (SpendScope.Push(meter))
        {
            var first = guard.CheckAsync(Key, CancellationToken.None);
            var second = guard.CheckAsync(Key, CancellationToken.None);
            answer.SetResult(true);
            await Task.WhenAll(first, second);
            await guard.CheckAsync(Key, CancellationToken.None);
        }

        Assert.Equal(1, asked);
        Assert.True(meter.LimitsWaived);
    }

    [Fact]
    public async Task No_stops_the_turn_and_later_requests_do_not_ask_again()
    {
        var (guard, ledger, _) = Guard(new SpendLimits { MonthUsd = 1m });
        ledger.Record(Key.Secret, new VeniceCost { Usd = 3m, HasData = true }, "chat");
        var asked = 0;
        guard.Ask = (_, _) =>
        {
            asked++;
            return Task.FromResult(false);
        };

        using (SpendScope.Push(new SpendMeter()))
        {
            await Assert.ThrowsAsync<SpendLimitException>(() => guard.CheckAsync(Key, CancellationToken.None));
            await Assert.ThrowsAsync<SpendLimitException>(() => guard.CheckAsync(Key, CancellationToken.None));
        }

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task The_turn_ceiling_asks_again_only_after_another_ceiling()
    {
        var (guard, _, _) = Guard(new SpendLimits { TurnUsd = 0.5m });
        var questions = new List<SpendQuestion>();
        guard.Ask = (question, _) =>
        {
            questions.Add(question);
            return Task.FromResult(true);
        };

        var meter = new SpendMeter();
        using (SpendScope.Push(meter))
        {
            meter.Add(new VeniceCost { Usd = 0.4m });
            await guard.CheckAsync(Key, CancellationToken.None);
            Assert.Empty(questions);

            meter.Add(new VeniceCost { Usd = 0.2m });
            await guard.CheckAsync(Key, CancellationToken.None);
            Assert.Single(questions);
            Assert.Equal(SpendLimitKind.Turn, questions[0].Breach.Kind);
            Assert.Equal(0.6m, questions[0].TurnSpent);

            meter.Add(new VeniceCost { Usd = 0.3m });
            await guard.CheckAsync(Key, CancellationToken.None);
            Assert.Single(questions);

            meter.Add(new VeniceCost { Usd = 0.3m });
            await guard.CheckAsync(Key, CancellationToken.None);
            Assert.Equal(2, questions.Count);
        }
    }

    [Fact]
    public async Task A_scheduled_run_stops_at_its_own_cap_even_without_limits()
    {
        var (guard, _, _) = Guard(new SpendLimits());
        var meter = new SpendMeter { Unattended = true, Cap = 0.2m };
        using (SpendScope.Push(meter))
        {
            await guard.CheckAsync(Key, CancellationToken.None);
            meter.Add(new VeniceCost { Usd = 0.25m });
            var refusal = await Assert.ThrowsAsync<SpendLimitException>(() => guard.CheckAsync(Key, CancellationToken.None));
            Assert.Equal(SpendLimitKind.Run, refusal.Breach.Kind);
        }
    }

    [Fact]
    public async Task The_warning_sounds_once_per_period()
    {
        var (guard, ledger, _) = Guard(new SpendLimits { DayUsd = 1m, WarnPercent = 80 });
        ledger.Record(Key.Secret, new VeniceCost { Usd = 0.85m, HasData = true }, "chat");
        var warned = new List<SpendBreach>();
        guard.Warned = warned.Add;

        await guard.CheckAsync(Key, CancellationToken.None);
        await guard.CheckAsync(Key, CancellationToken.None);

        Assert.Single(warned);
        Assert.Equal(SpendLimitKind.ProfileDay, warned[0].Kind);

        // Новый день или месяц — новый период, и предупреждение прозвучит снова.
        var day = new SpendBreach(SpendLimitKind.ProfileDay, 1m, 0.9m);
        var month = new SpendBreach(SpendLimitKind.KeyMonth, 1m, 0.9m);
        Assert.NotEqual(SpendRules.WarnKey(day, "fp", new DateTime(2026, 9, 30), 80), SpendRules.WarnKey(day, "fp", new DateTime(2026, 10, 1), 80));
        Assert.Equal(SpendRules.WarnKey(month, "fp", new DateTime(2026, 9, 1), 80), SpendRules.WarnKey(month, "fp", new DateTime(2026, 9, 30), 80));
        Assert.NotEqual(SpendRules.WarnKey(month, "fp", new DateTime(2026, 9, 30), 80), SpendRules.WarnKey(month, "other", new DateTime(2026, 9, 30), 80));
    }

    [Fact]
    public void The_warning_sounds_on_the_spend_that_crossed_the_share_not_on_the_next_request()
    {
        // До 1.32.0 предупреждение считалось только перед запросом, а цена записывается после
        // ответа: перейдённый последним запросом порог всплывал лишь на следующем — или никогда.
        var (guard, ledger, _) = Guard(new SpendLimits { DayUsd = 1m, WarnPercent = 80 });
        var warned = new List<SpendBreach>();
        guard.Warned = warned.Add;

        ledger.Record(Key.Secret, new VeniceCost { Usd = 0.5m, HasData = true }, "chat");
        guard.AfterSpend(Key.Secret);
        Assert.Empty(warned);

        ledger.Record(Key.Secret, new VeniceCost { Usd = 0.35m, HasData = true }, "chat");
        guard.AfterSpend(Key.Secret);

        var breach = Assert.Single(warned);
        Assert.Equal(SpendLimitKind.ProfileDay, breach.Kind);
        Assert.Equal(0.85m, breach.Spent);
    }

    [Fact]
    public void A_raised_limit_warns_again_when_its_own_share_is_reached()
    {
        // Ключ «уже предупреждали» не помнил сумму: поднятый в тот же день лимит молчал до завтра.
        var (guard, ledger, settings) = Guard(new SpendLimits { DayUsd = 1m, WarnPercent = 80 });
        var warned = new List<SpendBreach>();
        guard.Warned = warned.Add;

        ledger.Record(Key.Secret, new VeniceCost { Usd = 0.85m, HasData = true }, "chat");
        guard.AfterSpend(Key.Secret);
        settings.SpendLimits = new SpendLimits { DayUsd = 2m, WarnPercent = 80 };
        guard.AfterSpend(Key.Secret);
        Assert.Single(warned);

        ledger.Record(Key.Secret, new VeniceCost { Usd = 0.8m, HasData = true }, "chat");
        guard.AfterSpend(Key.Secret);

        Assert.Equal(2, warned.Count);
        Assert.Equal(2m, warned[1].Limit);
    }

    [Fact]
    public void Another_profile_is_warned_on_its_own()
    {
        // Набор «уже предупреждали» общий на программу: без сброса при смене профиля первый
        // глушил бы второму его предупреждение за тот же день.
        var (guard, ledger, _) = Guard(new SpendLimits { DayUsd = 1m, WarnPercent = 80 });
        var warned = new List<SpendBreach>();
        guard.Warned = warned.Add;
        ledger.Record(Key.Secret, new VeniceCost { Usd = 0.85m, HasData = true }, "chat");

        guard.AfterSpend(Key.Secret);
        guard.ForgetWarnings();
        guard.AfterSpend(Key.Secret);

        Assert.Equal(2, warned.Count);
    }

    [Fact]
    public void The_meter_counts_charges_through_the_client_and_survives_the_turn_being_hidden_from_agents()
    {
        var meter = new SpendMeter();
        using (SpendScope.Push(meter))
        using (VeniceTurnScope.Suppress())
        {
            Assert.Same(meter, SpendScope.Current);
        }

        Assert.Null(SpendScope.Current);
    }

    private sealed class Counting : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task A_refusing_gate_keeps_every_paid_request_off_the_wire()
    {
        var handler = new Counting();
        var breach = new SpendBreach(SpendLimitKind.ProfileDay, 1m, 1m);
        var options = new AgentOptions
        {
            ApiKey = "venice-secret",
            Model = "grok-4-6",
            SpendGate = (_, _) => throw new SpendLimitException(breach)
        };
        var client = new VeniceClient(new HttpClient(handler), options);

        await Assert.ThrowsAsync<SpendLimitException>(() => client.CreateChatCompletionAsync(
            "grok-4-6", [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }], null, null, new VeniceParameters()));
        await Assert.ThrowsAsync<SpendLimitException>(() => client.StreamChatCompletionAsync(
            "grok-4-6", "grok-4-6", [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }], null, null, new VeniceParameters(), null));
        await Assert.ThrowsAsync<SpendLimitException>(() => client.ScrapeUrlAsync("https://example.com"));
        await Assert.ThrowsAsync<SpendLimitException>(() => client.GenerateImageAsync("a cat"));
        await Assert.ThrowsAsync<SpendLimitException>(() => client.TranscribeAsync(
            WavFile.Build(new byte[320]), "whisper", null, new ApiCredential(LlmProvider.Venice, "venice-secret")));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Copies_of_the_options_for_agents_keep_the_gate()
    {
        Func<ApiCredential, CancellationToken, Task> gate = (_, _) => Task.CompletedTask;
        var source = new AgentOptions { ApiKey = "k", SpendGate = gate };

        var copy = AgentHost.CloneOptions(source, "grok-4-6", new ReasoningSettings());

        Assert.Same(gate, copy.SpendGate);
    }
}
