using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Желания своей модели и её мысли (вторая волна, M10): модель желает, мозг разрешает; мысль —
/// редко, при галочке и только когда человек не занят.
/// </summary>
public sealed class FoxActsTests
{
    private static readonly DateTime Noon = new(2026, 10, 15, 12, 0, 0);

    /// <summary>Окно активно, ничего поверх, человек не печатает и ответа не ждёт, мысли включены.</summary>
    private static readonly FoxSituation Calm = new() { SinceTyping = TimeSpan.FromMinutes(5), Thoughts = true };

    private static FoxState Known() => new()
    {
        MetAt = Noon.AddDays(-30),
        Affection = 40,
        LastSeenAt = Noon.AddMinutes(-1),
        LastAttentionAt = Noon.AddMinutes(-1),
        Today = new FoxDay { Date = FoxDays.Key(Noon), AffectionAtStart = 40 }
    };

    private static List<FoxAction> Act(FoxState state, string act, FoxSituation? situation = null, DateTime? at = null) =>
        FoxBrain.Next(state, new FoxSignal(FoxSignalKind.MindAct, Name: act), situation ?? Calm, at ?? Noon, new Random(1));

    private static List<FoxAction> Tick(FoxState state, FoxSituation situation, DateTime at) =>
        FoxBrain.Next(state, new FoxSignal(FoxSignalKind.Tick, Value: 10), situation, at, new Random(1));

    // ---------------- желания ----------------

    [Fact]
    public void Mischief_is_done_only_where_instinct_would_allow_it_and_turns_into_a_sulk_elsewhere()
    {
        var allowed = Act(Known(), FoxActs.Mischief);
        Assert.Contains(allowed, action => action.Kind == FoxActionKind.Mischief);

        var typing = Act(Known(), FoxActs.Mischief, Calm with { SinceTyping = TimeSpan.FromSeconds(2) });
        Assert.DoesNotContain(typing, action => action.Kind == FoxActionKind.Mischief);
        Assert.Contains(typing, action => action.Kind == FoxActionKind.TurnAway);

        var off = Act(Known(), FoxActs.Mischief, Calm with { MischiefEnabled = false });
        Assert.DoesNotContain(off, action => action.Kind == FoxActionKind.Mischief);
    }

    [Fact]
    public void A_wish_is_granted_at_most_every_thirty_seconds()
    {
        var state = Known();

        Assert.NotEmpty(Act(state, FoxActs.Cuddle));
        Assert.Empty(Act(state, FoxActs.Cuddle, at: Noon.AddSeconds(10)));
        Assert.NotEmpty(Act(state, FoxActs.Cuddle, at: Noon.AddSeconds(31)));
    }

    [Theory]
    [InlineData(FoxActs.Nap)]
    [InlineData(FoxActs.Peek)]
    [InlineData(FoxActs.Play)]
    public void Hiding_napping_and_games_wait_while_he_is_busy_with_the_human(string act)
    {
        Assert.Empty(Act(Known(), act, Calm with { Holding = true }));
        Assert.NotEmpty(Act(Known(), act));
    }

    [Theory]
    [InlineData(FoxActs.Play)]
    [InlineData(FoxActs.Strut)]
    [InlineData(FoxActs.Wander)]
    public void Without_animations_nothing_that_moves_is_done(string act) =>
        Assert.Empty(Act(Known(), act, Calm with { Motion = false }));

    [Fact]
    public void An_unknown_wish_or_a_fox_away_does_nothing()
    {
        Assert.Empty(Act(Known(), "delete-chats"));
        Assert.Empty(Act(Known(), FoxActs.Cuddle, Calm with { Away = true }));
    }

    // ---------------- слова и дела (1.36.0) ----------------

    /// <summary>
    /// Модели предлагают ровно то, что мозг исполнит: желание из списка случается, вне списка — нет.
    /// До 1.36.0 модель видела всё, а мозг молча отказывал, и Роза писал о том, чего не делал.
    /// </summary>
    [Theory]
    [InlineData("calm")]
    [InlineData("chat")]
    [InlineData("holding")]
    [InlineData("still")]
    [InlineData("asleep")]
    [InlineData("no-peek")]
    [InlineData("typing")]
    public void A_wish_is_offered_exactly_when_the_brain_grants_it(string why)
    {
        var situation = why switch
        {
            "chat" => Calm with { Chatting = true },
            "holding" => Calm with { Holding = true },
            "still" => Calm with { Motion = false },
            "asleep" => Calm with { Asleep = true },
            "no-peek" => Calm with { CanPeek = false },
            "typing" => Calm with { SinceTyping = TimeSpan.FromSeconds(2) },
            _ => Calm
        };
        var offered = FoxActs.Possible(Known(), situation, Noon);

        foreach (var act in FoxActs.All)
        {
            var actions = Act(Known(), act, situation);
            var done = act == FoxActs.Mischief ? actions.Exists(action => action.Kind == FoxActionKind.Mischief) : actions.Count > 0;
            Assert.True(done == offered.Contains(act), $"{why}: {act} предложено {offered.Contains(act)}, сделано {done}");
        }
    }

    /// <summary>В переписке он слушает: прилечь и вздремнуть — на месте, а уходить с места не станет.</summary>
    [Fact]
    public void In_the_chat_he_lies_down_on_the_spot_but_does_not_walk_off()
    {
        var chatting = Calm with { Chatting = true };
        var offered = FoxActs.Possible(Known(), chatting, Noon);

        Assert.Equal([FoxActs.Sulk, FoxActs.Cuddle, FoxActs.Lie, FoxActs.Nap], offered);
        Assert.Contains(Act(Known(), FoxActs.Lie, chatting), action => action is { Kind: FoxActionKind.Do, Name: FoxWill.Lie });
        Assert.Null(FoxWill.Choose(Known(), chatting, Noon, new Random(1)));
        Assert.False(FoxMischief.Allowed(Known(), chatting, Noon));
    }

    /// <summary>«Ну держись!» без драки было бы враньём: в переписке дразнящий курсор драки не затевает.</summary>
    [Fact]
    public void A_teasing_cursor_starts_no_fight_and_no_promise_of_one_in_the_chat()
    {
        var actions = FoxBrain.Next(Known(), FoxSignalKind.Teased, Calm with { Chatting = true }, Noon, new Random(1));

        Assert.DoesNotContain(actions, action => action.Kind is FoxActionKind.Do or FoxActionKind.Say);
    }

    /// <summary>Просьба к модели несёт список того, что мозг исполнит, — тот же, что дал бы <see cref="FoxActs.Possible"/>.</summary>
    [Fact]
    public void A_talk_asks_the_mind_with_what_he_can_do_now()
    {
        var chatting = Calm with { Chatting = true };
        var state = Known();
        var expected = FoxActs.Possible(state, chatting, Noon);

        var ask = Assert.Single(FoxBrain.Next(state, new FoxSignal(FoxSignalKind.Talk, "приляг"), chatting, Noon, new Random(1)),
            action => action.Kind == FoxActionKind.AskMind);

        Assert.Equal(expected, ask.Acts);

        state.LastActAt = Noon.AddSeconds(-5);
        var later = Assert.Single(FoxBrain.Next(state, new FoxSignal(FoxSignalKind.Talk, "а теперь?"), chatting, Noon, new Random(1)),
            action => action.Kind == FoxActionKind.AskMind);
        Assert.Empty(later.Acts!);
    }

    // ---------------- мысли ----------------

    [Fact]
    public void A_thought_comes_on_a_quiet_tick_and_then_not_for_twenty_minutes()
    {
        var state = Known();

        Assert.Contains(Tick(state, Calm, Noon), action => action is { Kind: FoxActionKind.AskMind, Name: "think" });
        Assert.DoesNotContain(Tick(state, Calm, Noon.AddMinutes(10)), action => action.Kind == FoxActionKind.AskMind);
        Assert.Contains(Tick(state, Calm, Noon.AddMinutes(21)), action => action is { Kind: FoxActionKind.AskMind, Name: "think" });
        Assert.Equal(2, state.Today.Thoughts);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("typing")]
    [InlineData("covered")]
    [InlineData("inactive")]
    [InlineData("turn")]
    [InlineData("asleep")]
    [InlineData("holding")]
    [InlineData("cap")]
    public void No_thought_while_the_human_is_busy_or_the_day_is_spent(string why)
    {
        var state = Known();
        var situation = why switch
        {
            "off" => Calm with { Thoughts = false },
            "typing" => Calm with { SinceTyping = TimeSpan.FromSeconds(30) },
            "covered" => Calm with { Covered = true },
            "inactive" => Calm with { WindowActive = false },
            "turn" => Calm with { TurnRunning = true },
            "asleep" => Calm with { Asleep = true },
            "holding" => Calm with { Holding = true },
            _ => Calm
        };
        if (why == "cap")
        {
            state.Today.Thoughts = FoxActs.MaxThoughts;
        }

        Assert.False(FoxActs.MayThink(state, situation, Noon));
    }

    /// <summary>Мысль — без текста переписки: только час, нужды и события дня ключами.</summary>
    [Fact]
    public void A_thought_sees_no_chat_text()
    {
        var state = Known();
        state.Today.Messages = 4;
        state.Today.Note("praised");

        var data = FoxPrompt.ThinkUser(state, Noon);

        Assert.Contains("messages 4", data);
        Assert.Contains("praised", data);
        Assert.Contains("12:00", data);
    }
}
