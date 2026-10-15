using System.Text.RegularExpressions;
using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Роза D4 без окна: таблица эмоций, воля (чем заняться и когда нельзя), большой кусь, игры и отказ
/// обиженного прийти на «Позвать».
/// </summary>
public sealed class FoxWillTests
{
    private static readonly DateTime Noon = new(2026, 10, 15, 12, 0, 0);
    private static readonly FoxSprites Sprites = FoxSprites.Load();

    /// <summary>Человек не печатал минуту, окно активно, ничего поверх.</summary>
    private static readonly FoxSituation Calm = new() { SinceTyping = TimeSpan.FromMinutes(1) };

    private static FoxState Known(double affection = 40) => new()
    {
        MetAt = Noon.AddDays(-30),
        Affection = affection,
        LastSeenAt = Noon.AddHours(-1),
        LastAttentionAt = Noon.AddHours(-1),
        Today = new FoxDay { Date = FoxDays.Key(Noon), AffectionAtStart = affection }
    };

    private static List<FoxAction> Run(FoxState state, FoxSignal signal, FoxSituation? situation = null, int seed = 1) =>
        FoxBrain.Next(state, signal, situation ?? new FoxSituation(), Noon, new Random(seed));

    private static IEnumerable<string> Said(IEnumerable<FoxAction> actions) =>
        actions.Where(action => action.Kind is FoxActionKind.Say or FoxActionKind.Shout)
            .Select(action => Regex.Replace(action.Name, @"\.\d+$", ""));

    // ---------------- эмоции ----------------

    /// <summary>
    /// Каждая эмоция, которую может назвать своя модель, есть в таблице, и всё, что таблица называет,
    /// нарисовано: мордочка, значок, поза. Иначе ответ модели выглядел бы беднее инстинкта.
    /// </summary>
    [Fact]
    public void Every_emotion_the_model_may_name_is_drawn_whole()
    {
        Assert.All(FoxPrompt.Emotions, name => Assert.True(FoxEmotions.All.ContainsKey(name), name));
        foreach (var (name, emotion) in FoxEmotions.All)
        {
            Assert.True(Sprites.Faces.ContainsKey(emotion.Face), name + ": мордочка " + emotion.Face);
            Assert.True(Sprites.Particles.ContainsKey("emote-" + emotion.Emote), name + ": значок " + emotion.Emote);
            Assert.True(emotion.Pose is null || Sprites.Animations.ContainsKey(emotion.Pose), name + ": поза " + emotion.Pose);
            Assert.True(emotion.Effect is null or "tremble", name + ": эффект " + emotion.Effect);
        }
    }

    /// <summary>Страх своей модели выглядит так же, как страх инстинкта: поза, значок, дрожь с волнами.</summary>
    [Fact]
    public void A_model_reaction_is_shown_through_the_same_table()
    {
        var actions = Run(Known(), new FoxSignal(FoxSignalKind.MindReaction, Name: "fear"));

        Assert.Contains(actions, action => action is { Kind: FoxActionKind.Anim, Name: "fear" });
        Assert.Contains(actions, action => action is { Kind: FoxActionKind.Emote, Name: "fright" });
        Assert.Contains(actions, action => action is { Kind: FoxActionKind.Effect, Name: "tremble" });
    }

    // ---------------- воля ----------------

    [Fact]
    public void The_will_picks_something_when_left_alone_and_then_waits()
    {
        var state = Known();
        var first = FoxWill.Choose(state, Calm, Noon, new Random(3));

        Assert.NotNull(first);
        Assert.Null(FoxWill.Choose(state, Calm, Noon.AddSeconds(30), new Random(3)));
        Assert.NotNull(FoxWill.Choose(state, Calm, Noon.AddMinutes(3), new Random(3)));
    }

    /// <summary>Роза — не помеха работе: пока человек печатает, читает вопрос или работает в колонке, своего он не затевает.</summary>
    [Theory]
    [InlineData("typing")]
    [InlineData("covered")]
    [InlineData("inactive")]
    [InlineData("busy")]
    [InlineData("away")]
    public void The_will_waits_while_the_human_is_busy(string busy)
    {
        var situation = busy switch
        {
            "typing" => Calm with { SinceTyping = TimeSpan.FromSeconds(3) },
            "covered" => Calm with { Covered = true },
            "inactive" => Calm with { WindowActive = false },
            "busy" => Calm with { UiBusy = true },
            _ => Calm with { Away = true }
        };

        Assert.Null(FoxWill.Choose(Known(), situation, Noon, new Random(1)));
    }

    [Fact]
    public void Teasing_and_pestering_need_mischief_and_without_motion_only_the_pose_changes()
    {
        var state = Known();
        state.Boredom = 90;
        state.Resentment = 30;
        state.LastAttentionAt = Noon.AddHours(-2);

        var all = FoxWill.Weights(state, Calm with { MischiefEnabled = true }, Noon).Select(option => option.Activity).ToList();
        Assert.Contains(FoxWill.Tease, all);
        Assert.Contains(FoxWill.Pester, all);

        var calm = FoxWill.Weights(state, Calm with { MischiefEnabled = false }, Noon).Select(option => option.Activity).ToList();
        Assert.DoesNotContain(FoxWill.Tease, calm);
        Assert.DoesNotContain(FoxWill.Pester, calm);

        Assert.Equal([FoxWill.Lie], FoxWill.Weights(state, Calm with { Motion = false }, Noon).Select(option => option.Activity));
    }

    /// <summary>Уставший лежит и засыпает охотнее, чем бегает, — и без анимаций может вздремнуть.</summary>
    [Fact]
    public void A_tired_fox_rests_and_a_lively_one_plays()
    {
        var tired = Known();
        tired.Energy = 10;
        tired.Boredom = 60;
        var lively = Known();
        lively.Energy = 95;
        lively.Boredom = 60;

        double Weight(FoxState state, string activity, FoxSituation? situation = null) =>
            FoxWill.Weights(state, situation ?? Calm, Noon).Where(option => option.Activity == activity).Sum(option => option.Weight);

        Assert.True(Weight(tired, FoxWill.Lie) > Weight(lively, FoxWill.Lie));
        Assert.True(Weight(tired, FoxActs.Nap) > 0);
        Assert.Equal(0, Weight(lively, FoxActs.Nap));
        Assert.True(Weight(tired, FoxWill.Chase) < Weight(lively, FoxWill.Chase));
        Assert.True(Weight(tired, FoxActs.Nap, Calm with { Motion = false }) > 0);
    }

    /// <summary>Силы копятся во сне и тают наяву; беготня забирает больше, отдых возвращает.</summary>
    [Fact]
    public void Energy_grows_in_sleep_and_drains_awake()
    {
        var asleep = Known();
        asleep.Energy = 50;
        Run(asleep, new FoxSignal(FoxSignalKind.Tick, Value: 600), Calm with { Asleep = true, Thoughts = false, WindowActive = false });
        Assert.True(asleep.Energy > 50);

        var awake = Known();
        awake.Energy = 50;
        Run(awake, new FoxSignal(FoxSignalKind.Tick, Value: 600), Calm with { WindowActive = false });
        Assert.True(awake.Energy < 50);

        Assert.True(FoxWill.Cost(FoxWill.Chase) > FoxWill.Cost(FoxWill.Strut));
        Assert.True(FoxWill.Cost(FoxWill.Lie) < 0);
    }

    [Fact]
    public void He_does_not_pester_someone_who_just_paid_attention()
    {
        var state = Known();
        state.Boredom = 90;
        state.LastAttentionAt = Noon.AddMinutes(-2);

        Assert.DoesNotContain(FoxWill.Weights(state, Calm, Noon), option => option.Activity == FoxWill.Pester);
    }

    /// <summary>Тик мозга отдаёт занятие сцене действием <see cref="FoxActionKind.Do"/>.</summary>
    [Fact]
    public void A_quiet_tick_turns_into_an_activity()
    {
        var actions = Run(Known(), new FoxSignal(FoxSignalKind.Tick, Value: 10), Calm with { MischiefEnabled = false });

        var activity = Assert.Single(actions, action => action.Kind == FoxActionKind.Do);
        string[] known = [FoxWill.Lie, FoxWill.Wander, FoxWill.Strut, FoxWill.Chase, FoxWill.Tease, FoxWill.Pester, FoxWill.Fetch, FoxWill.HideSeek, FoxWill.Catch];
        Assert.Contains(activity.Name, known);
    }

    // ---------------- кусь, драка, игры ----------------

    [Fact]
    public void A_cold_fox_bites_a_lingering_cursor_with_big_teeth()
    {
        var state = Known(-60);
        state.Cold = true;

        var actions = Run(state, FoxSignalKind.HoverLong);

        Assert.Contains(actions, action => action is { Kind: FoxActionKind.Anim, Name: "lunge" });
        Assert.Contains(actions, action => action is { Kind: FoxActionKind.Effect, Name: "chomp" });
        Assert.Equal(1, state.Today.Bites);
    }

    [Fact]
    public void A_teasing_cursor_starts_a_fight_and_an_angry_fox_bites_it()
    {
        var warm = Run(Known(), FoxSignalKind.Teased);
        Assert.Contains(warm, action => action is { Kind: FoxActionKind.Do, Name: FoxWill.Fight });
        Assert.Equal(["S.Fox.Say.Fight"], Said(warm));

        var angry = Known();
        angry.Resentment = 60;
        Assert.Contains(Run(angry, FoxSignalKind.Teased), action => action is { Kind: FoxActionKind.Effect, Name: "chomp" });
    }

    [Theory]
    [InlineData("Found", "S.Fox.Say.Found")]
    [InlineData("Caught", "S.Fox.Say.Caught")]
    public void Winning_a_game_warms_him(string kind, string phrase)
    {
        var state = Known();
        state.Boredom = 80;

        var actions = Run(state, Enum.Parse<FoxSignalKind>(kind));

        Assert.Equal([phrase], Said(actions));
        Assert.True(state.Affection > 40);
        Assert.True(state.Boredom < 80);
        Assert.Equal(1, state.Today.Games);
    }

    // ---------------- «Позвать» ----------------

    [Fact]
    public void An_offended_fox_tells_the_human_off_but_comes_on_the_third_call()
    {
        var state = Known(-50);
        state.Cold = true;
        state.Resentment = 90;

        var calls = new List<List<FoxAction>>();
        for (var i = 0; i < 3; i++)
        {
            // Случай с этим зерном меньше половины: отказ, когда он вообще возможен.
            calls.Add(Run(state, FoxSignalKind.Call, seed: 1));
        }

        // Первые два — из-за края, у следа лапки, и не приходит.
        Assert.All(calls.Take(2), actions =>
        {
            Assert.Equal(["S.Fox.Say.CallRefuse"], Said(actions));
            Assert.DoesNotContain(actions, action => action.Kind == FoxActionKind.Go);
        });

        // Третий раз — приходит всегда, ещё обиженный.
        Assert.Contains(calls[2], action => action is { Kind: FoxActionKind.Go, Name: "home" });
        Assert.Equal(2, state.Today.CallRefusals);
        Assert.Equal(0, state.RefusedCalls);
        Assert.True(state.Resentment < 90);
    }

    [Fact]
    public void A_fox_that_is_not_offended_comes_at_once_and_warmly()
    {
        var state = Known();
        state.Resentment = 10;

        var actions = Run(state, FoxSignalKind.Call);

        Assert.Contains(actions, action => action is { Kind: FoxActionKind.Go, Name: "home" });
        Assert.Equal(["S.Fox.Say.Back.Warm"], Said(actions));
        Assert.Equal(10, state.Resentment);
    }

    [Fact]
    public void A_new_escape_starts_the_count_of_refusals_over()
    {
        var state = Known();
        state.RefusedCalls = 2;

        Run(state, new FoxSignal(FoxSignalKind.MindEscape, Value: 5));

        Assert.Equal(0, state.RefusedCalls);
    }

    /// <summary>Всё, что новые занятия и игры называют, нарисовано: позы, частицы, значки.</summary>
    [Fact]
    public void Everything_the_new_stage_code_names_is_drawn()
    {
        foreach (var anim in (string[])["fear", "bristle", "liewag", "strut", "tease", "lunge", "beg", "sneak", "lookback", "poke", "stomp"])
        {
            Assert.True(Sprites.Animations.ContainsKey(anim), anim);
        }

        foreach (var particle in (string[])["tremble-l", "tremble-r", "jaws-top", "jaws-bottom", "claw", "speed", "emote", "emote-scheme"])
        {
            Assert.True(Sprites.Particles.ContainsKey(particle), particle);
        }

        Assert.True(Sprites.Faces.ContainsKey("tease"));
    }
}
