using System.Text.RegularExpressions;
using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Крючки окна второй волны (M12): Роза замечает, что открыли другой чат, начали говорить
/// голосом, ищут по чатам, прикрепили файл, — и читает начало готового ответа инстинктом.
/// Модели ничего из этого не уходит.
/// </summary>
public sealed class FoxHooksTests
{
    private static readonly DateTime Noon = new(2026, 10, 15, 12, 0, 0);

    private static FoxState Known() => new()
    {
        MetAt = Noon.AddDays(-30),
        Affection = 40,
        LastSeenAt = Noon.AddMinutes(-1),
        Today = new FoxDay { Date = FoxDays.Key(Noon), AffectionAtStart = 40 }
    };

    private static List<FoxAction> Run(FoxSignal signal, int seed = 0) =>
        FoxBrain.Next(Known(), signal, new FoxSituation(), Noon, new Random(seed));

    private static bool Shows(List<FoxAction> actions, FoxActionKind kind, string name) =>
        actions.Exists(action => action.Kind == kind && action.Name == name);

    [Fact]
    public void A_long_chat_surprises_him_and_a_science_title_makes_him_smart()
    {
        var plain = Run(new FoxSignal(FoxSignalKind.ChatOpened, Text: "Покупки на неделю", Value: 12));
        var science = Run(new FoxSignal(FoxSignalKind.ChatOpened, Text: "Квантовая физика и формулы", Value: 12));
        var big = Run(new FoxSignal(FoxSignalKind.ChatOpened, Text: "Покупки", Value: 900));

        Assert.True(Shows(plain, FoxActionKind.Anim, "lookback"));
        Assert.True(Shows(plain, FoxActionKind.Face, "curious"));
        Assert.True(Shows(science, FoxActionKind.Face, "smart"));
        Assert.True(Shows(big, FoxActionKind.Face, "surprised"));
        Assert.DoesNotContain(plain.Concat(science).Concat(big), action => action.Kind == FoxActionKind.AskMind);
    }

    [Fact]
    public void Voice_search_and_attachments_each_get_their_gesture()
    {
        Assert.True(Shows(Run(FoxSignalKind.RecordingStarted), FoxActionKind.Anim, "earflick"));
        Assert.True(Shows(Run(FoxSignalKind.SearchTyping), FoxActionKind.Anim, "sniff"));
        Assert.True(Shows(Run(FoxSignalKind.AttachmentAdded), FoxActionKind.Anim, "sniff"));
    }

    /// <summary>Готовый ответ о науке — умная мордочка; инстинкт читает только начало длинного ответа.</summary>
    [Fact]
    public void A_science_answer_puts_on_the_smart_face()
    {
        var science = Run(new FoxSignal(FoxSignalKind.AssistantCompleted, Text: "Формула энергии фотона связывает частоту и постоянную Планка."));
        var huge = Run(new FoxSignal(FoxSignalKind.AssistantCompleted, Text: new string('а', 50_000) + " квантовая физика"));

        Assert.True(Shows(science, FoxActionKind.Face, "smart"));
        Assert.False(Shows(huge, FoxActionKind.Face, "smart"));
    }

    /// <summary>Каждый новый повод назван во всех трёх словарях — сверяет тест фраз; здесь — что поводы есть.</summary>
    [Fact]
    public void Every_new_occasion_has_its_phrases()
    {
        foreach (var occasion in (string[])["Viewer", "LongChat", "Listen", "Attachment"])
        {
            Assert.True(FoxPhrases.Variants.TryGetValue(occasion, out var count) && count > 0, occasion);
            Assert.Matches(new Regex(@"^S\.Fox\.Say\." + occasion + @"\.\d+$"), FoxPhrases.Pick(occasion, new Random(1)));
        }
    }
}
