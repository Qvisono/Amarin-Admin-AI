namespace Amarin.Fox;

/// <summary>Как эмоция выглядит целиком: мордочка, значок над головой, поза и эффект.</summary>
/// <param name="Face">Мордочка из faces.fox.</param>
/// <param name="Emote">Значок в пузыре над головой: частица <c>emote-…</c>.</param>
/// <param name="Pose">Анимация, если эмоцию видно и телом; null — только лицо и значок.</param>
/// <param name="Effect">Эффект вокруг Розы (<c>tremble</c>), если он есть.</param>
internal sealed record FoxEmotion(string Face, string Emote, string? Pose = null, string? Effect = null);

/// <summary>
/// Таблица эмоций Розы. Мордочка — пара пикселей на глазу, на 48 точках её не прочесть: понятной
/// эмоцию делают значок над головой, поза и эффект вместе.
/// </summary>
/// <remarks>
/// Одна таблица и для инстинкта, и для своей модели: эмоция, которую назвала модель
/// (<see cref="FoxPrompt.Emotions"/>), выглядит так же, как та, что выбрал мозг. Новая эмоция для
/// модели — строка здесь, иначе тест не даст ей появиться в промпте.
/// </remarks>
internal static class FoxEmotions
{
    /// <summary>Сколько держится эмоция по умолчанию.</summary>
    public const int DefaultMs = 2400;

    public static readonly IReadOnlyDictionary<string, FoxEmotion> All = new Dictionary<string, FoxEmotion>(StringComparer.Ordinal)
    {
        ["happy"] = new("happy", "note", "tail"),
        ["excited"] = new("excited", "sparkle", "jump"),
        ["love"] = new("love", "heart", "nuzzle"),
        ["blush"] = new("blush", "heart"),
        ["sparkle"] = new("sparkle", "sparkle"),
        ["smart"] = new("smart", "bulb", "think"),
        ["thinking"] = new("thinking", "dots", "think"),
        ["curious"] = new("curious", "question", "sniff"),
        ["surprised"] = new("surprised", "exclaim", "surprised"),
        ["puzzled"] = new("puzzled", "question"),
        ["sad"] = new("sad", "tear", "sad"),
        ["pity"] = new("pity", "tear", "nuzzle"),
        ["bored"] = new("bored", "dots"),
        ["sleepy"] = new("sleepy", "zzz"),
        ["fear"] = new("fear", "fright", "fear", "tremble"),
        ["offended"] = new("offended", "hmph"),
        ["angry"] = new("angry", "anger", "bristle"),
        ["growl"] = new("growl", "anger", "bristle"),
        ["sly"] = new("sly", "scheme"),
        ["proud"] = new("proud", "sparkle"),
        ["shy"] = new("shy", "sweat"),
        ["relief"] = new("relief", "sigh"),
        ["lonely"] = new("lonely", "tear"),
        ["puppy"] = new("puppy", "heart", "beg")
    };

    /// <summary>
    /// Эмоция действиями для сцены. Незнакомое имя — только мордочка: модель могла назвать служебную,
    /// и она хотя бы ляжет на лицо.
    /// </summary>
    public static IEnumerable<FoxAction> Feel(string name, int ms = DefaultMs, bool pose = true)
    {
        if (!All.TryGetValue(name, out var emotion))
        {
            yield return FoxAction.Face(name, ms);
            yield break;
        }

        if (pose && emotion.Pose is { } anim)
        {
            yield return FoxAction.Anim(anim);
        }

        yield return FoxAction.Face(emotion.Face, ms);
        yield return FoxAction.Emote(emotion.Emote, Math.Min(ms, 2200));
        if (emotion.Effect is { } effect)
        {
            yield return FoxAction.Effect(effect, ms);
        }
    }
}
