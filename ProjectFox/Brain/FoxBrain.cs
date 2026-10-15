namespace Amarin.Fox;

/// <summary>
/// Мозг Розы: сигнал → перемены в памяти и действия для сцены.
/// </summary>
/// <remarks>
/// <para>
/// Чистый автомат по образцу <c>UpdateMachine</c>: ни окна, ни диска, ни сети. Время и случай —
/// параметры, поэтому любое правило проверяется тестом: «на четвёртый день без внимания требует»,
/// «синеет ниже −30 и розовеет выше −10», «шалости нет, пока человек печатает».
/// </para>
/// <para>
/// Память правится на месте — её держит <see cref="FoxStore"/> и сам решает, когда записать.
/// Что Роза делает прямо сейчас (куда смотрит, какая поза), здесь не хранится: это дело сцены.
/// </para>
/// </remarks>
internal static class FoxBrain
{
    /// <summary>Дневные потолки обращений к своей модели: реакций, реплик разговора, поисков.</summary>
    public const int MaxReactions = 100;
    public const int MaxTalks = 50;
    public const int MaxSearches = 5;
    public const int MaxEscapes = 3;

    /// <summary>Сколько раз подряд обиженный может не прийти на «Позвать»: на следующий раз приходит всегда.</summary>
    public const int MaxRefusedCalls = 2;

    /// <summary>Как часто требует внимания, когда забыт.</summary>
    public static readonly TimeSpan DemandEvery = TimeSpan.FromMinutes(8);

    public static List<FoxAction> Next(FoxState state, FoxSignal signal, FoxSituation situation, DateTime now, Random random)
    {
        var actions = new List<FoxAction>();
        var say = (string reason) => actions.Add(FoxAction.Say(FoxPhrases.Pick(reason, random)));
        var feel = (string emotion, int ms) => actions.AddRange(FoxEmotions.Feel(emotion, ms));

        if (FoxDays.Roll(state, now) && state.Unwritten.Count > 0)
        {
            actions.Add(FoxAction.Diary());
        }

        var day = state.Today;
        switch (signal.Kind)
        {
            case FoxSignalKind.Appeared:
                Greet(state, now, actions, say, random);
                break;

            case FoxSignalKind.Restored:
            case FoxSignalKind.Unlocked:
                if (state.Cold || state.Resentment >= 40)
                {
                    actions.Add(FoxAction.TurnAway());
                    say("Back.Cold");
                }
                else
                {
                    actions.Add(FoxAction.Anim("wave"));
                    actions.Add(FoxAction.Face("happy"));
                    say("Back.Warm");
                }

                state.LastSeenAt = now;
                break;

            case FoxSignalKind.Tick:
                Tick(state, situation, now, signal.Value, actions, say, random);
                break;

            case FoxSignalKind.Boop:
                day.Boops++;
                if (state.Cold)
                {
                    Bite(state, actions, say);
                    FoxFriendship.Change(state, -0.5);
                }
                else if (situation.RecentBoops >= 4)
                {
                    feel("angry", 1800);
                    actions.Add(FoxAction.Particle("vein"));
                    say("BoopAngry");
                    FoxFriendship.Resent(state, 6);
                    day.Annoyance += 0.5;
                    if (FoxMischief.Angry(state))
                    {
                        Bite(state, actions, say);
                    }
                }
                else
                {
                    actions.Add(FoxAction.Anim("sneeze"));
                    actions.Add(FoxAction.Face("presneeze", 500));
                    say("Boop");
                    FoxFriendship.Change(state, 0.2);
                }

                break;

            case FoxSignalKind.Pet:
            case FoxSignalKind.Scratch:
                Pet(state, signal.Kind == FoxSignalKind.Scratch, now, actions, say, random);
                break;

            case FoxSignalKind.TailPull:
                day.TailPulls++;
                FoxFriendship.Change(state, -3);
                FoxFriendship.Resent(state, 12);
                day.Annoyance += 1.5;
                feel("angry", 2000);
                actions.Add(FoxAction.Particle("vein"));
                say("Tail");
                if (FoxMischief.Angry(state))
                {
                    Bite(state, actions, say);
                }

                break;

            case FoxSignalKind.EarTouch:
                actions.Add(FoxAction.Anim("earflick"));
                if (random.Next(3) == 0)
                {
                    say("Ear");
                }

                break;

            case FoxSignalKind.DoubleClick:
                actions.Add(FoxAction.Anim("jump"));
                actions.Add(FoxAction.Face(state.Cold ? "sly" : "excited", 1200));
                actions.Add(FoxAction.Particle(state.Cold ? "exclaim" : "heart"));
                break;

            case FoxSignalKind.TripleClick:
                actions.Add(FoxAction.Face("dizzy", 2200));
                actions.Add(FoxAction.Particle("star", 3));
                say("Dizzy");
                break;

            case FoxSignalKind.HoverStart:
                if (state.Cold)
                {
                    feel("growl", 1500);
                    say("Growl");
                }
                else
                {
                    actions.Add(FoxAction.Anim("tail"));
                    actions.Add(FoxAction.Face("happy", 1400));
                }

                break;

            case FoxSignalKind.HoverLong:
                if (state.Cold)
                {
                    Bite(state, actions, say);
                }

                break;

            case FoxSignalKind.Teased:
                // Курсор дразнит под носом: злой кусает его, остальные затевают драку.
                if (FoxMischief.Angry(state))
                {
                    Bite(state, actions, say);
                }
                else
                {
                    actions.Add(FoxAction.Do(FoxWill.Fight));
                    say("Fight");
                }

                break;

            case FoxSignalKind.Found:
            case FoxSignalKind.Caught:
                FoxFriendship.Change(state, FoxFriendship.Gain(1.5, day.Games));
                day.Games++;
                day.Joy += 1;
                state.Boredom = Math.Max(0, state.Boredom - 40);
                Attended(state, now);
                feel("excited", 1600);
                actions.Add(FoxAction.Particle("heart", 2));
                say(signal.Kind == FoxSignalKind.Found ? "Found" : "Caught");
                break;

            case FoxSignalKind.GameIgnored:
                FoxFriendship.Resent(state, 3);
                feel("offended", 2200);
                say("FetchIgnored");
                break;

            case FoxSignalKind.Grab:
                actions.Add(FoxAction.Anim("hang"));
                actions.Add(FoxAction.Face(state.Cold ? "angry" : "surprised", 60_000));
                say(state.Cold ? "HoldCold" : "HoldWarm");
                break;

            case FoxSignalKind.Shake:
                FoxFriendship.Change(state, -1);
                FoxFriendship.Resent(state, 5);
                day.Annoyance += 0.5;
                actions.Add(FoxAction.Face("dizzy", 60_000));
                break;

            case FoxSignalKind.Thrown:
                day.Throws++;
                actions.Add(FoxAction.Anim("fly"));
                actions.Add(FoxAction.Face(state.Cold ? "angry" : signal.Value > 0.8 ? "fear" : "excited", 60_000));
                break;

            case FoxSignalKind.Landed:
                Landed(state, signal.Value, actions, say);
                break;

            case FoxSignalKind.Treat:
                if (day.Treats >= 3)
                {
                    actions.Add(FoxAction.Face("shy", 1800));
                    say("TreatFull");
                    break;
                }

                FoxFriendship.Change(state, FoxFriendship.Gain(3, day.Treats));
                FoxFriendship.Resent(state, -10);
                day.Treats++;
                day.Joy += 1;
                Attended(state, now);
                feel("love", 1800);
                actions.Add(FoxAction.Particle("heart", 2));
                say("Treat");
                break;

            case FoxSignalKind.PlayBall:
            case FoxSignalKind.PlayLaser:
                FoxFriendship.Change(state, FoxFriendship.Gain(2, day.Games));
                day.Games++;
                day.Joy += 1;
                state.Boredom = Math.Max(0, state.Boredom - 60);
                Attended(state, now);
                actions.Add(FoxAction.Face("excited", 1600));
                say("Play");
                break;

            case FoxSignalKind.Talk:
                day.Talks++;
                Attended(state, now);
                if (day.Talks <= MaxTalks)
                {
                    actions.Add(FoxAction.AskMind("talk", signal.Text));
                }
                else
                {
                    actions.Add(FoxAction.Face("sleepy", 1800));
                    say("Bored");
                }

                break;

            case FoxSignalKind.Hide:
                state.HiddenUntil = signal.Value > 0 ? now.AddHours(signal.Value) : now.Date.AddDays(1).AddHours(6);
                actions.Add(FoxAction.Anim("wave"));
                say("Hide");
                actions.Add(FoxAction.Hide());
                break;

            case FoxSignalKind.Call:
                Call(state, actions, say, feel, random);
                break;

            case FoxSignalKind.UserSent:
                UserSent(state, situation, signal.Text, actions, say);
                break;

            case FoxSignalKind.AssistantStarted:
                actions.Add(FoxAction.Anim("nod"));
                actions.Add(FoxAction.Face("smart", 2400));
                break;

            case FoxSignalKind.AssistantCompleted:
                actions.Add(FoxAction.Anim("lookback"));
                if (day.Events.Contains("scared-virus") && !day.Events.Contains("virus-clear") && FoxInstinct.SoundsClean(signal.Text))
                {
                    day.Note("virus-clear");
                    actions.Add(FoxAction.Face("relief", 2000));
                    actions.Add(FoxAction.Particle("steam"));
                    say("VirusClear");
                }
                else if (signal.Text.Contains("```", StringComparison.Ordinal))
                {
                    actions.Add(FoxAction.Face("smart", 2200));
                    say("Code");
                }
                else
                {
                    actions.Add(FoxAction.Face("happy", 1200));
                }

                break;

            case FoxSignalKind.AssistantFailed:
                actions.AddRange(FoxEmotions.Feel("pity", 2200, pose: false));
                say("Error");
                break;

            case FoxSignalKind.AssistantCancelled:
                actions.Add(FoxAction.Face("curious", 1200));
                break;

            case FoxSignalKind.LongWait:
                actions.Add(FoxAction.Face("yawn", 1300));
                say("LongWait");
                break;

            case FoxSignalKind.AgentWorking:
                actions.Add(FoxAction.Face("curious", 2000));
                say("Agent");
                break;

            case FoxSignalKind.SpendWarning:
                actions.Add(FoxAction.Item("coin"));
                feel("fear", 2400);
                say("Spend");
                break;

            case FoxSignalKind.NewChat:
                day.NewChats++;
                actions.Add(FoxAction.Face("curious", 1600));
                if (random.Next(3) == 0)
                {
                    say("NewChat");
                }

                break;

            case FoxSignalKind.ChatsDeleted:
                var count = Math.Max(1, (int)signal.Value);
                day.DeletedChats += count;
                if (count >= 3)
                {
                    actions.Add(FoxAction.Anim("surprised"));
                    actions.Add(FoxAction.Face("surprised", 2000));
                    say("DeleteMany");
                }
                else
                {
                    actions.Add(FoxAction.Anim("wave"));
                    say("DeleteOne");
                }

                break;

            case FoxSignalKind.ThemeChanged:
                actions.Add(FoxAction.Anim("lookback"));
                actions.Add(FoxAction.Face("curious", 1500));
                say("Theme");
                break;

            case FoxSignalKind.SettingsOpened:
                actions.Add(FoxAction.Go("settings"));
                if (signal.Text == "keys")
                {
                    actions.Add(FoxAction.Anim("cover"));
                    say("Keys");
                }
                else
                {
                    actions.Add(FoxAction.Face("curious", 1600));
                    if (random.Next(2) == 0)
                    {
                        say("Settings");
                    }
                }

                break;

            case FoxSignalKind.SettingsClosed:
                actions.Add(FoxAction.Go("home"));
                break;

            case FoxSignalKind.DangerAsked:
                feel("fear", 2600);
                say("Danger");
                break;

            case FoxSignalKind.MindReaction when signal.Name == "mute":
                // Своя модель не ответила на разговор: ключа нет, лимит, сеть. Без упрёков — слова убежали.
                actions.Add(FoxAction.Face("shy", 2000));
                say("Mute");
                break;

            case FoxSignalKind.MindReaction:
                FoxFriendship.Change(state, Math.Clamp(signal.Value, -3, 3));
                if (signal.Value < 0)
                {
                    day.Annoyance += -signal.Value;
                }
                else
                {
                    day.Joy += signal.Value;
                }

                if (signal.Name.Length > 0)
                {
                    // Эмоция своей модели выглядит так же, как эмоция инстинкта: значок, поза, эффект.
                    feel(signal.Name, 2600);
                }

                if (signal.Text.Length > 0)
                {
                    actions.Add(FoxAction.Line(signal.Text));
                }

                break;

            case FoxSignalKind.MindEscape:
                TryEscape(state, now, signal.Value, actions, say);
                break;

            case FoxSignalKind.MindAct:
                Act(state, situation, now, signal.Name, actions, say, feel, random);
                break;
        }

        if (FoxFriendship.UpdateEyes(state))
        {
            actions.Add(FoxAction.Eyes(state.Cold));
        }

        return actions;
    }

    /// <summary>Сколько ещё реакций своей модели можно сегодня.</summary>
    public static bool MayReact(FoxState state) => state.Today.ModelCalls < MaxReactions;

    public static bool MaySearch(FoxState state) => state.Today.Searches < MaxSearches;

    private static void Greet(FoxState state, DateTime now, List<FoxAction> actions, Action<string> say, Random random)
    {
        var missed = state.LastSeenAt is { } seen && (now.Date - seen.Date).TotalDays >= 2;
        state.LastSeenAt = now;

        if (state.MetAt is null)
        {
            state.MetAt = now;
            state.LastAttentionAt = now;
            state.Milestones.Add("first-meet");
            state.Today.Note("first-meet");
            actions.Add(FoxAction.Anim("wave"));
            actions.Add(FoxAction.Face("excited", 2400));
            actions.Add(FoxAction.Particle("heart", 3));
            say("FirstMeet");
            return;
        }

        if (state.MetAt is { } met && met.Month == now.Month && met.Day == now.Day && now.Year > met.Year &&
            !state.Milestones.Contains("year-" + now.Year))
        {
            state.Milestones.Add("year-" + now.Year);
            state.Today.Note("anniversary");
            FoxFriendship.Change(state, 5);
            actions.Add(FoxAction.Item("hat", 6000));
            actions.Add(FoxAction.Face("excited", 3000));
            actions.Add(FoxAction.Particle("spark", 3));
            say("Anniversary");
            return;
        }

        if (missed && !state.Cold)
        {
            actions.Add(FoxAction.Anim("run"));
            actions.Add(FoxAction.Face("puppy", 2600));
            actions.Add(FoxAction.Particle("heart", 2));
            say("Greet.Missed");
            return;
        }

        if (state.Resentment >= 40 || state.NeglectDays >= 2)
        {
            actions.Add(FoxAction.TurnAway());
            actions.AddRange(FoxEmotions.Feel("offended", 2600));
            say("Greet.Sulk");
            return;
        }

        if (state.Cold)
        {
            actions.Add(FoxAction.TurnAway());
            actions.Add(FoxAction.Particle("puff"));
            say("Greet.Cold");
            return;
        }

        var hour = now.Hour;
        if (hour is >= 5 and < 11 && state.Today.Note("morning"))
        {
            actions.Add(FoxAction.Anim("stretch"));
            actions.Add(FoxAction.Face("yawn", 1200));
            say("Morning");
            return;
        }

        if (hour < 5 && state.Today.Note("night"))
        {
            actions.Add(FoxAction.Face("sleepy", 2400));
            say("Night");
            return;
        }

        actions.Add(FoxAction.Anim("wave"));
        actions.Add(FoxAction.Face(random.Next(2) == 0 ? "happy" : "love", 2000));
        actions.Add(FoxAction.Particle("heart"));
        say("Greet.Warm");
    }

    private static void Tick(FoxState state, FoxSituation situation, DateTime now, double seconds, List<FoxAction> actions, Action<string> say, Random random)
    {
        var minutes = Math.Clamp(seconds, 0, 600) / 60;
        state.LastSeenAt = now;

        // Обида проходит сама: десять делений в час — сильная держится полдня.
        FoxFriendship.Resent(state, -minutes * 10 / 60);
        if (situation.WindowActive && !situation.Covered && !situation.Away)
        {
            state.Boredom = Math.Min(100, state.Boredom + minutes);
        }

        if (situation.Away)
        {
            return;
        }

        // Забыт: с четвёртого дня без внимания садится посреди поля ввода и требует.
        if (state.NeglectDays >= 4 && situation.WindowActive && !situation.Covered &&
            (state.LastDemandAt is not { } demanded || now - demanded >= DemandEvery))
        {
            state.LastDemandAt = now;
            actions.Add(FoxAction.Go("center"));
            actions.Add(FoxAction.Anim("stomp"));
            actions.Add(FoxAction.Face("puppy", 3000));
            actions.Add(FoxAction.Particle("exclaim"));
            say("Attention");
            return;
        }

        // Обиделся сильно — может убежать сам.
        if (state.Resentment >= 85 && random.Next(20) == 0)
        {
            TryEscape(state, now, 1 + random.Next(30), actions, say);
            return;
        }

        if (FoxMischief.Allowed(state, situation, now) &&
            (state.Boredom >= 70 || FoxMischief.Angry(state)) && random.Next(FoxMischief.Angry(state) ? 4 : 8) == 0)
        {
            Mischief(state, now, actions, say, random);
            return;
        }

        // Своя мысль модели — реже всего прочего и только при галочке: каждая стоит денег.
        if (FoxActs.MayThink(state, situation, now))
        {
            state.LastThoughtAt = now;
            state.Today.Thoughts++;
            actions.Add(FoxAction.AskMind("think", ""));
            return;
        }

        // Своя воля: чем заняться, пока никто не трогает. Слово к занятию — не всегда.
        if (FoxWill.Choose(state, situation, now, random) is { } activity)
        {
            actions.Add(FoxAction.Do(activity));
            if (ActivityPhrase(activity) is { } reason && random.Next(2) == 0)
            {
                say(reason);
            }

            return;
        }

        if (state.Boredom >= 60 && random.Next(12) == 0)
        {
            actions.Add(FoxAction.Face(random.Next(2) == 0 ? "bored" : "yawn", 2000));
            say("Bored");
            return;
        }

        if (now.Hour is >= 23 or < 5 && state.Today.Note("bedtime"))
        {
            actions.Add(FoxAction.Face("sleepy", 2600));
            say("Night");
            return;
        }

        if (state.Affection >= 70 && random.Next(90) == 0 && state.Today.Note("gift"))
        {
            actions.Add(FoxAction.Item("flower", 4000));
            actions.Add(FoxAction.Face("shy", 3000));
            say("Gift");
        }
    }

    private static void Pet(FoxState state, bool scratch, DateTime now, List<FoxAction> actions, Action<string> say, Random random)
    {
        var day = state.Today;
        if (state.Cold && scratch)
        {
            actions.Add(FoxAction.Face("sly", 900));
            actions.Add(FoxAction.Anim("bite"));
            say("Bite");
            FoxFriendship.Change(state, -0.3);
            return;
        }

        var wasSulking = state.Resentment >= 40;
        var gain = FoxFriendship.Gain(scratch ? 1.2 : 1.5, day.Pets) * (state.Cold ? 0.6 : 1);
        FoxFriendship.Change(state, gain);
        FoxFriendship.Resent(state, -6);
        state.Boredom = Math.Max(0, state.Boredom - 30);
        state.PetsTotal++;
        day.Pets++;
        day.Joy += 0.3;
        Attended(state, now);

        if (wasSulking)
        {
            actions.Add(FoxAction.Face("offended", 1400));
            say(state.Resentment < 40 ? "MakeUp" : "PetSulk");
            return;
        }

        actions.Add(FoxAction.Anim(scratch ? "nuzzle" : "knead"));
        actions.Add(FoxAction.Face("purr", 1800));
        actions.Add(FoxAction.Particle(random.Next(2) == 0 ? "heart" : "note"));
        if (day.Pets % 3 == 1)
        {
            say("Pet");
        }

        if (state.PetsTotal >= 100 && !state.Milestones.Contains("pets-100"))
        {
            state.Milestones.Add("pets-100");
            day.Note("pets-100");
            FoxFriendship.Change(state, 5);
            actions.Add(FoxAction.Item("hat", 5000));
            actions.Add(FoxAction.Particle("spark", 3));
            say("Pets100");
        }
    }

    private static void Landed(FoxState state, double speed, List<FoxAction> actions, Action<string> say)
    {
        actions.Add(FoxAction.Anim("crouch"));
        if (speed > 1.2)
        {
            FoxFriendship.Change(state, -1);
            FoxFriendship.Resent(state, 3);
            state.Today.Annoyance += 0.5;
            actions.Add(FoxAction.Face("dizzy", 2600));
            actions.Add(FoxAction.Particle("star", 3));
            say("Dizzy");
            return;
        }

        if (state.Cold)
        {
            FoxFriendship.Resent(state, 4);
            actions.Add(FoxAction.Face("growl", 1800));
            actions.Add(FoxAction.TurnAway());
            say("ThrowCold");
            return;
        }

        FoxFriendship.Change(state, 0.5);
        state.Today.Games++;
        state.Today.Joy += 0.5;
        actions.Add(FoxAction.Face("excited", 1600));
        actions.Add(FoxAction.Particle("heart"));
        say("ThrowWarm");
    }

    private static void UserSent(FoxState state, FoxSituation situation, string text, List<FoxAction> actions, Action<string> say)
    {
        var day = state.Today;
        day.Messages++;
        day.Characters += text.Length;
        var reading = FoxInstinct.Read(text);
        foreach (var topic in reading.Topics)
        {
            day.Topics[topic] = day.Topics.GetValueOrDefault(topic) + 1;
        }

        day.NonsenseStreak = reading.Has(FoxTopic.Nonsense) ? day.NonsenseStreak + 1 : 0;
        React(state, reading, actions, say);

        // Модель уточняет реакцию по смыслу, если человек это разрешил и дневной потолок не выбран.
        if (situation.ReadChat && MayReact(state) && text.Trim().Length >= 2)
        {
            day.ModelCalls++;
            actions.Add(FoxAction.AskMind("react", text));
        }
    }

    /// <summary>Мгновенная реакция инстинкта — по важности: страх и жалость раньше смайликов.</summary>
    private static void React(FoxState state, FoxReading reading, List<FoxAction> actions, Action<string> say)
    {
        var day = state.Today;
        var feel = (string emotion, int ms) => actions.AddRange(FoxEmotions.Feel(emotion, ms));
        if (reading.Has(FoxTopic.Virus))
        {
            day.Note("scared-virus");
            day.Annoyance += 0.2;
            feel("fear", 3000);
            actions.Add(FoxAction.Particle("drop"));

            // Сильный испуг: прячется за поле ввода и выглядывает оттуда.
            actions.Add(FoxAction.Go("peek"));
            say("Virus");
            return;
        }

        if (reading.Has(FoxTopic.Scam))
        {
            day.Note("scammed");
            feel("pity", 3000);
            say("Scam");
            return;
        }

        if (reading.Has(FoxTopic.RudeToFox))
        {
            day.Note("rude-to-me");
            FoxFriendship.Change(state, -6);
            FoxFriendship.Resent(state, 20);
            day.Annoyance += 2;
            feel("offended", 3000);
            actions.Add(FoxAction.TurnAway());
            actions.Add(FoxAction.Particle("heartbreak"));
            say("RudeToMe");
            return;
        }

        if (reading.Has(FoxTopic.Praise))
        {
            day.Note("praised");
            FoxFriendship.Change(state, 3);
            day.Joy += 1.5;
            feel("blush", 2600);
            actions.Add(FoxAction.Particle("heart", 2));
            say("Praise");
            return;
        }

        if (reading.Has(FoxTopic.Sad))
        {
            day.Note("sad");
            feel("pity", 2600);
            say("Sad");
            return;
        }

        if (reading.Has(FoxTopic.Nonsense))
        {
            day.Annoyance += 0.3;
            if (day.NonsenseStreak >= 3)
            {
                actions.Add(FoxAction.TurnAway());
                actions.Add(FoxAction.Particle("puff"));
                say("NonsenseMany");
                return;
            }

            feel("puzzled", 2400);
            say(reading.WrongLayout ? "WrongLayout" : "Nonsense");
            return;
        }

        if (reading.Has(FoxTopic.Rude))
        {
            day.Annoyance += 0.5;
            actions.AddRange(FoxEmotions.Feel("surprised", 1500, pose: false));
            actions.Add(FoxAction.Shake(400));
            say("Rude");
            return;
        }

        if (reading.Has(FoxTopic.Science) || reading.Has(FoxTopic.Code))
        {
            feel("smart", 2600);
            say(reading.Has(FoxTopic.Science) ? "Science" : "Code");
            return;
        }

        if (reading.Has(FoxTopic.Thanks) || reading.Has(FoxTopic.Happy))
        {
            day.Joy += 0.5;
            feel("excited", 1600);
            say(reading.Has(FoxTopic.Thanks) ? "Thanks" : "Happy");
            return;
        }

        if (reading.Has(FoxTopic.Long))
        {
            actions.AddRange(FoxEmotions.Feel("surprised", 1800, pose: false));
            say("Long");
            return;
        }

        // Обычное сообщение — без значка: значок над каждым сообщением перестал бы что-то значить.
        actions.Add(FoxAction.Anim("nod"));
        actions.Add(FoxAction.Face("curious", 1400));
    }

    private static void Mischief(FoxState state, DateTime now, List<FoxAction> actions, Action<string> say, Random random)
    {
        state.LastMischiefAt = now;
        state.Today.Mischief++;
        actions.Add(FoxAction.Mischief(FoxMischief.Choose(state, random)));
        say("Mischief");
    }

    /// <summary>
    /// Желание своей модели. Модель желает, мозг разрешает (см. <see cref="FoxActs"/>): нельзя —
    /// шалость оборачивается капризом, остальное просто не случается.
    /// </summary>
    private static void Act(
        FoxState state, FoxSituation situation, DateTime now, string act, List<FoxAction> actions,
        Action<string> say, Action<string, int> feel, Random random)
    {
        if (!FoxActs.All.Contains(act) || situation.Away || (state.LastActAt is { } last && now - last < FoxActs.Every))
        {
            return;
        }

        state.LastActAt = now;
        switch (act)
        {
            case FoxActs.Mischief when FoxMischief.Allowed(state, situation, now):
                Mischief(state, now, actions, say, random);
                break;
            case FoxActs.Mischief:
            case FoxActs.Sulk:
                feel("offended", 2400);
                actions.Add(FoxAction.TurnAway());
                break;
            case FoxActs.Cuddle:
                feel("love", 2000);
                actions.Add(FoxAction.Anim("nuzzle"));
                actions.Add(FoxAction.Particle("heart", 2));
                break;
            case FoxActs.Peek when !situation.Holding:
                actions.Add(FoxAction.Go("peek"));
                break;
            case FoxActs.Nap when !situation.Holding && !situation.Asleep:
                actions.Add(FoxAction.Do(FoxActs.Nap));
                break;
            case FoxActs.Play when situation.Motion && !situation.Holding:
                string[] games = [FoxWill.Fetch, FoxWill.HideSeek, FoxWill.Catch];
                actions.Add(FoxAction.Do(games[random.Next(games.Length)]));
                break;
            case FoxActs.Strut when situation.Motion && !situation.Holding:
                actions.Add(FoxAction.Do(FoxWill.Strut));
                break;
            case FoxActs.Wander when situation.Motion && !situation.Holding:
                actions.Add(FoxAction.Do(FoxWill.Wander));
                break;
        }
    }

    /// <summary>
    /// Большой кусь: рывок вперёд, пасть, зубы смыкаются над курсором (эффект <c>chomp</c>).
    /// </summary>
    private static void Bite(FoxState state, List<FoxAction> actions, Action<string> say)
    {
        state.Today.Bites++;
        actions.Add(FoxAction.Anim("lunge"));
        actions.Add(FoxAction.Face("bite", 1000));
        actions.Add(FoxAction.Emote("anger", 1200));
        actions.Add(FoxAction.Effect("chomp", 820));
        say("Bite");
    }

    /// <summary>
    /// «Позвать» убежавшего. Обиженный (холодный или с обидой от 60) может отказаться и послать
    /// человека — из-за края окна, у следа лапки. Каждый отказ смягчает обиду, второй вероятнее
    /// уступает, а третий раз он приходит всегда: вернуть его должно быть можно.
    /// </summary>
    private static void Call(FoxState state, List<FoxAction> actions, Action<string> say, Action<string, int> feel, Random random)
    {
        var offended = state.Cold || state.Resentment >= 60;
        if (offended && state.RefusedCalls < MaxRefusedCalls &&
            random.NextDouble() < (state.RefusedCalls == 0 ? 0.8 : 0.5))
        {
            state.RefusedCalls++;
            state.Today.CallRefusals++;
            state.Today.Note("told-off");
            FoxFriendship.Resent(state, -15);
            actions.Add(FoxAction.Shout(FoxPhrases.Pick("CallRefuse", random)));
            return;
        }

        state.RefusedCalls = 0;
        actions.Add(FoxAction.Go("home"));
        if (state.Resentment >= 40)
        {
            feel("offended", 2600);
            say("Return");
        }
        else
        {
            actions.Add(FoxAction.Face("happy", 1600));
            say("Back.Warm");
        }
    }

    /// <summary>Что сказать, берясь за занятие воли. Игры говорят сами — когда позовут играть.</summary>
    private static string? ActivityPhrase(string activity) => activity switch
    {
        FoxWill.Strut => "Strut",
        FoxWill.Chase => "Chase",
        FoxWill.Tease => "Tease",
        FoxWill.Pester => "Pester",
        FoxWill.HideSeek => "HideSeek",
        FoxWill.Catch => "Catch",
        _ => null
    };

    private static void TryEscape(FoxState state, DateTime now, double minutes, List<FoxAction> actions, Action<string> say)
    {
        if (state.Today.Escapes >= MaxEscapes)
        {
            actions.Add(FoxAction.TurnAway());
            actions.Add(FoxAction.Face("offended", 2600));
            return;
        }

        state.Today.Escapes++;
        state.Today.Note("escaped");
        state.RefusedCalls = 0;
        actions.Add(FoxAction.Face("offended", 1500));
        say("Escape");
        actions.Add(FoxAction.Escape(TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 30))));
    }

    private static void Attended(FoxState state, DateTime now)
    {
        state.LastAttentionAt = now;
        state.NeglectDays = 0;
    }
}
