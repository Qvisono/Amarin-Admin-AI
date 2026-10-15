using System.Text.RegularExpressions;
using Amarin.Core;
using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Мозг Розы (1.35.0): чистый автомат, поэтому каждое правило дружбы, обиды и шалостей проверяется
/// без окна, с подставленным временем и случаем.
/// </summary>
public sealed class FoxBrainTests
{
    private static readonly DateTime Noon = new(2026, 10, 13, 12, 0, 0);

    private static FoxState Known(double affection = 40) => new()
    {
        MetAt = Noon.AddDays(-30),
        Affection = affection,
        LastSeenAt = Noon.AddHours(-1),
        LastAttentionAt = Noon.AddHours(-1),
        Today = new FoxDay { Date = FoxDays.Key(Noon), AffectionAtStart = affection }
    };

    private static List<FoxAction> Run(FoxState state, FoxSignal signal, FoxSituation? situation = null, DateTime? now = null, int seed = 1) =>
        FoxBrain.Next(state, signal, situation ?? new FoxSituation(), now ?? Noon, new Random(seed));

    private static IEnumerable<string> Said(IEnumerable<FoxAction> actions) =>
        actions.Where(action => action.Kind == FoxActionKind.Say).Select(action => Regex.Replace(action.Name, @"\.\d+$", ""));

    /// <summary>Своя модель не ответила на разговор — «слова убежали», без перемен в дружбе.</summary>
    [Fact]
    public void A_mute_mind_says_so_and_changes_nothing()
    {
        var state = Known();
        var actions = Run(state, new FoxSignal(FoxSignalKind.MindReaction, Name: "mute"));

        Assert.Equal(["S.Fox.Say.Mute"], Said(actions));
        Assert.Equal(40, state.Affection);
        Assert.Equal(0, state.Resentment);
    }

    [Fact]
    public void The_first_meeting_is_remembered_and_greeted()
    {
        var state = new FoxState();
        var actions = Run(state, FoxSignalKind.Appeared);

        Assert.Equal(Noon, state.MetAt);
        Assert.Contains("first-meet", state.Milestones);
        Assert.Equal(["S.Fox.Say.FirstMeet"], Said(actions));
    }

    [Fact]
    public void A_warm_fox_waves_and_a_cold_one_turns_away()
    {
        var warm = Run(Known(50), FoxSignalKind.Appeared);
        Assert.Contains(warm, action => action.Kind == FoxActionKind.Anim && action.Name == "wave");
        Assert.Equal(["S.Fox.Say.Greet.Warm"], Said(warm));

        var coldState = Known(-60);
        coldState.Cold = true;
        var cold = Run(coldState, FoxSignalKind.Appeared);
        Assert.Contains(cold, action => action.Kind == FoxActionKind.TurnAway);
        Assert.Equal(["S.Fox.Say.Greet.Cold"], Said(cold));
    }

    /// <summary>Программу не открывали — он не обижен, а скучал.</summary>
    [Fact]
    public void After_days_away_he_missed_you_instead_of_sulking()
    {
        var state = Known();
        state.LastSeenAt = Noon.AddDays(-5);

        Assert.Equal(["S.Fox.Say.Greet.Missed"], Said(Run(state, FoxSignalKind.Appeared)));
        Assert.Equal(0, state.NeglectDays);
    }

    /// <summary>Синеют ниже −30, розовеют только выше −10: запас, чтобы не мигали туда-обратно.</summary>
    [Fact]
    public void Eyes_change_with_a_margin()
    {
        var state = Known(-29);
        Assert.False(FoxFriendship.UpdateEyes(state));

        state.Affection = -31;
        Assert.True(FoxFriendship.UpdateEyes(state));
        Assert.True(state.Cold);

        state.Affection = -20;
        Assert.False(FoxFriendship.UpdateEyes(state));
        Assert.True(state.Cold);

        state.Affection = -9;
        Assert.True(FoxFriendship.UpdateEyes(state));
        Assert.False(state.Cold);
    }

    [Fact]
    public void Pulling_the_tail_turns_his_eyes_when_affection_falls_through()
    {
        var state = Known(-28);
        var actions = Run(state, FoxSignalKind.TailPull);

        Assert.True(state.Cold);
        Assert.Contains(actions, action => action.Kind == FoxActionKind.Eyes && action.Name == "cold");
        Assert.True(state.Resentment > 0);
    }

    /// <summary>Первое поглаживание за день греет сильнее сотого: дружбу не накрутить мышью.</summary>
    [Fact]
    public void Petting_warms_less_and_less_within_a_day()
    {
        var state = Known(0);
        var gains = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var before = state.Affection;
            Run(state, FoxSignalKind.Pet);
            gains.Add(state.Affection - before);
        }

        Assert.True(gains[0] > gains[^1] * 5);
        Assert.Equal(20, state.Today.Pets);
        Assert.Equal(20, state.PetsTotal);
    }

    [Fact]
    public void A_fourth_treat_is_refused()
    {
        var state = Known();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(["S.Fox.Say.Treat"], Said(Run(state, FoxSignalKind.Treat)));
        }

        Assert.Equal(["S.Fox.Say.TreatFull"], Said(Run(state, FoxSignalKind.Treat)));
        Assert.Equal(3, state.Today.Treats);
    }

    /// <summary>
    /// Забвение считается по дням, когда программа была открыта. Не гладили два таких дня — дуется,
    /// с четвёртого садится посреди поля ввода и требует внимания.
    /// </summary>
    [Fact]
    public void Neglect_grows_on_open_days_without_attention_and_he_demands_on_the_fourth()
    {
        var state = Known();
        var now = Noon;
        for (var day = 0; day < 4; day++)
        {
            Run(state, new FoxSignal(FoxSignalKind.UserSent, "привет"), new FoxSituation { ReadChat = false }, now);
            now = now.AddDays(1);
            Run(state, new FoxSignal(FoxSignalKind.Tick, Value: 10), now: now);
        }

        Assert.Equal(4, state.NeglectDays);
        var demand = Run(state, new FoxSignal(FoxSignalKind.Tick, Value: 10), now: now.AddMinutes(10));
        Assert.Contains(demand, action => action.Kind == FoxActionKind.Go && action.Name == "center");
        Assert.Equal(["S.Fox.Say.Attention"], Said(demand));

        Run(state, FoxSignalKind.Pet, now: now.AddMinutes(11));
        Assert.Equal(0, state.NeglectDays);
    }

    [Fact]
    public void Days_with_the_program_closed_do_not_count_as_neglect()
    {
        var state = Known();
        Run(state, new FoxSignal(FoxSignalKind.Tick, Value: 10), now: Noon.AddDays(1));
        Run(state, new FoxSignal(FoxSignalKind.Tick, Value: 10), now: Noon.AddDays(2));

        Assert.Equal(0, state.NeglectDays);
        Assert.Empty(state.Unwritten);
    }

    [Fact]
    public void A_finished_day_goes_to_the_diary_and_at_most_three_wait()
    {
        var state = Known();
        var now = Noon;
        List<FoxAction> last = [];
        for (var day = 0; day < 5; day++)
        {
            Run(state, FoxSignalKind.Pet, now: now);
            now = now.AddDays(1);
            last = Run(state, new FoxSignal(FoxSignalKind.Tick, Value: 10), now: now);
        }

        Assert.Contains(last, action => action.Kind == FoxActionKind.Diary);
        Assert.Equal(FoxDays.MaxUnwritten, state.Unwritten.Count);
        Assert.Equal(FoxDays.Key(now.AddDays(-1)), state.Unwritten[^1].Date);
    }

    [Fact]
    public void A_virus_scare_is_followed_by_relief_when_the_scan_is_clean()
    {
        var state = Known();
        var scared = Run(state, new FoxSignal(FoxSignalKind.UserSent, "у меня на пк вирус?"), new FoxSituation { ReadChat = false });

        // Страх целиком: поза, дрожь с волнами, значок — и прячется за поле ввода явной командой.
        Assert.Contains(scared, action => action is { Kind: FoxActionKind.Anim, Name: "fear" });
        Assert.Contains(scared, action => action is { Kind: FoxActionKind.Effect, Name: "tremble" });
        Assert.Contains(scared, action => action is { Kind: FoxActionKind.Emote, Name: "fright" });
        Assert.Contains(scared, action => action is { Kind: FoxActionKind.Go, Name: "peek" });
        Assert.Equal(["S.Fox.Say.Virus"], Said(scared));

        var relief = Run(state, new FoxSignal(FoxSignalKind.AssistantCompleted, "Проверил: угроз не обнаружено."));
        Assert.Equal(["S.Fox.Say.VirusClear"], Said(relief));
    }

    [Fact]
    public void Being_tricked_brings_pity_and_rudeness_to_rosa_hurts()
    {
        Assert.Equal(["S.Fox.Say.Scam"], Said(Run(Known(), new FoxSignal(FoxSignalKind.UserSent, "меня обманули мошенники"), new FoxSituation { ReadChat = false })));

        var state = Known(20);
        var hurt = Run(state, new FoxSignal(FoxSignalKind.UserSent, "роза, ты тупой"), new FoxSituation { ReadChat = false });
        Assert.Equal(["S.Fox.Say.RudeToMe"], Said(hurt));
        Assert.True(state.Affection < 20);
        Assert.True(state.Resentment >= 20);
    }

    [Fact]
    public void The_third_nonsense_in_a_row_gets_a_snort()
    {
        var state = Known();
        var quiet = new FoxSituation { ReadChat = false };
        Assert.Equal(["S.Fox.Say.Nonsense"], Said(Run(state, new FoxSignal(FoxSignalKind.UserSent, "фывапролдж"), quiet)));
        Run(state, new FoxSignal(FoxSignalKind.UserSent, "asdfghjkl"), quiet);
        Assert.Equal(["S.Fox.Say.NonsenseMany"], Said(Run(state, new FoxSignal(FoxSignalKind.UserSent, "йцукенгшщ"), quiet)));

        Run(state, new FoxSignal(FoxSignalKind.UserSent, "привет, как дела?"), quiet);
        Assert.Equal(0, state.Today.NonsenseStreak);
    }

    /// <summary>Свою модель Роза спрашивает, только если чтение разрешено, и не больше сотни раз в день.</summary>
    [Fact]
    public void The_mind_is_asked_only_when_allowed_and_within_the_daily_cap()
    {
        var state = Known();
        Assert.DoesNotContain(Run(state, new FoxSignal(FoxSignalKind.UserSent, "привет"), new FoxSituation { ReadChat = false }), action => action.Kind == FoxActionKind.AskMind);

        for (var i = 0; i < FoxBrain.MaxReactions; i++)
        {
            Assert.Contains(Run(state, new FoxSignal(FoxSignalKind.UserSent, "привет")), action => action.Kind == FoxActionKind.AskMind);
        }

        Assert.DoesNotContain(Run(state, new FoxSignal(FoxSignalKind.UserSent, "привет")), action => action.Kind == FoxActionKind.AskMind);
    }

    [Fact]
    public void He_escapes_at_most_three_times_a_day()
    {
        var state = Known();
        for (var i = 0; i < FoxBrain.MaxEscapes; i++)
        {
            Assert.Contains(Run(state, new FoxSignal(FoxSignalKind.MindEscape, Value: 10)), action => action.Kind == FoxActionKind.Escape);
        }

        Assert.DoesNotContain(Run(state, new FoxSignal(FoxSignalKind.MindEscape, Value: 10)), action => action.Kind == FoxActionKind.Escape);
    }

    // ───────────── шалости ─────────────

    private static readonly FoxSituation Calm = new() { SinceTyping = TimeSpan.FromMinutes(5) };

    [Theory]
    [InlineData("disabled")]
    [InlineData("background")]
    [InlineData("covered")]
    [InlineData("text-in-input")]
    [InlineData("just-typed")]
    [InlineData("turn-running")]
    [InlineData("ui-busy")]
    [InlineData("away")]
    public void Mischief_waits_while_the_human_is_busy(string why)
    {
        var situation = why switch
        {
            "disabled" => Calm with { MischiefEnabled = false },
            "background" => Calm with { WindowActive = false },
            "covered" => Calm with { Covered = true },
            "text-in-input" => Calm with { InputEmpty = false },
            "just-typed" => Calm with { SinceTyping = TimeSpan.FromSeconds(5) },
            "turn-running" => Calm with { TurnRunning = true },
            "ui-busy" => Calm with { UiBusy = true },
            _ => Calm with { Away = true }
        };

        Assert.False(FoxMischief.Allowed(Known(), situation, Noon));
    }

    [Fact]
    public void Mischief_is_allowed_when_calm_but_not_too_often()
    {
        var state = Known();
        Assert.True(FoxMischief.Allowed(state, Calm, Noon));

        state.LastMischiefAt = Noon.AddMinutes(-5);
        Assert.False(FoxMischief.Allowed(state, Calm, Noon));

        state.Resentment = 60;
        Assert.True(FoxMischief.Allowed(state, Calm, Noon.AddMinutes(2)));
    }

    /// <summary>Делают что-то в программе ровно две шалости — третья была бы решением, а не правкой списка.</summary>
    [Fact]
    public void Exactly_two_mischiefs_act_on_the_program() =>
        Assert.Equal([FoxMischief.NewChat, FoxMischief.PushColumn], FoxMischief.Acting);

    // ───────────── связь со словарями и кадрами ─────────────

    [Fact]
    public void Every_phrase_and_level_has_its_strings()
    {
        var missing = FoxPhrases.AllKeys()
            .Concat(FoxFriendship.LevelNames.Select(name => "S.Fox.Level." + name))
            .Where(key => !StringsRu.Values.ContainsKey(key))
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// Каждая анимация, мордочка, предмет и частица, которые называет мозг, есть в кадрах: опечатка
    /// в имени молча оставила бы Розу без реакции.
    /// </summary>
    [Fact]
    public void Everything_the_brain_names_exists_in_the_sprites()
    {
        var code = File.ReadAllText(SourceTree.Find("FoxBrain.cs"));
        var sprites = Path.Combine(SourceTree.RepositoryRoot, "ProjectFox", "Sprites");
        var faces = File.ReadAllText(Path.Combine(sprites, "faces.fox"));
        var items = File.ReadAllText(Path.Combine(sprites, "items.fox"));
        var particles = File.ReadAllText(Path.Combine(sprites, "particles.fox"));
        var missing = new List<string>();

        foreach (Match match in Regex.Matches(code, @"FoxAction\.(Anim|Face|Item|Particle)\(""([a-z-]+)"""))
        {
            var name = match.Groups[2].Value;
            var found = match.Groups[1].Value switch
            {
                "Anim" => File.Exists(Path.Combine(sprites, name + ".fox")),
                "Face" => Regex.IsMatch(faces, @"(?m)^face " + Regex.Escape(name) + @"\s*$"),
                "Item" => Regex.IsMatch(items, @"(?m)^item " + Regex.Escape(name) + @"\s*$"),
                _ => Regex.IsMatch(particles, @"(?m)^particle " + Regex.Escape(name) + @"\s*$")
            };
            if (!found)
            {
                missing.Add(match.Value);
            }
        }

        Assert.Empty(missing);
    }
}
